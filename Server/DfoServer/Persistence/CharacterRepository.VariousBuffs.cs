using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Inventory.Models;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    static ImmutableArray<VariousBuffState> ReadVariousBuffs(SqliteConnection db, SqliteTransaction? tx, ushort id, string account)
    {
        using var command = db.CreateCommand(); command.Transaction = tx;
        command.CommandText = "SELECT account,buffs FROM character_various_buffs WHERE character_id=$id";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return [];
        if (reader.GetString(0) != account) throw new InvalidDataException("通用Buff账号归属损坏");
        var rows = JsonSerializer.Deserialize<ImmutableArray<VariousBuffState>>(reader.GetString(1));
        VariousBuffState.Validate(rows);
        return rows;
    }

    static void WriteVariousBuffs(SqliteConnection db, SqliteTransaction tx, ushort id, string account, ImmutableArray<VariousBuffState> rows)
    {
        VariousBuffState.Validate(rows);
        using var command = db.CreateCommand(); command.Transaction = tx;
        command.CommandText = rows.IsEmpty ? "DELETE FROM character_various_buffs WHERE character_id=$id AND account=$account" : """
            INSERT INTO character_various_buffs(character_id,account,buffs) VALUES($id,$account,$buffs)
            ON CONFLICT(character_id) DO UPDATE SET buffs=excluded.buffs WHERE account=excluded.account
            """;
        command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$account", account);
        if (!rows.IsEmpty) command.Parameters.AddWithValue("$buffs", JsonSerializer.Serialize(rows));
        command.ExecuteNonQuery();
    }
}
