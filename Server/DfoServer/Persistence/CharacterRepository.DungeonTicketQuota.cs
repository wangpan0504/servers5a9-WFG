using DfoServer.Game.Dungeons;
using DfoServer.Game.Accounts;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    public int LoadAccountDungeonIncountExtra(AccountOwners.Lease owner, int dungeon, int period) => owner.Execute(() =>
    {
        using var db = database.Open();
        return ReadAccountIncountExtra(db, null, owner.Account, dungeon, period);
    });

    static int ReadAccountIncountExtra(SqliteConnection db, SqliteTransaction? tx, string account, int dungeon, int period)
    {
        using var query = db.CreateCommand(); query.Transaction = tx;
        query.CommandText = "SELECT extra FROM account_dungeon_incount_extra WHERE account=$account AND dungeon=$dungeon AND period=$period";
        query.Parameters.AddWithValue("$account", account); query.Parameters.AddWithValue("$dungeon", dungeon);
        query.Parameters.AddWithValue("$period", period);
        return query.ExecuteScalar() is long extra ? checked((int)extra) : 0;
    }

    static DungeonIncountUsage ReadRewardTicketExtra(SqliteConnection db, SqliteTransaction? tx, string account,
        ushort character, int kind, int target, DungeonIncountPeriod period)
    {
        using var query = db.CreateCommand(); query.Transaction = tx;
        query.CommandText = """
            SELECT day,daily_extra,week,weekly_extra FROM character_dungeon_reward_extra
            WHERE account=$account AND character_id=$id AND kind=$kind AND target=$target
            """;
        query.Parameters.AddWithValue("$account", account); query.Parameters.AddWithValue("$id", character);
        query.Parameters.AddWithValue("$kind", kind); query.Parameters.AddWithValue("$target", target);
        using var reader = query.ExecuteReader();
        return reader.Read() ? new(0, 0, reader.GetInt32(0) == period.Day ? reader.GetInt32(1) : 0,
            reader.GetInt32(2) == period.Week ? reader.GetInt32(3) : 0) : default;
    }
}
