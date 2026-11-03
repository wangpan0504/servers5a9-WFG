using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Raids;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    public ImmutableArray<RaidClearCount> ReadRaidClearCounts(AccountOwners.Lease owner, ushort character,
        ImmutableArray<RaidClearPeriod> periods) => owner.Execute(() =>
    {
        if (periods.IsDefaultOrEmpty || periods.Length > 4 || periods.Select(row => row.Type).Distinct().Count() != periods.Length ||
            periods.Any(row => row.Type > 3 || row.Week <= 0 || row.DayStart < 0))
            throw new InvalidDataException("团本次数查询周期无效");
        using var db = database.Open();
        using var tx = db.BeginRead();
        using var query = db.CreateCommand(); query.Transaction = tx;
        query.Parameters.AddWithValue("$id", character); query.Parameters.AddWithValue("$account", owner.Account);
        query.CommandText = "SELECT EXISTS(SELECT 1 FROM characters WHERE id=$id AND account=$account)";
        if (Convert.ToInt32(query.ExecuteScalar()) != 1) throw new InvalidOperationException("团本次数角色不属于原账号");
        query.Parameters.Add("$type", SqliteType.Integer); query.Parameters.Add("$week", SqliteType.Integer);
        query.Parameters.Add("$entry", SqliteType.Integer);
        query.Parameters.Add("$day", SqliteType.Integer);
        query.CommandText = """
            SELECT COALESCE(SUM(CASE WHEN COALESCE(phase1_anchor,phase0_anchor) >= $day
              AND COALESCE(phase1_anchor,phase0_anchor) < $day+86400 THEN 1 ELSE 0 END),0),
              COALESCE(SUM(CASE WHEN clear_week=$week THEN 1 ELSE 0 END),0)
            FROM character_raid_runs WHERE character_id=$id AND account=$account AND raid_type=$type AND state=2
              AND entry_epoch=$entry
              AND (clear_week=$week OR COALESCE(phase1_anchor,phase0_anchor) >= $day)
            """;
        var result = ImmutableArray.CreateBuilder<RaidClearCount>(periods.Length);
        foreach (var period in periods)
        {
            query.Parameters["$type"].Value = period.Type; query.Parameters["$week"].Value = period.Week;
            query.Parameters["$day"].Value = period.DayStart;
            query.Parameters["$entry"].Value = ReadRaidQuotaEpochs(db, tx, owner.Account, (RaidKind)period.Type).Entry;
            using var reader = query.ExecuteReader();
            if (!reader.Read()) throw new InvalidDataException("团本次数聚合缺少结果");
            result.Add(new(period.Type, checked((uint)reader.GetInt64(0)), checked((uint)reader.GetInt64(1))));
        }
        return result.MoveToImmutable();
    });
}
