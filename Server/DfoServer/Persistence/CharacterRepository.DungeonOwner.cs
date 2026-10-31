using DfoServer.Game.Accounts;
using System.Text.Json;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository : IPartyDungeonStore
{
    static CharacterDungeonState? ReadDungeonState(SqliteConnection db, ushort id, SqliteTransaction? tx)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            SELECT s.account,s.next_object_key,s.last_attempt,s.capsule_experience,s.proficiency_level,
              s.proficiency_experience,c.account FROM character_dungeon_state s
            JOIN characters c ON c.id=s.character_id WHERE s.character_id=$id
            """;
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        if (reader.GetString(0) != reader.GetString(6))
            throw new InvalidDataException("副本owner账号归属损坏");
        var state = new CharacterDungeonState(checked((uint)reader.GetInt64(1)), reader.GetInt64(2),
            checked((uint)reader.GetInt64(3)), checked((uint)reader.GetInt64(4)), checked((ulong)reader.GetInt64(5)));
        state.Validate();
        return state;
    }

    static void CreateDungeonState(SqliteConnection db, SqliteTransaction tx, string account, ushort id, CharacterDungeonState? state)
    {
        if (state is null) return;
        state.Validate();
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            INSERT INTO character_dungeon_state(character_id,account,next_object_key,last_attempt,
              capsule_experience,proficiency_level,proficiency_experience)
            VALUES($id,$account,$key,$attempt,$capsule,$level,$experience)
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$account", account);
        command.Parameters.AddWithValue("$key", state.NextObjectKey);
        command.Parameters.AddWithValue("$attempt", state.LastAttempt);
        command.Parameters.AddWithValue("$capsule", state.GrowthCapsuleExperience);
        command.Parameters.AddWithValue("$level", state.ProficiencyLevel);
        command.Parameters.AddWithValue("$experience", checked((long)state.ProficiencyExperience));
        command.ExecuteNonQuery();
    }

    // 熟练度与角色经验在同一事务提交；用旧值做乐观校验，冲突时整笔回滚而不是覆盖。
    static void WriteProficiency(SqliteConnection db, SqliteTransaction tx, string account, ushort id,
        ProficiencyProgress before, ProficiencyProgress after)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$account", account);
        command.Parameters.AddWithValue("$level", after.Level);
        command.Parameters.AddWithValue("$experience", checked((long)after.Experience));
        command.Parameters.AddWithValue("$oldLevel", before.Level);
        command.Parameters.AddWithValue("$oldExperience", checked((long)before.Experience));
        command.CommandText = """
            UPDATE character_dungeon_state SET proficiency_level=$level,proficiency_experience=$experience
            WHERE character_id=$id AND account=$account AND proficiency_level=$oldLevel AND proficiency_experience=$oldExperience
            """;
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("熟练度状态已变化");
    }

    public Character ReserveDungeonAttempt(AccountOwners.Lease owner, Character expected, uint nextObjectKey,
        WeeklyDungeonEntry? weekly = null) => ReserveDungeonEntry(owner, expected, nextObjectKey, weekly).Character;

    public DungeonEntryCommit ReserveDungeonEntry(AccountOwners.Lease owner, Character expected, uint nextObjectKey,
        WeeklyDungeonEntry? weekly = null) => owner.Execute(() =>
    {
        var state = ReservationState(expected, nextObjectKey);
        using var db = database.Open();
        using var write = db.BeginWrite("CharacterRepository.ReserveDungeonAttempt");
        using var tx = write.Transaction;
        ReserveInTransaction(db, tx, owner, expected, expected.Revision, state, nextObjectKey, weekly: weekly);
        var committed = Require(db, tx, owner.Account, expected.Name);
        var membership = ReadDungeonGuildMembership(db, tx, expected.Id);
        tx.Commit();
        return new DungeonEntryCommit(committed, null) { GuildMembership = membership };
    });

    // #510/#511 入场票、入场一次性疲劳与尝试号预留同一写事务：任一步失败整笔回滚，重复请求或提交失败都不会只扣一半。
    public DungeonEntryCommit CommitDungeonEntry(AccountOwners.Lease owner, Character expected, uint nextObjectKey, DungeonEntryCharge charge) => owner.Execute(() =>
    {
        if (charge.Marriage is not null || charge.DungeonId == MarriageDungeonAdmission.DungeonId)
            throw new InvalidOperationException("夫妻副本只能通过双方原子入场");
        if (charge.IsEmpty) throw new InvalidDataException("单人无费用入场使用原对象预留合同");
        using var db = database.Open();
        using var write = db.BeginWrite("CharacterRepository.CommitDungeonEntry");
        using var tx = write.Transaction;
        var committed = CommitDungeonEntryInTransaction(db, tx, owner, expected, nextObjectKey, charge, out var cachedFatigue);
        tx.Commit();
        InstallEntryFatigue(owner, charge, cachedFatigue);
        return committed;
    });

    DungeonEntryCommit CommitDungeonEntryInTransaction(SqliteConnection db, SqliteTransaction tx,
        AccountOwners.Lease owner, Character expected, uint nextObjectKey, DungeonEntryCharge charge, out DungeonTransientFatigue? cachedFatigue,
        DungeonIncountUsage? partyCounted = null)
    {
        var state = ReservationState(expected, nextObjectKey);
        var identity = charge.Identity;
        if (identity.CharacterId != expected.Id || identity.InstanceId == Guid.Empty || identity.Epoch <= 0 ||
            identity.TaskAttempt != state.LastAttempt + 1 || charge.MapId <= 0)
            throw new InvalidDataException("入场费用需要本次待安装实例的身份");
        AdmitOrdinaryLuke(db, tx, owner, expected, charge);
        AdmitPreyNormal(db, tx, owner, expected, charge);
        AdmitAntonNormal(db, tx, owner, expected, charge);
        AdmitDazzlementTower(db, tx, owner, expected, charge);
        AdmitGuildDungeon(db, tx, expected.Id, charge);
        CharacterFatigueState? fatigueBefore = null, fatigueAfter = null;
        if (charge.Fatigue > 0)
        {
            fatigueBefore = expected.Fatigue ?? throw new InvalidOperationException("入场疲劳需要已初始化的疲劳owner");
            fatigueAfter = fatigueBefore.ConsumeAtEntry(charge.Fatigue);
        }
        DungeonIncountCommit? counted = null;
        if (charge.Dimension is { } dimension)
        {
            var wantedUsage = partyCounted is null ? dimension.DailyBefore :
                new DungeonIncountUsage(checked(dimension.DailyBefore.DailyUsed + 1), checked(dimension.DailyBefore.WeeklyUsed + 1));
            if (dimension.DungeonId != charge.DungeonId ||
                partyCounted is { } actualUsage && actualUsage != wantedUsage ||
                ReadDungeonIncount(db, tx, expected.Id, owner.Account, dimension.Catalog.DailyRow.CountKey, dimension.Period) != wantedUsage)
                throw new InvalidOperationException("异界入场计数已变化，不能提交旧抽样");
            if (dimension.HiddenCharge is { } hidden)
            {
                if (ReadDungeonIncount(db, tx, expected.Id, owner.Account, hidden.Dungeon, hidden.Period) != hidden.ExpectedUsage ||
                    CommitDungeonIncountInTransaction(db, tx, owner, expected, hidden.Dungeon, hidden.Period, hidden.Row, null, [], true) is null)
                    throw new InvalidOperationException("异界隐藏领主周额度已变化");
            }
        }
        if (charge.Incount is { } incount)
            counted = CommitDungeonIncountInTransaction(db, tx, owner, expected,
                incount.Dungeon, incount.Period, incount.Row, null, incount.Account, incount.Increment) ??
                throw new InvalidOperationException("入场次数在提交前已用尽");
        long revision = charge.InventoryPayment is { } items ? CommitInventoryInTransaction(db, tx, owner, expected, items).Revision : expected.Revision;
        ReserveInTransaction(db, tx, owner, expected, revision, state, nextObjectKey, fatigueBefore, charge.Weekly);
        CommitTagEvildomAdmission(db, tx, owner, expected, charge, checked(revision + 1));
        CommitImperialArenaAdmission(db, tx, owner, expected, charge, checked(revision + 1));
        CommitHopeAdmission(db, tx, owner, expected, charge, checked(revision + 1));
        CommitGriefEntry(db, tx, owner, expected, charge, checked(revision + 1));
        WriteSpecialEntry(db, tx, owner, expected, charge, checked(revision + 1));
        if (fatigueBefore is not null && fatigueAfter is not null)
        {
            using var command = db.CreateCommand();
            command.Transaction = tx;
            command.Parameters.AddWithValue("$id", expected.Id);
            command.Parameters.AddWithValue("$account", owner.Account);
            command.Parameters.AddWithValue("$used", fatigueAfter.Used);
            command.Parameters.AddWithValue("$remaining", fatigueAfter.Remaining);
            command.CommandText = "UPDATE character_fatigue SET used=$used,remaining=$remaining WHERE character_id=$id AND account=$account";
            if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("入场疲劳缺少原疲劳owner");
        }
        var committed = Require(db, tx, owner.Account, expected.Name);
        cachedFatigue = fatigueBefore is not null && fatigueAfter is not null
            ? new(charge.Hope is null ? CharacterFatigueState.StartDungeonRuleId : "operation-hope-first-area-v1", revision, fatigueBefore, fatigueAfter) : null;
        return new DungeonEntryCommit(committed, charge.Incount?.Row is not null ? counted?.Usage : null)
            { GuildMembership = ReadDungeonGuildMembership(db, tx, expected.Id) };
    }

    static DfoServer.Game.Guild.GuildDungeonMembership ReadDungeonGuildMembership(SqliteConnection db, SqliteTransaction tx, ushort id)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "SELECT guild_id,joined_at_unix FROM guild_members WHERE character_id=$id";
        command.Parameters.AddWithValue("$id", id);
        using var row = command.ExecuteReader();
        return row.Read() ? new(id, checked((uint)row.GetInt64(0)), row.GetInt64(1)) : new(id, 0, 0);
    }

    public PartyDungeonEntryCommit[] CommitPartyDungeonEntry(IReadOnlyList<PartyDungeonReservation> reservations)
    {
        if (reservations.Count is < 1 or > 4 || reservations.Select(r => r.Charge.Identity.InstanceId).Distinct().Count() != 1)
            throw new InvalidDataException("组队入场需要同一实例的一至四个当前成员");
        return AccountOwners.Lease.ExecuteGroup(reservations.Select(r => (r.Owner, r.Expected.Id)).ToArray(), () =>
        {
            using var db = database.Open();
            using var write = db.BeginWrite("CharacterRepository.CommitPartyDungeonEntry");
            using var tx = write.Transaction;
            int? lukeDay = reservations[0].Charge.OrdinaryLukeEntry?.OrdinaryLukeDay;
            if (reservations.Any(r => r.Charge.OrdinaryLukeEntry?.OrdinaryLukeDay != lukeDay))
                throw new InvalidDataException("组队普通卢克成员必须冻结同一入场业务日");
            var anton = reservations[0].Charge.AntonNormalEntry?.AntonNormal;
            if (reservations.Any(r => r.Charge.AntonNormalEntry?.AntonNormal != anton))
                throw new InvalidDataException("组队普通安徒恩成员必须冻结同一业务日、分支与资源");
            var prey = reservations[0].Charge.PreyNormalEntry?.PreyNormal;
            if (reservations.Any(r => r.Charge.PreyNormalEntry?.PreyNormal != prey))
                throw new InvalidDataException("组队普通普雷成员必须冻结同一业务日、分支与资源");
            if (reservations.Any(r => r.Charge.Marriage is not null || r.Charge.DungeonId == MarriageDungeonAdmission.DungeonId))
            {
                var admission = reservations[0].Charge.Marriage;
                var members = reservations.Select(row => row.Expected.Id).ToArray();
                if (admission is null || reservations.Count != 2 || reservations.Any(row => row.Charge.Marriage != admission ||
                    row.Charge.DungeonId != MarriageDungeonAdmission.DungeonId || !admission.HasTicketPayment(row.Charge.Items) ||
                    row.Charge.Fatigue != 0) || members.Any(member => !admission.Matches(ReadMarriage(db, tx, member), members)))
                    throw new InvalidOperationException("夫妻副本入场关系、成员或版本已变化");
            }
            var result = new PartyDungeonEntryCommit[reservations.Count];
            var memberships = new DfoServer.Game.Guild.GuildDungeonMembership[reservations.Count];
            var fatigue = new DungeonTransientFatigue?[reservations.Count];
            for (int i = 0; i < reservations.Count; i++)
            {
                var r = reservations[i];
                var count = r.Incount;
                var counted = count is null ? null : CommitDungeonIncountInTransaction(db, tx, r.Owner, r.Expected,
                    count.Dungeon, count.Period, count.Row, null, count.Account, count.Increment, check: true);
                if (count is not null && counted is null) throw new InvalidOperationException("组队成员入场次数已变化");
                var character = CommitDungeonEntryInTransaction(db, tx, r.Owner, r.Expected, r.NextObjectKey, r.Charge, out fatigue[i], counted?.Usage);
                memberships[i] = character.GuildMembership!;
                result[i] = new(character.Character, r.Charge.Dimension is null && counted is not null && count is { Increment: true, Row: { } row }
                    ? [.. DungeonIncountPolicy.Views(row, counted.Usage)] : []);
            }
            var frozenMemberships = System.Collections.Immutable.ImmutableArray.CreateRange(memberships);
            CommitEvildomWarEntryInTransaction(db, tx, reservations, result);
            for (int i = 0; i < result.Length; i++) result[i] = result[i] with { GuildMemberships = frozenMemberships };
            tx.Commit();
            for (int i = 0; i < reservations.Count; i++)
                InstallEntryFatigue(reservations[i].Owner, reservations[i].Charge, fatigue[i]);
            return result;
        });
    }

    public Character[] CommitPartyDungeonExit(IReadOnlyList<PartyDungeonExitReservation> reservations) =>
        AccountOwners.Lease.ExecuteGroup(reservations.Select(r => (r.Owner, r.Expected.Id)).ToArray(), () =>
        {
            using var db = database.Open();
            if (reservations.All(r => !r.Changed)) return Validate(null);
            using var write = db.BeginWrite("CharacterRepository.CommitPartyDungeonExit");
            using var tx = write.Transaction;
            Validate(tx);
            var result = reservations.Select(r =>
            {
                var character = r.Position != r.Expected.Position || r.Inventory is not null
                    ? SaveInTransaction(db,tx,r.Owner,r.Expected,r.Position,r.Expected.Return,r.Inventory) : r.Expected;
                return r.Hope is { } hope
                    ? ClearOperationHopeAcceptancesInTransaction(db,tx,r.Owner,character,hope.Catalog,[hope.Acceptance]) : character;
            }).ToArray();
            tx.Commit();
            return result;

            Character[] Validate(SqliteTransaction? tx)
            {
                foreach (var r in reservations)
                {
                    using var command = db.CreateCommand();
                    command.Transaction = tx;
                    command.CommandText = "SELECT revision FROM characters WHERE id=$id AND account=$account";
                    command.Parameters.AddWithValue("$id", r.Expected.Id);
                    command.Parameters.AddWithValue("$account", r.Owner.Account);
                    if (command.ExecuteScalar() is not long revision || revision != r.Expected.Revision)
                        throw new InvalidOperationException("组队退场前角色revision已变化");
                }
                return reservations.Select(r => r.Expected).ToArray();
            }
        });

    static void InstallEntryFatigue(AccountOwners.Lease owner, DungeonEntryCharge charge, DungeonTransientFatigue? fatigue)
    {
        if (fatigue is null) return;
        fatigue.Committed = true;
        var receipts = DungeonTransientReceipts.Get(owner, charge.Identity);
        if (charge.Hope is { } hope)
            receipts.HopeAreaFatigue[hope.Catalog.Maps[charge.MapId].AreaId] = fatigue;
        else receipts.Fatigue[charge.MapId] = fatigue;
    }

    static CharacterDungeonState ReservationState(Character expected, uint nextObjectKey)
    {
        var state = expected.DungeonState ?? throw new InvalidOperationException("副本owner未初始化");
        // 本入口只预留新尝试，允许新实例复用旧数值；同实例的丢弃仍走独立的单调游标提交。
        if (nextObjectKey is < 1 or > 65536 || state.LastAttempt == long.MaxValue)
            throw new InvalidDataException("副本对象key或尝试号耗尽");
        return state;
    }

    // 只参与调用方事务：复核准入前提（revision 允许已被同事务的入场票提交推进到 revision），推进 revision 与尝试号，
    // 并按本次入场累加冒险团每周地下城列；任一步失败整笔回滚，重复提交不会多计。
    void ReserveInTransaction(SqliteConnection db, SqliteTransaction tx, AccountOwners.Lease owner, Character expected, long revision,
        CharacterDungeonState state, uint nextObjectKey, CharacterFatigueState? fatigue = null, WeeklyDungeonEntry? weekly = null)
    {
        var current = Require(db, tx, owner.Account, expected.Name);
        if (fatigue is not null && current.Fatigue != fatigue)
            throw new InvalidOperationException("入场疲劳的原owner已变化");
        if (current.Id != expected.Id || current.Revision != revision || current.DungeonState != state ||
            current.Job != expected.Job || current.GrowType != expected.GrowType || current.Level != expected.Level ||
            current.Position != expected.Position || !current.ActiveMainQuests.SequenceEqual(expected.ActiveMainQuests) ||
            !current.CompletedQuests.SequenceEqual(expected.CompletedQuests) || !SameLearning(current.Learning, expected.Learning))
            throw new InvalidOperationException("副本准入的角色或任务前提已变化");
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            UPDATE characters SET revision=revision+1 WHERE id=$id AND account=$account AND revision=$revision
            """;
        command.Parameters.AddWithValue("$id", expected.Id);
        command.Parameters.AddWithValue("$account", owner.Account);
        command.Parameters.AddWithValue("$revision", revision);
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("副本准入revision冲突");
        command.Parameters.AddWithValue("$key", nextObjectKey);
        command.Parameters.AddWithValue("$oldKey", state.NextObjectKey);
        command.Parameters.AddWithValue("$oldAttempt", state.LastAttempt);
        command.CommandText = """
            UPDATE character_dungeon_state SET next_object_key=$key,last_attempt=last_attempt+1
            WHERE character_id=$id AND account=$account AND next_object_key=$oldKey AND last_attempt=$oldAttempt
            """;
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("副本身份预留冲突");
        if (weekly is null) return;
        command.Parameters.AddWithValue("$week", weekly.Week);
        command.Parameters.AddWithValue("$slot", weekly.Slot);
        command.CommandText = """
            INSERT INTO character_weekly_dungeon(character_id,account,week,slot,used) VALUES($id,$account,$week,$slot,1)
            ON CONFLICT(character_id,week,slot) DO UPDATE SET used=used+1
            """;
        command.ExecuteNonQuery();
    }
}
