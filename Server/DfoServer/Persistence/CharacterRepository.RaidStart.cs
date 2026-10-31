using System.Collections.Immutable;
using Microsoft.Data.Sqlite;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Raids;
using DfoServer.GameWorld.Raids;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository : IRaidStartStore
{
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
        if ((admission.Week is null) != (admission.WeeklyLimit is null) || admission.Week is <= 0 || admission.WeeklyLimit is <= 0)
            throw new InvalidDataException("卢克入场周资格策略不完整");
        command.Parameters.AddWithValue("$week", (object?)admission.Week ?? DBNull.Value);
        foreach (var row in rows)
        {
            command.Parameters["$id"].Value = row.Expected.Id; command.Parameters["$account"].Value = row.Actor.Owner.Account;
            if (admission.WeeklyLimit is { } limit)
            {
                command.CommandText = "SELECT count(*) FROM character_raid_runs WHERE character_id=$id AND account=$account AND raid_type=$type AND state=2 AND clear_week=$week";
                if (Convert.ToInt32(command.ExecuteScalar()) >= limit)
                    throw new RaidLifecycleRejectedException(kind.ToString().ToLowerInvariant() + "-weekly-clear-limit");
            }
            if (admission.DailyAccountLimit is { } daily)
            {
                if (kind != RaidKind.Prey || daily <= 0) throw new InvalidDataException("攻坚账号日入场策略不完整");
                // 沿本服业务日UTC+8 06:00；失败和中途离团也已真实入场，不退回日次数。
                long day = new DateTimeOffset(admission.At.UtcDateTime.AddHours(2).Date.AddHours(-2), TimeSpan.Zero).ToUnixTimeSeconds();
                command.Parameters.AddWithValue("$day", day);
                command.CommandText = "SELECT count(*) FROM character_raid_runs WHERE account=$account AND raid_type=$type AND admitted_at >= $day AND admitted_at < $day+86400";
                if (Convert.ToInt32(command.ExecuteScalar()) >= daily) throw new RaidLifecycleRejectedException("prey-daily-account-enter-limit");
                command.Parameters.RemoveAt("$day");
            }
            command.CommandText = """
                UPDATE character_raid_runs SET state=1 WHERE character_id=$id AND account=$account AND raid_type=$type AND state=0 AND instance_id<>$instance;
                INSERT INTO character_raid_runs(character_id,account,instance_id,raid_type,source,admitted_at,state)
                SELECT id,account,$instance,$type,$source,$at,0 FROM characters WHERE id=$id AND account=$account;
                """;
            command.ExecuteNonQuery();
            command.CommandText = "SELECT COUNT(*) FROM character_raid_runs WHERE character_id=$id AND account=$account AND instance_id=$instance";
            if (Convert.ToInt32(command.ExecuteScalar()) != 1) throw new InvalidDataException("卢克入场角色归属已变化");
        }
    }
}
