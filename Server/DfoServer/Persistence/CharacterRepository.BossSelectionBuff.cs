using System.Text.Json;
using DfoServer.Game.Inventory.Models;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    static BossSelectionBuff? ReadBossSelectionBuff(SqliteConnection db, SqliteTransaction? tx, ushort id, string account)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "SELECT state_json,account FROM character_boss_selection_buff WHERE character_id=$id";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        if (reader.GetString(1) != account) throw new InvalidDataException("白斯追击药水账号归属损坏");
        var state = JsonSerializer.Deserialize<BossSelectionBuff>(reader.GetString(0)) ?? throw new InvalidDataException("白斯追击药水状态缺失");
        state.Validate();
        return state;
    }

    static void WriteBossSelectionBuff(SqliteConnection db, SqliteTransaction tx, string account, ushort id, BossSelectionBuff? state)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "DELETE FROM character_boss_selection_buff WHERE character_id=$id AND account=$account";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$account", account);
        command.ExecuteNonQuery();
        if (state is null) return;
        state.Validate();
        command.CommandText = "INSERT INTO character_boss_selection_buff(character_id,account,state_json) VALUES($id,$account,$state)";
        command.Parameters.AddWithValue("$state", JsonSerializer.Serialize(state));
        command.ExecuteNonQuery();
    }
}
