using System.Collections.Immutable;
using DfoServer.Game.Inventory.Models;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    static ImmutableArray<ConsumableState> ReadConsumableStates(SqliteConnection db, SqliteTransaction? tx, ushort id, string account)
    {
        using var command = db.CreateCommand(); command.Transaction = tx;
        command.CommandText = "SELECT kind,item_id,until_ms,maintenance,cool_group,account FROM character_consumable_states WHERE character_id=$id ORDER BY kind,item_id";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        var rows = ImmutableArray.CreateBuilder<ConsumableState>();
        while (reader.Read())
        {
            if (reader.GetString(5) != account) throw new InvalidDataException("消耗品持续状态账号归属损坏");
            if (rows.Count >= 2 * byte.MaxValue) throw new InvalidDataException("消耗品持续状态超过协议容量");
            rows.Add(new((ConsumableStateKind)reader.GetInt32(0), reader.GetInt32(1), reader.GetInt64(2),
                checked((byte)reader.GetInt32(3)), reader.GetInt32(4)));
        }
        return rows.ToImmutable();
    }

    static void WriteConsumableStates(SqliteConnection db, SqliteTransaction tx, string account, ushort id,
        ImmutableArray<ConsumableState> rows)
    {
        using var command = db.CreateCommand(); command.Transaction = tx;
        command.CommandText = "DELETE FROM character_consumable_states WHERE character_id=$id AND account=$account";
        command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$account", account);
        command.ExecuteNonQuery();
        command.CommandText = "INSERT INTO character_consumable_states(character_id,account,kind,item_id,until_ms,maintenance,cool_group) VALUES($id,$account,$kind,$item,$until,$maintenance,$group)";
        var kind = command.Parameters.Add("$kind", SqliteType.Integer);
        var item = command.Parameters.Add("$item", SqliteType.Integer);
        var until = command.Parameters.Add("$until", SqliteType.Integer);
        var maintenance = command.Parameters.Add("$maintenance", SqliteType.Integer);
        var group = command.Parameters.Add("$group", SqliteType.Integer);
        foreach (var row in rows)
        {
            kind.Value = (byte)row.Kind; item.Value = row.ItemId; until.Value = row.UntilMilliseconds;
            maintenance.Value = row.Maintenance; group.Value = row.CoolGroup; command.ExecuteNonQuery();
        }
    }
}
