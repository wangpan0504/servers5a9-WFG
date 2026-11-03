using System.Collections.Immutable;
using Microsoft.Data.Sqlite;
using DfoServer.Game.Raids;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    internal readonly record struct RaidQuotaEpochs(long Entry, long Reward);
    internal enum RaidQuotaAxis { Entry, Reward }
    internal readonly record struct RaidQuotaAdvance(RaidKind Type, RaidQuotaEpochs Before,
        RaidQuotaEpochs After, int ArchivedRuns, int ArchivedRewardReceipts, int ArchivedGuideBindings);

    internal static RaidQuotaEpochs ReadRaidQuotaEpochs(SqliteConnection db, SqliteTransaction? tx, string account, RaidKind type)
    {
        if (!Enum.IsDefined(type)) throw new InvalidDataException("未知团本额度类型");
        using var query = db.CreateCommand(); query.Transaction = tx;
        query.CommandText = "SELECT entry_epoch,reward_epoch FROM account_raid_quota_epochs WHERE account=$account AND raid_type=$type";
        query.Parameters.AddWithValue("$account", account); query.Parameters.AddWithValue("$type", (byte)type);
        using var reader = query.ExecuteReader();
        return reader.Read() ? new(reader.GetInt64(0), reader.GetInt64(1)) : default;
    }

    internal static RaidQuotaEpochs? ReadRaidRunQuotaEpochs(SqliteConnection db, SqliteTransaction tx,
        string account, ushort character, Guid instance, RaidKind type)
    {
        if (!Enum.IsDefined(type)) throw new InvalidDataException("未知团本额度类型");
        using var query = db.CreateCommand(); query.Transaction = tx;
        query.CommandText = "SELECT entry_epoch,reward_epoch FROM character_raid_runs WHERE account=$account AND character_id=$id AND instance_id=$instance AND raid_type=$type";
        query.Parameters.AddWithValue("$account", account); query.Parameters.AddWithValue("$id", character);
        query.Parameters.AddWithValue("$instance", instance.ToString("N")); query.Parameters.AddWithValue("$type", (byte)type);
        using var reader = query.ExecuteReader();
        return reader.Read() ? new RaidQuotaEpochs(reader.GetInt64(0), reader.GetInt64(1)) : null;
    }

    internal static ImmutableArray<RaidQuotaAdvance> AdvanceRaidQuotaEpochs(SqliteConnection db, SqliteTransaction tx,
        string account, RaidQuotaAxis axis, int fiendwarWeek)
    {
        var result = ImmutableArray.CreateBuilder<RaidQuotaAdvance>(4);
        foreach (RaidKind type in Enum.GetValues<RaidKind>().Where(type => (byte)type <= 3))
        {
            var before = ReadRaidQuotaEpochs(db, tx, account, type);
            long epoch = axis == RaidQuotaAxis.Entry ? before.Entry : before.Reward;
            int runs = Count(db, tx, "character_raid_runs", account, type, axis == RaidQuotaAxis.Entry ? "entry_epoch" : "reward_epoch", epoch);
            int receipts = axis == RaidQuotaAxis.Reward ? CountReceipts(db, tx, account, type, epoch) : 0;
            int guides = axis == RaidQuotaAxis.Entry && type == RaidKind.Fiendwar ? CountGuides(db, tx, account, epoch, fiendwarWeek) : 0;
            if (runs == 0 && receipts == 0 && guides == 0) continue;
            using (var insert = db.CreateCommand())
            {
                insert.Transaction = tx; insert.CommandText = "INSERT OR IGNORE INTO account_raid_quota_epochs(account,raid_type) VALUES($account,$type)";
                insert.Parameters.AddWithValue("$account", account); insert.Parameters.AddWithValue("$type", (byte)type); insert.ExecuteNonQuery();
            }
            long next = checked(epoch + 1);
            using var update = db.CreateCommand(); update.Transaction = tx;
            update.CommandText = axis == RaidQuotaAxis.Entry
                ? "UPDATE account_raid_quota_epochs SET entry_epoch=$next WHERE account=$account AND raid_type=$type AND entry_epoch=$old AND reward_epoch=$reward"
                : "UPDATE account_raid_quota_epochs SET reward_epoch=$next WHERE account=$account AND raid_type=$type AND entry_epoch=$entry AND reward_epoch=$old";
            update.Parameters.AddWithValue("$next", next); update.Parameters.AddWithValue("$account", account);
            update.Parameters.AddWithValue("$type", (byte)type); update.Parameters.AddWithValue("$old", epoch);
            update.Parameters.AddWithValue(axis == RaidQuotaAxis.Entry ? "$reward" : "$entry", axis == RaidQuotaAxis.Entry ? before.Reward : before.Entry);
            if (update.ExecuteNonQuery() != 1) throw new InvalidDataException("团本额度世代并发推进失败");
            var after = axis == RaidQuotaAxis.Entry ? before with { Entry = next } : before with { Reward = next };
            result.Add(new(type, before, after, runs, receipts, guides));
        }
        return result.ToImmutable();
    }

    static int Count(SqliteConnection db, SqliteTransaction tx, string table, string account, RaidKind type, string epochColumn, long epoch)
    {
        using var query = db.CreateCommand(); query.Transaction = tx;
        query.CommandText = $"SELECT count(*) FROM {table} WHERE account=$account AND raid_type=$type AND {epochColumn}=$epoch";
        query.Parameters.AddWithValue("$account", account); query.Parameters.AddWithValue("$type", (byte)type); query.Parameters.AddWithValue("$epoch", epoch);
        return Convert.ToInt32(query.ExecuteScalar());
    }
    static int CountReceipts(SqliteConnection db, SqliteTransaction tx, string account, RaidKind type, long epoch)
    {
        using var query = db.CreateCommand(); query.Transaction = tx;
        query.CommandText = "SELECT count(*) FROM character_raid_reward_receipts WHERE account=$account AND raid_type=$type AND reward_epoch=$epoch";
        query.Parameters.AddWithValue("$account", account); query.Parameters.AddWithValue("$type", (byte)type); query.Parameters.AddWithValue("$epoch", epoch);
        return Convert.ToInt32(query.ExecuteScalar());
    }
    static int CountGuides(SqliteConnection db, SqliteTransaction tx, string account, long epoch, int week)
    {
        using var query = db.CreateCommand(); query.Transaction = tx;
        query.CommandText = "SELECT count(*) FROM account_fiendwar_guides WHERE account=$account AND week=$week AND entry_epoch=$epoch";
        query.Parameters.AddWithValue("$account", account); query.Parameters.AddWithValue("$epoch", epoch);
        query.Parameters.AddWithValue("$week", week);
        return Convert.ToInt32(query.ExecuteScalar());
    }
}
