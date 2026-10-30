using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Inventory.Models;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    static KnightShieldDeck ReadKnightShieldDeck(SqliteConnection db, SqliteTransaction? tx, ushort id, string account)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "SELECT knight_shield_deck FROM characters WHERE id=$id AND account=$account";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$account", account);
        if (command.ExecuteScalar() is not string json) throw new InvalidDataException("守护者盾牌角色归属不存在");
        var state = new KnightShieldDeck(JsonSerializer.Deserialize<ImmutableArray<int>>(json));
        state.Validate();
        return state;
    }

    static void WriteKnightShieldDeck(SqliteConnection db, SqliteTransaction tx, string account, ushort id, KnightShieldDeck state)
    {
        state.Validate();
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "UPDATE characters SET knight_shield_deck=$state WHERE id=$id AND account=$account";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$account", account);
        command.Parameters.AddWithValue("$state", JsonSerializer.Serialize(state.Items));
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("守护者盾牌保存角色归属已变化");
    }
}
