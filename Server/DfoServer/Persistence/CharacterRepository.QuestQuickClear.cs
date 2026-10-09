using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Quests;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    public QuestQuickClearCommit CommitQuestQuickClear(AccountOwners.Lease owner, QuestQuickClearPlan plan) => owner.Execute(() =>
    {
        var expected = plan.Expected;
        var scope = plan.Interval.Select(quest => quest.Id).Append(plan.Question.Id).Append(plan.MarkerQuestId).ToHashSet();
        var newCompleted = scope.Except(QuestEligibility.Completed(expected)).Order().ToArray();
        if (expected.Id == 0 || plan.AcceptanceId == Guid.Empty || plan.Unit.ParentKey < 0 ||
            !expected.ActiveMainQuests.Contains(plan.ActiveQuestion) || plan.ActiveQuestion.QuestId != plan.Question.Id ||
            !plan.NewCompletedQuestIds.SequenceEqual(newCompleted) ||
            !plan.CompletedAfter.SequenceEqual(expected.CompletedQuests.Concat(newCompleted).Distinct().Order()) ||
            !plan.RemovedActive.SequenceEqual(expected.ActiveMainQuests.Where(row => scope.Contains(row.QuestId))) ||
            !plan.ActiveAfter.Select(row => (row.Slot,row.QuestId,row.AcceptanceId)).SequenceEqual(
                expected.ActiveMainQuests.Where(row => !scope.Contains(row.QuestId)).Select(row => (row.Slot,row.QuestId,row.AcceptanceId))) ||
            !QuestCycle.SameAnswers(plan.AnswersAfter,expected.QuestAnswers.SetItem(plan.Question.Id,plan.Answer)) ||
            !plan.AccountCompletedAfter.SequenceEqual(expected.AccountCompletedQuests) ||
            !QuestCycle.SameAnswers(plan.AccountAnswersAfter,expected.AccountQuestAnswers))
            throw new InvalidDataException("无奖励跳过规划必须保留原接取、完整范围及无关任务状态");
        var unchangedInventory = new InventoryPlan(expected.Inventory,expected.Inventory,"quest_skip_comparison");
        using var db = database.Open();
        using var tx = db.BeginTransaction(deferred: true);
        var current = Require(db,tx,owner.Account,expected.Name);
        using var command = db.CreateCommand(); command.Transaction = tx;
        command.Parameters.AddWithValue("$id",expected.Id);
        command.Parameters.AddWithValue("$account",owner.Account);
        command.Parameters.AddWithValue("$acceptance",plan.AcceptanceId.ToString("N"));
        command.CommandText = "SELECT parent_key,question_quest,before_revision,revision,plan_snapshot FROM character_quest_skip_receipts WHERE character_id=$id AND account=$account AND acceptance_id=$acceptance";
        using (var reader = command.ExecuteReader())
        {
            if (reader.Read())
            {
                if (reader.GetInt32(0) != plan.Unit.ParentKey || reader.GetInt32(1) != plan.Question.Id ||
                    reader.GetInt64(2) != expected.Revision || reader.GetInt64(3) != checked(expected.Revision+1) ||
                    reader.GetString(4) != plan.Snapshot)
                    throw new InvalidDataException("相同跳过接取身份的范围、答案或冻结规划发生变化");
                return new QuestQuickClearCommit(current,plan,true,Matches(current,After()));
            }
        }
        if (!Matches(current,expected))
            throw new InvalidOperationException("跳过原角色、完整资产、活动任务或revision已变化");
        command.Parameters.AddWithValue("$before",expected.Revision);
        command.CommandText = "UPDATE characters SET revision=revision+1 WHERE id=$id AND account=$account AND revision=$before";
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("跳过revision已变化");
        foreach (var active in plan.RemovedActive)
        {
            command.Parameters.AddWithValue("$slot",active.Slot);
            command.Parameters.AddWithValue("$quest",active.QuestId);
            command.Parameters.AddWithValue("$removedAcceptance",active.AcceptanceId.ToString("N"));
            command.Parameters.AddWithValue("$scalar",(long)active.ConditionState);
            command.CommandText = "DELETE FROM character_active_main_quests WHERE character_id=$id AND slot=$slot AND quest_id=$quest AND acceptance_id=$removedAcceptance AND condition_state=$scalar";
            if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("跳过不能原子移除原活动槽");
            foreach (string key in new[] { "$slot","$quest","$removedAcceptance","$scalar" }) command.Parameters.RemoveAt(key);
        }
        WriteQuestConditions(db,tx,expected.Id,expected.ActiveMainQuests,plan.ActiveAfter);
        foreach (int id in plan.NewCompletedQuestIds)
        {
            command.Parameters.AddWithValue("$quest",id);
            command.Parameters.AddWithValue("$answer",id == plan.Question.Id ? plan.Answer : (object)DBNull.Value);
            command.CommandText = "INSERT INTO character_completed_quests(character_id,quest_id,daily_period,answer) VALUES($id,$quest,0,$answer)";
            command.ExecuteNonQuery();
            command.Parameters.RemoveAt("$quest"); command.Parameters.RemoveAt("$answer");
        }
        command.Parameters.AddWithValue("$parent",plan.Unit.ParentKey);
        command.Parameters.AddWithValue("$question",plan.Question.Id);
        command.Parameters.AddWithValue("$revision",checked(expected.Revision+1));
        command.Parameters.AddWithValue("$snapshot",plan.Snapshot);
        command.CommandText = """
            INSERT INTO character_quest_skip_receipts(character_id,account,acceptance_id,parent_key,question_quest,before_revision,revision,plan_snapshot)
            VALUES($id,$account,$acceptance,$parent,$question,$before,$revision,$snapshot)
            """;
        command.ExecuteNonQuery();
        var committed = Require(db,tx,owner.Account,expected.Name);
        if (!Matches(committed,After())) throw new InvalidOperationException("持久无奖励跳过结果与完整规划不同");
        tx.Commit();
        return new QuestQuickClearCommit(committed,plan,false,true);

        Character After() => expected with { Revision = checked(expected.Revision+1), ActiveMainQuests = plan.ActiveAfter.ToArray(),
            CompletedQuests = plan.CompletedAfter.ToArray(), QuestAnswers = plan.AnswersAfter };

        bool Matches(Character actual,Character target) => actual.Id == target.Id && actual.Slot == target.Slot &&
            actual.Name == target.Name && actual.Revision == target.Revision && actual.Job == target.Job && actual.GrowType == target.GrowType &&
            actual.Level == target.Level && actual.EquipmentSlotFlags == target.EquipmentSlotFlags &&
            actual.AcceptedStoryLevel == target.AcceptedStoryLevel && actual.Position == target.Position && actual.Return == target.Return &&
            actual.DungeonState == target.DungeonState && actual.Skills.SequenceEqual(target.Skills) && SameLearning(actual.Learning,target.Learning) &&
            SameInventory(actual.Inventory,unchangedInventory) && actual.ActiveMainQuests.SequenceEqual(target.ActiveMainQuests) &&
            actual.CompletedQuests.SequenceEqual(target.CompletedQuests) && QuestCycle.Same(actual,target);
    });
}
