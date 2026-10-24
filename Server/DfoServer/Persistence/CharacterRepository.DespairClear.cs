using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    public DungeonDespairClearCommit CommitDungeonDespairClear(AccountOwners.Lease owner, SinglePlayerDungeonRun run,
        PreparedDungeonDespairClear prepared) => owner.Execute(() =>
    {
        if (!run.IsPendingDespairClear(prepared)) throw new InvalidOperationException("绝望通关不是原实例冻结结果");
        var expected = prepared.Expected;
        var fact = prepared.Fact;
        ValidateInventoryPlan(expected, prepared.Inventory);
        prepared.After.Validate();
        string deaths = JsonSerializer.Serialize(fact.ConfirmedDeathEvents);
        string assets = JsonSerializer.Serialize(new { prepared.Inventory.After, prepared.Inventory.OverflowItems, prepared.Reward,
            Rule = prepared.Reward is null ? null : DungeonDespairRewardPlan.RuleId, Progress = prepared.Title?.Progress,
            Sections = prepared.Title?.Sections.Order().ToArray(), Postals = prepared.Title?.Postals, Items = prepared.Title?.PostalItems });
        using var db = database.Open();
        using var write = db.BeginWrite("CharacterRepository.CommitDungeonDespairClear", deferred: true);
        using var tx = write.Transaction;
        var current = Require(db, tx, owner.Account, expected.Name);
        if (current.Id != expected.Id || current.DungeonState?.LastAttempt != fact.Identity.TaskAttempt)
            throw new InvalidOperationException("绝望通关角色或尝试已变化");
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.Parameters.AddWithValue("$id", expected.Id);
        command.Parameters.AddWithValue("$account", owner.Account);
        command.Parameters.AddWithValue("$instance", fact.Identity.InstanceId.ToString("N"));
        command.CommandText = """
            SELECT event_id,task_attempt,epoch,dungeon_id,clear_day,play_milliseconds,death_events,assets_json
            FROM dungeon_despair_clear_receipts WHERE character_id=$id AND account=$account AND instance_id=$instance
            """;
        bool duplicate;
        using (var reader = command.ExecuteReader())
        {
            duplicate = reader.Read();
            if (duplicate && (reader.GetString(0) != prepared.EventId.ToString("N") ||
                reader.GetInt64(1) != fact.Identity.TaskAttempt || reader.GetInt64(2) != fact.Identity.Epoch ||
                reader.GetInt64(3) != fact.DungeonId || reader.GetInt32(4) != prepared.ClearDay ||
                reader.GetInt64(5) != prepared.PlayMilliseconds || reader.GetString(6) != deaths || reader.GetString(7) != assets))
                throw new InvalidDataException("同一绝望实例的冻结通关结果被修改");
        }
        if (!duplicate)
        {
            ValidateCurrentInventoryPlan(current, expected, prepared.Inventory);
            if (ReadDespairProgress(db, tx, owner.Account, expected.Id) != prepared.Entry.DespairProgress)
                throw new InvalidOperationException("绝望楼层在通关准备后变化");
            command.CommandText = """
                SELECT count(*) FROM dungeon_special_entry_receipts WHERE character_id=$id AND account=$account AND
                  instance_id=$instance AND task_attempt=$attempt AND dungeon_id=$dungeon AND kind=3 AND layer=$layer AND service_day=$entryDay
                """;
            command.Parameters.AddWithValue("$attempt", fact.Identity.TaskAttempt);
            command.Parameters.AddWithValue("$dungeon", fact.DungeonId);
            command.Parameters.AddWithValue("$layer", prepared.Entry.Layer);
            command.Parameters.AddWithValue("$entryDay", prepared.Entry.Day);
            if (Convert.ToInt64(command.ExecuteScalar()) != 1) throw new InvalidOperationException("绝望通关缺少原层已提交入场事实");
            // 称号目录先规划完整奖励；合并后的资产只写一次，共用原事务，不能内层另开提交。
            current = prepared.Title is { } title
                ? CommitTitleAchievementInTransaction(db, tx, owner, title with { Inventory = prepared.Inventory })
                : CommitInventoryInTransaction(db, tx, owner, current, expected, prepared.Inventory);
            command.CommandText = """
                UPDATE dungeon_despair_progress SET next_layer=$next,last_clear_day=$day,clear_count=$count
                WHERE character_id=$id AND account=$account AND next_layer=$layer AND last_clear_day=$previousDay AND clear_count=$previousCount
                """;
            command.Parameters.AddWithValue("$next", prepared.After.NextLayer);
            command.Parameters.AddWithValue("$day", prepared.After.LastClearDay);
            command.Parameters.AddWithValue("$count", prepared.After.ClearCount);
            command.Parameters.AddWithValue("$previousDay", prepared.Entry.DespairProgress!.LastClearDay);
            command.Parameters.AddWithValue("$previousCount", prepared.Entry.DespairProgress.ClearCount);
            if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("绝望通关进度版本变化");
            command.CommandText = """
                INSERT INTO dungeon_despair_clear_receipts(character_id,account,instance_id,event_id,task_attempt,epoch,dungeon_id,
                  clear_day,play_milliseconds,death_events,assets_json)
                VALUES($id,$account,$instance,$event,$attempt,$epoch,$dungeon,$day,$time,$deaths,$assets)
                """;
            command.Parameters.AddWithValue("$event", prepared.EventId.ToString("N"));
            command.Parameters.AddWithValue("$epoch", fact.Identity.Epoch);
            command.Parameters.AddWithValue("$time", (long)prepared.PlayMilliseconds);
            command.Parameters.AddWithValue("$deaths", deaths);
            command.Parameters.AddWithValue("$assets", assets);
            command.ExecuteNonQuery();
        }
        var progress = ReadDespairProgress(db, tx, owner.Account, current.Id);
        if (progress != prepared.After) throw new InvalidOperationException("绝望回执与实际进度不一致");
        tx.Commit();
        return new DungeonDespairClearCommit(current, duplicate, progress,
            new(prepared.Inventory.Before, current.Inventory, prepared.Inventory.Reason, new(expected), prepared.Inventory.OverflowItems));
    });
}
