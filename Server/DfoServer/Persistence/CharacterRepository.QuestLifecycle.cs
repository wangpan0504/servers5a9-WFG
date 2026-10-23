using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Quests;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    public QuestLifecycleCommit CommitQuestLifecycle(AccountOwners.Lease owner, QuestLifecyclePlan plan) => owner.Execute(() =>
    {
        var expected = plan.Expected;
        if (expected.Id == 0 || plan.OperationId == Guid.Empty || plan.AcceptanceId == Guid.Empty ||
            plan.ActiveAfter.Length > 30 || plan.ActiveAfter.Any(active => active.Slot >= 30 || active.QuestId is 0 or >= 30000 || active.AcceptanceId == Guid.Empty) ||
            plan.ActiveAfter.Select(active => active.Slot).Distinct().Count() != plan.ActiveAfter.Length ||
            plan.ActiveAfter.Select(active => active.QuestId).Distinct().Count() != plan.ActiveAfter.Length ||
            plan.ActiveAfter.Select(active => active.AcceptanceId).Distinct().Count() != plan.ActiveAfter.Length ||
            !plan.CompletedAfter.SequenceEqual(expected.CompletedQuests.Where(id => plan.CompletedAfter.Contains(id))) ||
            expected.CompletedQuests.Except(plan.CompletedAfter).Any(id => plan.Action != QuestLifecycleAction.Accept || id != plan.QuestId))
            throw new InvalidDataException("任务生命周期规划的身份或活动槽无效");
        if (!plan.AccountCompletedAfter.SequenceEqual(expected.AccountCompletedQuests.Where(plan.AccountCompletedAfter.Contains)) ||
            expected.AccountCompletedQuests.Except(plan.AccountCompletedAfter).Any(id => plan.Action != QuestLifecycleAction.Accept || id != plan.QuestId))
            throw new InvalidDataException("任务生命周期不能改变无关账号完成位");
        ValidateInventoryPlan(expected, plan.Inventory);
        using var db = database.Open();
        using var write = db.BeginWrite("CharacterRepository.CommitQuestLifecycle", deferred: true);
        using var tx = write.Transaction;
        var current = Require(db, tx, owner.Account, expected.Name);
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.Parameters.AddWithValue("$id", expected.Id);
        command.Parameters.AddWithValue("$account", owner.Account);
        command.Parameters.AddWithValue("$operation", plan.OperationId.ToString("N"));
        string beforeActive = JsonSerializer.Serialize(expected.ActiveMainQuests);
        string afterActive = JsonSerializer.Serialize(plan.ActiveAfter);
        string beforeCompleted = JsonSerializer.Serialize(expected.CompletedQuests);
        string afterCompleted = JsonSerializer.Serialize(plan.CompletedAfter);
        string? sceneEvent = plan.SceneEvent is null ? null : JsonSerializer.Serialize(plan.SceneEvent);
        command.CommandText = """
            SELECT action,quest_id,acceptance_id,before_revision,revision,before_active,after_active,changed_items,scene_event,before_completed,after_completed
            FROM character_quest_lifecycle_receipts WHERE character_id=$id AND account=$account AND operation_id=$operation
            """;
        using (var reader = command.ExecuteReader())
        {
            if (reader.Read())
            {
                if (reader.GetString(0) != plan.Action.ToString() || reader.GetInt32(1) != plan.QuestId ||
                    reader.GetString(2) != plan.AcceptanceId.ToString("N") || reader.GetInt64(3) != expected.Revision ||
                    reader.GetString(5) != beforeActive || reader.GetString(6) != afterActive ||
                    (reader.IsDBNull(8) ? null : reader.GetString(8)) != sceneEvent ||
                    !reader.IsDBNull(9) && reader.GetString(9) != beforeCompleted ||
                    !reader.IsDBNull(10) && reader.GetString(10) != afterCompleted)
                    throw new InvalidDataException("相同任务操作身份的规划发生变化");
                var changedItems = JsonSerializer.Deserialize<ImmutableArray<InventoryItem>>(reader.GetString(7));
                var changedIndex = changedItems.ToDictionary(item => item.Position);
                var after = plan.Inventory.After with { Items = plan.Inventory.After.Items.Select(item =>
                    changedIndex.GetValueOrDefault(item.Position, item)).ToImmutableArray() };
                // 原库存提交只在实际账号钱包变化时推进一次版本；按冻结计划恢复，不能借用后续钱包版本。
                if (plan.Inventory.Changed && !plan.Inventory.Before.AccountWallet.Same(after.AccountWallet))
                    after = after with { AccountWallet = after.AccountWallet with
                        { Revision = checked(plan.Inventory.Before.AccountWallet.Revision + 1) } };
                bool projectionCurrent = current.Id == expected.Id && current.Revision == reader.GetInt64(4) &&
                    current.ActiveMainQuests.SequenceEqual(plan.ActiveAfter) && current.Level == expected.Level &&
                    current.Job == expected.Job && current.GrowType == expected.GrowType && current.Position == expected.Position &&
                    current.AcceptedStoryLevel == expected.AcceptedStoryLevel && current.CompletedQuests.SequenceEqual(plan.CompletedAfter) &&
                    current.AccountCompletedQuests.SequenceEqual(plan.AccountCompletedAfter) &&
                    current.Skills.SequenceEqual(expected.Skills) && SameLearning(current.Learning, expected.Learning) &&
                    SameInventory(current.Inventory, new(after, after, "quest_receipt_projection"));
                reader.Close();
                SaveOrVerifySceneClear(db,tx,owner,plan,true);
                return new QuestLifecycleCommit(current, plan, true, projectionCurrent);
            }
        }
        if (current.Id != expected.Id || current.Revision != expected.Revision || current.Level != expected.Level ||
            current.Job != expected.Job || current.GrowType != expected.GrowType || current.Position != expected.Position ||
            current.AcceptedStoryLevel != expected.AcceptedStoryLevel || !current.Skills.SequenceEqual(expected.Skills) ||
            !current.CompletedQuests.SequenceEqual(expected.CompletedQuests) ||
            !current.ActiveMainQuests.SequenceEqual(expected.ActiveMainQuests) || !SameLearning(current.Learning, expected.Learning) ||
            !QuestCycle.Same(current, expected))
            throw new InvalidOperationException("任务原角色、场景、进度或revision已变化");
        var inventory = CommitInventoryInTransaction(db, tx, owner, expected, plan.Inventory);
        command.Parameters.AddWithValue("$beforeRevision", expected.Revision);
        if (!plan.Inventory.Changed)
        {
            command.CommandText = "UPDATE characters SET revision=revision+1 WHERE id=$id AND account=$account AND revision=$beforeRevision";
            if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("任务提交revision已变化");
        }
        command.CommandText = "DELETE FROM character_active_main_quests WHERE character_id=$id";
        command.ExecuteNonQuery();
        foreach (var active in plan.ActiveAfter)
        {
            command.Parameters.AddWithValue("$slot", active.Slot);
            command.Parameters.AddWithValue("$quest", active.QuestId);
            command.Parameters.AddWithValue("$scalar", (long)active.ConditionState);
            command.Parameters.AddWithValue("$acceptance", active.AcceptanceId.ToString("N"));
            command.CommandText = """
                INSERT INTO character_active_main_quests(character_id,slot,quest_id,condition_state,acceptance_id)
                VALUES($id,$slot,$quest,$scalar,$acceptance)
                """;
            command.ExecuteNonQuery();
            foreach (string name in new[] { "$slot", "$quest", "$scalar", "$acceptance" }) command.Parameters.RemoveAt(name);
        }
        foreach (int id in expected.CompletedQuests.Except(plan.CompletedAfter))
        {
            command.Parameters.AddWithValue("$removed", id);
            command.CommandText = "DELETE FROM character_completed_quests WHERE character_id=$id AND quest_id=$removed";
            if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("重复接取的原完成投影已变化");
            command.Parameters.RemoveAt("$removed");
        }
        foreach (int id in expected.AccountCompletedQuests.Except(plan.AccountCompletedAfter))
        {
            command.Parameters.AddWithValue("$removed", id);
            command.CommandText = "DELETE FROM account_completed_quests WHERE account=$account AND quest_id=$removed";
            if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("重复接取的账号完成投影已变化");
            command.Parameters.RemoveAt("$removed");
        }
        var changed = plan.Inventory.ChangedSlots.Select(inventory.Inventory.Find).Where(item => item is not null).Cast<InventoryItem>().ToImmutableArray();
        command.Parameters.AddWithValue("$action", plan.Action.ToString());
        command.Parameters.AddWithValue("$quest", plan.QuestId);
        command.Parameters.AddWithValue("$acceptance", plan.AcceptanceId.ToString("N"));
        command.Parameters.AddWithValue("$revision", checked(expected.Revision + 1));
        command.Parameters.AddWithValue("$beforeActive", beforeActive);
        command.Parameters.AddWithValue("$afterActive", afterActive);
        command.Parameters.AddWithValue("$changed", JsonSerializer.Serialize(changed));
        command.Parameters.AddWithValue("$scene", (object?)sceneEvent ?? DBNull.Value);
        command.Parameters.AddWithValue("$beforeCompleted", beforeCompleted);
        command.Parameters.AddWithValue("$afterCompleted", afterCompleted);
        command.CommandText = """
            INSERT INTO character_quest_lifecycle_receipts(character_id,account,operation_id,action,quest_id,acceptance_id,
                before_revision,revision,before_active,after_active,changed_items,scene_event,before_completed,after_completed)
            VALUES($id,$account,$operation,$action,$quest,$acceptance,$beforeRevision,$revision,$beforeActive,$afterActive,$changed,$scene,$beforeCompleted,$afterCompleted)
            """;
        command.ExecuteNonQuery();
        SaveOrVerifySceneClear(db,tx,owner,plan,false);
        var committed = Require(db, tx, owner.Account, expected.Name);
        if (committed.Revision != expected.Revision + 1 || !committed.ActiveMainQuests.SequenceEqual(plan.ActiveAfter) ||
            !committed.CompletedQuests.SequenceEqual(plan.CompletedAfter) || !committed.AccountCompletedQuests.SequenceEqual(plan.AccountCompletedAfter))
            throw new InvalidOperationException("持久任务槽与原规划不同");
        tx.Commit();
        return new QuestLifecycleCommit(committed, plan, false, true);
    });

    public Character AdvanceQuestCycle(AccountOwners.Lease owner, Character expected, int day, QuestAutoDailyPlan? daily = null) => owner.Execute(() =>
    {
        if (day is < 19000101 or > 99991231 || day < expected.QuestCycleDay)
            throw new InvalidDataException("游戏日不能逆向切换");
        if (daily is not null && (!ReferenceEquals(daily.Expected,expected) || daily.Day != day))
            throw new InvalidOperationException("自动每日规划不属于本次角色和游戏日");
        if (day == expected.QuestCycleDay && daily is null) return expected;
        using var db = database.Open();
        using var write = db.BeginWrite("CharacterRepository.AdvanceQuestCycle", deferred: true);
        using var tx = write.Transaction;
        var current = Require(db, tx, owner.Account, expected.Name);
        if (current.Id != expected.Id || current.Revision != expected.Revision || !QuestCycle.Same(current, expected) ||
            !current.CompletedQuests.SequenceEqual(expected.CompletedQuests) || !current.ActiveMainQuests.SequenceEqual(expected.ActiveMainQuests))
            throw new InvalidOperationException("任务周期的原角色或进度快照已失效");
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.Parameters.AddWithValue("$id", current.Id); command.Parameters.AddWithValue("$account", owner.Account);
        command.Parameters.AddWithValue("$revision", current.Revision); command.Parameters.AddWithValue("$day", day);
        command.CommandText = "DELETE FROM character_completed_quests WHERE character_id=$id AND daily_period>0 AND daily_period<$day";
        command.ExecuteNonQuery();
        command.CommandText = "DELETE FROM account_completed_quests WHERE account=$account AND daily_period>0 AND daily_period<$day";
        command.ExecuteNonQuery();
        if (daily is not null)
        {
            command.Parameters.AddWithValue("$quest", QuestAutoDailyPlan.QuestId);
            command.CommandText = "DELETE FROM character_active_main_quests WHERE character_id=$id AND quest_id=$quest";
            command.ExecuteNonQuery();
            if (daily.ActiveAfter.SingleOrDefault(row => row.QuestId == QuestAutoDailyPlan.QuestId) is { } assigned)
            {
                command.Parameters.AddWithValue("$slot",assigned.Slot);
                command.Parameters.AddWithValue("$scalar",assigned.ConditionState);
                command.Parameters.AddWithValue("$acceptance",assigned.AcceptanceId.ToString("N"));
                command.CommandText = "INSERT INTO character_active_main_quests(character_id,slot,quest_id,condition_state,acceptance_id) VALUES($id,$slot,$quest,$scalar,$acceptance)";
                command.ExecuteNonQuery();
            }
        }
        command.CommandText = "UPDATE characters SET quest_cycle_day=$day,revision=revision+1 WHERE id=$id AND account=$account AND revision=$revision";
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("任务周期revision已变化");
        var committed = Require(db, tx, owner.Account, expected.Name);
        if (daily is not null && !committed.ActiveMainQuests.SequenceEqual(daily.ActiveAfter))
            throw new InvalidOperationException("每日任务提交与规划的接取身份不符");
        tx.Commit();
        return committed;
    });
}
