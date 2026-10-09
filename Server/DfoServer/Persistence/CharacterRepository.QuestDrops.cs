using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Quests;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    static void SaveQuestDrops(SqliteConnection db, SqliteTransaction tx, string account, QuestDropPlan plan, InventorySnapshot actual)
    {
        using var command = db.CreateCommand(); command.Transaction = tx;
        command.CommandText = """
            INSERT INTO character_quest_drop_receipts(character_id,account,event_id,before_revision,revision,plan_snapshot,changed_items)
            VALUES($id,$account,$event,$before,$revision,$snapshot,$items)
            """;
        command.Parameters.AddWithValue("$id",plan.Expected.Id); command.Parameters.AddWithValue("$account",account);
        command.Parameters.AddWithValue("$event",plan.EventId.ToString("N")); command.Parameters.AddWithValue("$before",plan.Expected.Revision);
        command.Parameters.AddWithValue("$revision",plan.Expected.Revision + 1); command.Parameters.AddWithValue("$snapshot",plan.Snapshot);
        command.Parameters.AddWithValue("$items",JsonSerializer.Serialize(actual.Items.Where(item => plan.Inventory.ChangedSlots.Contains(item.Position)).ToImmutableArray()));
        command.ExecuteNonQuery();
    }

    static void VerifyQuestDrops(SqliteConnection db, SqliteTransaction tx, string account, ushort characterId, Guid eventId, QuestDropPlan? plan)
    {
        using var command = db.CreateCommand(); command.Transaction = tx;
        command.CommandText = "SELECT plan_snapshot FROM character_quest_drop_receipts WHERE character_id=$id AND account=$account AND event_id=$event";
        command.Parameters.AddWithValue("$id",characterId); command.Parameters.AddWithValue("$account",account);
        command.Parameters.AddWithValue("$event",eventId.ToString("N"));
        string? saved = command.ExecuteScalar() as string;
        if (saved != plan?.Snapshot) throw new InvalidDataException("同一死亡事件的任务掉落结果发生变化");
    }
}
