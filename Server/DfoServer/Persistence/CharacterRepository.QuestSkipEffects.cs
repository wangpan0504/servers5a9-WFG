using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    static void WriteAccountSyncMarkers(SqliteConnection db,SqliteTransaction tx,string account,
        ImmutableArray<int> markers,Guid operation)
    {
        if(markers.IsEmpty) return;
        using var command=db.CreateCommand(); command.Transaction=tx;
        command.Parameters.AddWithValue("$account",account); command.Parameters.AddWithValue("$operation",operation.ToString("N"));
        foreach(int marker in markers)
        {
            command.Parameters.AddWithValue("$quest",marker);
            // 必须有同账号真实角色完成位；原标记及答案不重写，来源角色删除后账号记录仍保留。
            command.CommandText="""
                INSERT INTO account_completed_quests(account,quest_id,character_id,acceptance_id,daily_period,answer)
                SELECT $account,$quest,MIN(q.character_id),$operation,0,NULL
                FROM character_completed_quests q JOIN characters c ON c.id=q.character_id
                WHERE c.account=$account AND q.quest_id=$quest HAVING COUNT(*)>0
                ON CONFLICT(account,quest_id) DO NOTHING
                """;
            command.ExecuteNonQuery();
            command.CommandText="SELECT COUNT(*) FROM account_completed_quests WHERE account=$account AND quest_id=$quest";
            if(Convert.ToInt32(command.ExecuteScalar())!=1) throw new InvalidOperationException("账号同步缺少原完成标记");
            command.Parameters.RemoveAt("$quest");
        }
    }

    static void WriteQuestEffectReceipt(SqliteConnection db,SqliteTransaction tx,Character expected,Guid operation,
        InventoryPlan inventory,InventorySnapshot committed)
    {
        using var command=db.CreateCommand(); command.Transaction=tx;
        command.Parameters.AddWithValue("$id",expected.Id); command.Parameters.AddWithValue("$operation",operation.ToString("N"));
        command.Parameters.AddWithValue("$before",expected.Revision); command.Parameters.AddWithValue("$revision",checked(expected.Revision+1));
        // 保留已发布结构；旧补领声明不再写入，新回执仅恢复本次提交的实际UID。
        command.Parameters.AddWithValue("$items",JsonSerializer.Serialize(inventory.ChangedSlots.Select(committed.Find).Where(item=>item is not null).ToArray()));
        command.CommandText="INSERT INTO character_quest_effect_receipts(character_id,operation_id,before_revision,revision,claimed_quests,changed_items) VALUES($id,$operation,$before,$revision,'[]',$items)";
        command.ExecuteNonQuery();
    }

    static InventorySnapshot RestoreQuestEffectInventory(SqliteConnection db,SqliteTransaction tx,Character expected,Guid operation,InventorySnapshot after)
    {
        using var command=db.CreateCommand(); command.Transaction=tx;
        command.Parameters.AddWithValue("$id",expected.Id); command.Parameters.AddWithValue("$operation",operation.ToString("N"));
        command.CommandText="SELECT changed_items FROM character_quest_effect_receipts WHERE character_id=$id AND operation_id=$operation";
        if(command.ExecuteScalar() is not string json) return after;
        var changed=JsonSerializer.Deserialize<InventoryItem[]>(json)!.ToDictionary(item=>item.Position);
        return after with { Items=after.Items.Select(item=>changed.GetValueOrDefault(item.Position,item)).ToImmutableArray() };
    }
}
