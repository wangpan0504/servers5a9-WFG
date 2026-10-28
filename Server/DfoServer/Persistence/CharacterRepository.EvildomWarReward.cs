using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Mailbox;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository : IEvildomWarRewardStore
{
    public ImmutableArray<EvildomWarCompletion> LoadUnrewardedEvildomWarCompletions(AccountOwners.Lease owner, ushort characterId) => owner.Execute(() =>
    {
        using var db = database.Open();
        using var command = WarCommand(db, null, owner.Account, characterId, Guid.Empty);
        command.CommandText = """
            SELECT b.checkpoint_json,s.latest_instance FROM character_evildom_war_seats s
            JOIN evildom_war_battles b ON b.battle_id=s.battle_id
            JOIN characters c ON c.id=s.character_id AND c.account=s.account
            WHERE s.character_id=$id AND s.account=$account AND s.state=2
              AND NOT EXISTS(SELECT 1 FROM character_evildom_war_rewards r WHERE r.character_id=s.character_id AND r.battle_id=s.battle_id)
            ORDER BY b.deadline,s.battle_id
            """;
        using var rows = command.ExecuteReader();
        var result = ImmutableArray.CreateBuilder<EvildomWarCompletion>();
        while (rows.Read())
        {
            var checkpoint = ReadWarJson<EvildomWarCheckpoint>(rows.GetString(0));
            checkpoint.Validate();
            if (!checkpoint.View.Completed || checkpoint.Completion is not { } completion || completion.BattleId != checkpoint.View.BattleId ||
                completion.Mode != checkpoint.View.Mode || completion.Facts.SingleOrDefault(f => f.Identity.CharacterId == characterId) is not { } fact ||
                fact.Identity.InstanceId.ToString("N") != rows.GetString(1) || fact.ConfirmedDeathEvents.Count == 0)
                throw new InvalidDataException("魔界完成检查点缺少本人真实王座凭据");
            result.Add(completion);
        }
        return result.ToImmutable();
    });

    public void FreezeRecoveredEvildomWarReward(AccountOwners.Lease owner, ushort characterId,
        EvildomWarRewardSnapshot snapshot) => owner.Execute(() =>
    {
        snapshot.Validate();
        var fact = snapshot.Completion.Facts.Single(f => f.Identity.CharacterId == characterId);
        using var db = database.Open(); using var write = db.BeginWrite("CharacterRepository.FreezeRecoveredEvildomWarReward");
        using var tx = write.Transaction;
        using var command = WarCommand(db, tx, owner.Account, characterId, snapshot.Completion.BattleId);
        command.CommandText = """
            SELECT b.checkpoint_json,s.latest_instance FROM character_evildom_war_seats s
            JOIN evildom_war_battles b ON b.battle_id=s.battle_id
            JOIN characters c ON c.id=s.character_id AND c.account=s.account
            WHERE s.character_id=$id AND s.account=$account AND s.battle_id=$battle AND s.state=2
            """;
        using (var row = command.ExecuteReader())
        {
            if (!row.Read() || row.GetString(1) != fact.Identity.InstanceId.ToString("N") ||
                JsonSerializer.Serialize(ReadWarJson<EvildomWarCheckpoint>(row.GetString(0)).Completion) != JsonSerializer.Serialize(snapshot.Completion))
                throw new InvalidOperationException("魔界恢复奖励缺少原持久完成事实");
        }
        command.CommandText = "SELECT count(*) FROM character_evildom_war_rewards WHERE character_id=$id AND battle_id=$battle";
        if (command.ExecuteScalar() is 0L) InsertEvildomWarReward(db, tx, owner.Account, characterId, snapshot);
        tx.Commit(); return true;
    });

    static void InsertEvildomWarReward(SqliteConnection db, SqliteTransaction tx, string account, ushort characterId,
        EvildomWarRewardSnapshot snapshot)
    {
        var fact = snapshot.Completion.Facts.Single(f => f.Identity.CharacterId == characterId);
        using var command = WarCommand(db, tx, account, characterId, snapshot.Completion.BattleId);
        command.Parameters.AddWithValue("$instance", fact.Identity.InstanceId.ToString("N"));
        command.Parameters.AddWithValue("$attempt", fact.Identity.TaskAttempt);
        command.Parameters.AddWithValue("$dungeon", fact.DungeonId);
        command.Parameters.AddWithValue("$mode", snapshot.Completion.Mode);
        command.Parameters.AddWithValue("$created", snapshot.Completion.Created.ToUnixTimeSeconds());
        command.Parameters.AddWithValue("$reward", JsonSerializer.Serialize(snapshot));
        command.CommandText = """
            INSERT INTO character_evildom_war_rewards(character_id,account,battle_id,instance_id,task_attempt,
              dungeon_id,mode,created_at,reward_json,mail_ids,delivered)
            VALUES($id,$account,$battle,$instance,$attempt,$dungeon,$mode,$created,$reward,'[]',0)
            """;
        command.ExecuteNonQuery();
    }

    public EvildomWarRewardCommit CommitEvildomWarReward(AccountOwners.Lease owner, EvildomWarRewardPlan plan) => owner.Execute(() =>
    {
        plan.Validate();
        var snapshot = plan.Snapshot;
        string content = JsonSerializer.Serialize(snapshot);
        var fact = plan.Fact;
        using var db = database.Open();
        using (var read = db.BeginRead())
        {
            var restored = ReadEvildomWarReward(db, read, owner.Account, fact.Identity.CharacterId, snapshot, content);
            read.Commit();
            if (restored is { Delivered: true }) return new EvildomWarRewardCommit(plan, restored.MailIds, true);
        }
        // 先保存完整核心和真实终点凭据；后续交付失败仍由本人新owner恢复，不重新抽奖。
        using (var write = db.BeginWrite("CharacterRepository.FreezeEvildomWarReward"))
        using (var tx = write.Transaction)
        {
            if (ReadEvildomWarReward(db, tx, owner.Account, fact.Identity.CharacterId, snapshot, content) is null)
            {
                var identity = fact.Identity;
                var recipient = FindMailRecipientById(db, tx, identity.CharacterId);
                if (recipient is null || recipient.Account != owner.Account ||
                    ReadDungeonState(db, identity.CharacterId, tx)?.LastAttempt != identity.TaskAttempt)
                    throw new InvalidOperationException("魔界奖励角色归属或原王座尝试已变化");
                InsertEvildomWarReward(db, tx, owner.Account, identity.CharacterId, snapshot);
            }
            tx.Commit();
        }
        var result = DeliverEvildomWarReward(owner, fact.Identity.CharacterId, snapshot, content);
        return new EvildomWarRewardCommit(plan, result.MailIds, result.Delivered);
    });

    public ImmutableArray<uint> RecoverEvildomWarRewards(AccountOwners.Lease owner, ushort characterId) => owner.Execute(() =>
    {
        var pending = new List<string>();
        using (var db = database.Open())
        using (var tx = db.BeginRead())
        {
            using var command = db.CreateCommand(); command.Transaction = tx;
            command.Parameters.AddWithValue("$id", characterId); command.Parameters.AddWithValue("$account", owner.Account);
            command.CommandText = """
                SELECT r.reward_json FROM character_evildom_war_rewards r JOIN characters c ON c.id=r.character_id AND c.account=r.account
                WHERE r.character_id=$id AND r.account=$account AND r.delivered=0 ORDER BY r.created_at,r.battle_id
                """;
            using (var reader = command.ExecuteReader()) while (reader.Read()) pending.Add(reader.GetString(0));
            tx.Commit();
        }
        var recovered = ImmutableArray.CreateBuilder<uint>();
        foreach (string content in pending)
        {
            var snapshot = JsonSerializer.Deserialize<EvildomWarRewardSnapshot>(content)
                ?? throw new InvalidDataException("魔界冻结奖励快照为空");
            snapshot.Validate();
            recovered.AddRange(DeliverEvildomWarReward(owner, characterId, snapshot, content).MailIds);
        }
        return recovered.ToImmutable();
    });

    // Delivered=true表示此前已提交；本次完成时为false，调用方据此识别幂等回放。
    sealed record StoredEvildomWarReward(bool Delivered, ImmutableArray<uint> MailIds);

    StoredEvildomWarReward DeliverEvildomWarReward(AccountOwners.Lease owner, ushort characterId,
        EvildomWarRewardSnapshot snapshot, string content)
    {
        using var db = database.Open();
        using var write = db.BeginWrite("CharacterRepository.DeliverEvildomWarReward");
        using var tx = write.Transaction;
        var stored = ReadEvildomWarReward(db, tx, owner.Account, characterId, snapshot, content)
            ?? throw new InvalidDataException("魔界交付缺少已冻结奖励凭据");
        if (stored.Delivered) { tx.Commit(); return stored; }
        var recipient = FindMailRecipientById(db, tx, characterId);
        if (recipient is null || recipient.Account != owner.Account) throw new InvalidOperationException("魔界奖励收件归属变化");
        var mails = ImmutableArray.CreateBuilder<uint>();
        // 每封最多十件，所有邮件和最终回执处于同一事务，不截掉后续完整实例。
        foreach (var batch in snapshot.Items.Chunk(10))
        {
            var delivery = new MailboxDelivery($"evildom-war:{snapshot.Completion.BattleId:N}:{characterId}:{mails.Count}",
                recipient, "魔界大战", "本场通关奖励已永久保存，请领取附件。", 0, [..batch],
                snapshot.Completion.Created.ToUnixTimeSeconds());
            var (error, mailId) = DeliverMailInTransaction(db, tx, delivery);
            if (error != MailboxError.None || mailId == 0) throw new InvalidOperationException("魔界奖励邮件失败: " + error);
            mails.Add(mailId);
        }
        var result = mails.ToImmutable();
        using var command = db.CreateCommand(); command.Transaction = tx;
        command.Parameters.AddWithValue("$id", characterId);
        command.Parameters.AddWithValue("$battle", snapshot.Completion.BattleId.ToString("N"));
        command.Parameters.AddWithValue("$mails", JsonSerializer.Serialize(result));
        command.CommandText = "UPDATE character_evildom_war_rewards SET mail_ids=$mails,delivered=1 WHERE character_id=$id AND battle_id=$battle AND delivered=0";
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("魔界奖励回执状态已变化");
        tx.Commit();
        return new(false, result);
    }

    static StoredEvildomWarReward? ReadEvildomWarReward(SqliteConnection db, SqliteTransaction tx, string account,
        ushort characterId, EvildomWarRewardSnapshot snapshot, string content)
    {
        var fact = snapshot.Completion.Facts.Single(fact => fact.Identity.CharacterId == characterId);
        using var command = db.CreateCommand(); command.Transaction = tx;
        command.Parameters.AddWithValue("$id", characterId);
        command.Parameters.AddWithValue("$battle", snapshot.Completion.BattleId.ToString("N"));
        command.CommandText = """
            SELECT account,instance_id,task_attempt,dungeon_id,mode,created_at,reward_json,mail_ids,delivered
            FROM character_evildom_war_rewards WHERE character_id=$id AND battle_id=$battle
            """;
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        if (reader.GetString(0) != account || reader.GetString(1) != fact.Identity.InstanceId.ToString("N") ||
            reader.GetInt64(2) != fact.Identity.TaskAttempt || reader.GetInt64(3) != fact.DungeonId ||
            reader.GetInt32(4) != snapshot.Completion.Mode || reader.GetInt64(5) != snapshot.Completion.Created.ToUnixTimeSeconds() ||
            reader.GetString(6) != content)
            throw new InvalidDataException("同一魔界战役的终点、角色或冻结奖励发生变化");
        return new(reader.GetInt32(8) == 1, JsonSerializer.Deserialize<ImmutableArray<uint>>(reader.GetString(7)));
    }
}
