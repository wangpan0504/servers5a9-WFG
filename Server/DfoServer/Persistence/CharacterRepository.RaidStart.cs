using System.Collections.Immutable;
using Microsoft.Data.Sqlite;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Raids;
using DfoServer.GameWorld.Raids;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository : IRaidStartStore
{
    public int FiendwarGuideRoute(RaidActor actor, DateTimeOffset now) => actor.Owner.Execute(() =>
    {
        if (!actor.IsCurrent) throw new InvalidOperationException("漩涡引导原会话已变化");
        using var db = database.Open();
        using var tx = db.BeginRead();
        var quota = ReadRaidQuotaEpochs(db, tx, actor.Owner.Account, RaidKind.Fiendwar);
        using var query = db.CreateCommand(); query.Transaction = tx;
        query.CommandText = "SELECT count(*) FROM character_raid_runs WHERE character_id=$id AND account=$account AND raid_type=2 AND state=2 AND clear_week=$week AND entry_epoch=$entry";
        query.Parameters.AddWithValue("$id", actor.Character);
        query.Parameters.AddWithValue("$account", actor.Owner.Account);
        query.Parameters.AddWithValue("$week", FiendwarRewards.Cycle.Key(now));
        query.Parameters.AddWithValue("$entry", quota.Entry);
        return Math.Min(1, Convert.ToInt32(query.ExecuteScalar()));
    });

    public ImmutableArray<Character> CommitRaidStart(RaidKind kind, RaidDefinition definition, Guid instance, IReadOnlyList<RaidStartReservation> reservations) =>
        RaidOwnerGroup.Execute(kind, reservations.Select(r => (r.Actor.Owner, r.Expected.Id)).ToArray(), () =>
        {
            if (instance == Guid.Empty || definition.Type != (byte)kind ||
                definition.Resources?.Script.WaitingRoom is not { } room ||
                reservations.Count < definition.StartMinimum || reservations.Count > definition.MemberMax ||
                reservations.Any(r => !r.Actor.Session.MatchesRaidStart(r) ||
                    RaidTypes.FromChannelType(r.Actor.Session.ChannelEntry?.Type ?? -1) != (byte)kind ||
                    r.Actor.Session.ChannelEntry?.Identity != r.Actor.Channel ||
                    r.Position.Town != room.Town || r.Position.Area != room.Area))
                throw new InvalidOperationException("攻坚开战成员快照已变化");
            using var db = database.Open();
            using var write = db.BeginWrite("CharacterRepository.CommitRaidStart");
            using var tx = write.Transaction;
            if (kind == RaidKind.Luke || reservations.Any(row => row.Lifecycle is not null))
                AdmitRaidLifecycle(db, tx, instance, reservations, kind);
            if (kind == RaidKind.Fiendwar)
                AdmitFiendwarRecovery(db, tx, instance, reservations, definition.Resources.Script.EntryCost
                    ?? throw new InvalidDataException("漩涡开战缺少原门票声明"));
            var committed = ImmutableArray.CreateBuilder<Character>(reservations.Count);
            foreach (var r in reservations)
                committed.Add(SaveInTransaction(db, tx, r.Actor.Owner, r.Expected, r.Position, r.Expected.Return,
                    new(r.Inventory.Before, r.Inventory.After, kind.ToString().ToLowerInvariant() + "_raid_start:" + instance.ToString("N"))));
            tx.Commit();
            return committed.ToImmutable();
        });
    static void AdmitRaidLifecycle(SqliteConnection db, SqliteTransaction tx, Guid instance, IReadOnlyList<RaidStartReservation> rows, RaidKind kind)
    {
        var admission = rows[0].Lifecycle;
        if (admission is null || string.IsNullOrWhiteSpace(admission.Source) || admission.At.ToUnixTimeSeconds() < 0 ||
            rows.Any(row => row.Lifecycle != admission)) throw new InvalidDataException("卢克开战缺少一致生命周期来源");
        using var command = db.CreateCommand(); command.Transaction = tx;
        command.Parameters.AddWithValue("$instance", instance.ToString("N"));
        command.Parameters.AddWithValue("$type", (byte)kind);
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM character_raid_reward_receipts WHERE instance_id=$instance) OR EXISTS(SELECT 1 FROM character_raid_runs WHERE instance_id=$instance)";
        if (Convert.ToInt32(command.ExecuteScalar()) != 0) throw new InvalidDataException("新攻坚实例不可复用既有生命周期");
        command.Parameters.AddWithValue("$source", admission.Source);
        command.Parameters.AddWithValue("$at", admission.At.ToUnixTimeSeconds());
        command.Parameters.Add("$id", SqliteType.Integer); command.Parameters.Add("$account", SqliteType.Text);
        command.Parameters.Add("$entryEpoch", SqliteType.Integer); command.Parameters.Add("$rewardEpoch", SqliteType.Integer);
        if ((admission.Week is null) != (admission.WeeklyLimit is null) || admission.Week is <= 0 || admission.WeeklyLimit is <= 0)
            throw new InvalidDataException("卢克入场周资格策略不完整");
        command.Parameters.AddWithValue("$week", (object?)admission.Week ?? DBNull.Value);
        foreach (var row in rows)
        {
            var quota = ReadRaidQuotaEpochs(db, tx, row.Actor.Owner.Account, kind);
            var membership = ReadDungeonGuildMembership(db, tx, row.Expected.Id);
            command.Parameters["$id"].Value = row.Expected.Id; command.Parameters["$account"].Value = row.Actor.Owner.Account;
            command.Parameters.AddWithValue("$guild", membership.GuildId);
            command.Parameters.AddWithValue("$joined", membership.JoinedAtUnix);
            if (admission.WeeklyLimit is { } limit)
            {
                command.CommandText = "SELECT count(*) FROM character_raid_runs WHERE character_id=$id AND account=$account AND raid_type=$type AND state=2 AND clear_week=$week AND entry_epoch=$entryEpoch";
                command.Parameters["$entryEpoch"].Value = quota.Entry;
                int completed = Convert.ToInt32(command.ExecuteScalar());
                if (completed >= limit)
                    throw new RaidLifecycleRejectedException(kind.ToString().ToLowerInvariant() + "-weekly-clear-limit");
                if (admission.FiendwarGuideRoute is { } route && (kind != RaidKind.Fiendwar || route != Math.Min(1, completed)))
                    throw new RaidLifecycleRejectedException("fiendwar-guide-route-changed");
            }
            if (admission.FiendwarGuideRoute is not null)
            {
                // 名额与门票、入场生命周期共用事务；历史世代绑定保留。
                command.CommandText = "SELECT EXISTS(SELECT 1 FROM account_fiendwar_guides WHERE account=$account AND character_id=$id AND week=$week AND entry_epoch=$entryEpoch)";
                command.Parameters["$entryEpoch"].Value = quota.Entry;
                if (Convert.ToInt32(command.ExecuteScalar()) == 0)
                {
                    command.CommandText = "SELECT count(*) FROM account_fiendwar_guides WHERE account=$account AND week=$week AND entry_epoch=$entryEpoch";
                    int bound = Convert.ToInt32(command.ExecuteScalar());
                    if (bound >= 2) throw new RaidLifecycleRejectedException("fiendwar-guide-account-weekly-bind-limit");
                    command.Parameters.AddWithValue("$slot", bound);
                    command.CommandText = "INSERT INTO account_fiendwar_guides(account,slot,week,entry_epoch,character_id) VALUES($account,$slot,$week,$entryEpoch,$id)";
                    command.ExecuteNonQuery();
                    command.Parameters.RemoveAt("$slot");
                }
            }
            if (admission.DailyAccountLimit is { } daily)
            {
                if (kind is not (RaidKind.Prey or RaidKind.Fiendwar) || daily <= 0) throw new InvalidDataException("攻坚账号日入场策略不完整");
                // 沿本服业务日UTC+8 06:00；失败和中途离团也已真实入场，不退回日次数。
                long day = new DateTimeOffset(admission.At.UtcDateTime.AddHours(2).Date.AddHours(-2), TimeSpan.Zero).ToUnixTimeSeconds();
                command.Parameters.AddWithValue("$day", day);
                command.CommandText = "SELECT count(*) FROM character_raid_runs WHERE account=$account AND raid_type=$type AND admitted_at >= $day AND admitted_at < $day+86400 AND entry_epoch=$entryEpoch";
                command.Parameters["$entryEpoch"].Value = quota.Entry;
                if (Convert.ToInt32(command.ExecuteScalar()) >= daily)
                    throw new RaidLifecycleRejectedException(kind.ToString().ToLowerInvariant() + "-daily-account-enter-limit");
                if (kind == RaidKind.Fiendwar)
                {
                    command.CommandText = "SELECT count(*) FROM character_raid_runs WHERE character_id=$id AND account=$account AND raid_type=$type AND state=2 AND entry_epoch=$entryEpoch AND COALESCE(phase0_anchor,0) >= $day AND COALESCE(phase0_anchor,0) < $day+86400";
                    if (Convert.ToInt32(command.ExecuteScalar()) >= 1) throw new RaidLifecycleRejectedException("fiendwar-daily-character-clear-limit");
                }
                command.Parameters.RemoveAt("$day");
            }
            command.CommandText = """
                UPDATE character_raid_runs SET state=1 WHERE character_id=$id AND account=$account AND raid_type=$type AND state=0 AND instance_id<>$instance;
                INSERT INTO character_raid_runs(character_id,account,instance_id,raid_type,source,admitted_at,state,guild_id,guild_joined_at,entry_epoch,reward_epoch)
                SELECT id,account,$instance,$type,$source,$at,0,$guild,$joined,$entryEpoch,$rewardEpoch FROM characters WHERE id=$id AND account=$account;
                """;
            command.Parameters["$entryEpoch"].Value = quota.Entry; command.Parameters["$rewardEpoch"].Value = quota.Reward;
            command.ExecuteNonQuery();
            command.CommandText = "SELECT COUNT(*) FROM character_raid_runs WHERE character_id=$id AND account=$account AND instance_id=$instance";
            if (Convert.ToInt32(command.ExecuteScalar()) != 1) throw new InvalidDataException("卢克入场角色归属已变化");
            command.Parameters.RemoveAt("$guild");
            command.Parameters.RemoveAt("$joined");
        }
    }
}
