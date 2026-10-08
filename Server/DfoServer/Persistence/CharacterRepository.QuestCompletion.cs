using DfoServer.GameWorld.Characters;
using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Quests;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    public QuestCompletionCommit CommitQuestCompletion(AccountOwners.Lease owner, QuestCompletionPlan plan) => owner.Execute(() =>
    {
        ArgumentNullException.ThrowIfNull(plan);
        var expected = plan.Expected;
        var clear = plan.Clear;
        var reward = plan.ExperienceReward;
        if (!QuestCompletionPlan.MatchesClearedActiveQuest(expected, clear) || expected.Job > 15 ||
            reward.RuleId != QuestExperiencePolicy.RuleId || reward.ResourcePath != GameWorld.Quests.QuestRewardParameterCatalog.ResourcePath ||
            reward.QuestPath != QuestExperiencePolicy.QuestPath || reward.NativeIndex != 1 || reward.BaseExperience <= 0 ||
            reward.RawAdditionalPercent > 0 || reward.DifficultyKey != "1" || reward.DifficultyWeight <= 0 ||
            reward.Amount != checked((ulong)reward.BaseExperience * (ulong)reward.DifficultyWeight) / 100 || plan.GoldAmount != 0 ||
            plan.Growth.CharacterId != expected.Id || plan.Growth.Revision != expected.Revision ||
            plan.Growth.Learning.Experience != expected.Learning.Experience + reward.Amount ||
            plan.Rewards.Length != 7 || plan.Inventory.ChangedSlots.Length != 7 ||
            !plan.ActiveAfter.SequenceEqual(expected.ActiveMainQuests.Where(active => active.AcceptanceId != clear.AcceptanceId)) ||
            !plan.CompletedAfter.SequenceEqual(expected.CompletedQuests.Append(3145).Order()))
            throw new InvalidDataException("3145领奖必须使用完整匹配接取、本服资源规则及七项固定奖励计划");
        using var db = database.Open();
        using var tx = db.BeginTransaction(deferred: true);
        var current = Require(db, tx, owner.Account, expected.Name);
        if (current.Id != expected.Id)
            throw new InvalidOperationException("领奖角色或账号已变化");
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.Parameters.AddWithValue("$id", current.Id);
        command.Parameters.AddWithValue("$account", owner.Account);
        command.Parameters.AddWithValue("$acceptance", clear.AcceptanceId.ToString("N"));
        command.CommandText = """
            SELECT event_id,instance_id,task_attempt,epoch,quest_slot,revision
            FROM character_quest_clear_receipts
            WHERE character_id=$id AND account=$account AND acceptance_id=$acceptance AND quest_id=3145 AND dungeon_id=3 AND map_id=76126
            """;
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read() || reader.GetString(0) != clear.EventId.ToString("N") ||
                reader.GetString(1) != clear.InstanceId.ToString("N") || reader.GetInt64(2) != clear.TaskAttempt ||
                reader.GetInt64(3) != clear.Epoch || reader.GetInt64(4) != clear.QuestSlot || reader.GetInt64(5) != clear.Revision)
                throw new InvalidDataException("领奖资格不是原持久接取的权威清图事实");
        }
        command.CommandText = """
            SELECT event_id,instance_id,task_attempt,epoch,rule_id,resource_source,quest_source,native_index,
              base_experience,raw_additional_percent,difficulty_key,difficulty_weight,experience_amount,gold_amount,
              before_revision,revision,rewards,after_level,after_experience,before_level
            FROM character_quest_completion_receipts WHERE character_id=$id AND account=$account AND acceptance_id=$acceptance
            """;
        using (var reader = command.ExecuteReader())
        {
            if (reader.Read())
            {
                if (reader.GetString(0) != clear.EventId.ToString("N") || reader.GetString(1) != clear.InstanceId.ToString("N") ||
                    reader.GetInt64(2) != clear.TaskAttempt || reader.GetInt64(3) != clear.Epoch || reader.GetString(4) != reward.RuleId ||
                    reader.GetString(5) != reward.ResourcePath || reader.GetString(6) != reward.QuestPath || reader.GetInt64(7) != reward.NativeIndex ||
                    reader.GetInt64(8) != reward.BaseExperience || reader.GetInt64(9) != reward.RawAdditionalPercent ||
                    reader.GetString(10) != reward.DifficultyKey || reader.GetInt64(11) != reward.DifficultyWeight ||
                    reader.GetInt64(12) != reward.Amount || reader.GetInt64(13) != 0 || reader.GetInt64(14) != expected.Revision ||
                    reader.GetInt32(19) != expected.Level ||
                    !current.CompletedQuests.Contains(3145) || current.ActiveMainQuests.Any(active => active.AcceptanceId == clear.AcceptanceId))
                    throw new InvalidDataException("相同领奖身份的规则、接取或持久结果发生变化");
                var granted = JsonSerializer.Deserialize<ImmutableArray<QuestGrantedItem>>(reader.GetString(16));
                VerifyQuestGrantedItems(plan, granted);
                long revision = reader.GetInt64(15);
                bool projectionCurrent = current.Revision == revision && current.Job == expected.Job && current.GrowType == expected.GrowType &&
                    current.Level == reader.GetInt32(17) && current.ActiveMainQuests.SequenceEqual(plan.ActiveAfter) &&
                    current.CompletedQuests.SequenceEqual(plan.CompletedAfter) &&
                    current.Learning.Experience == checked((ulong)reader.GetInt64(18)) &&
                    SameLearning(current.Learning, plan.Growth.Learning) &&
                    current.Skills.SequenceEqual(plan.Growth.Learning.Trees.Single(tree => tree.Context == 0).Skills
                        .Select(skill => new CharacterSkill(skill.Id, skill.Level)).OrderBy(skill => skill.Id)) && granted.All(row =>
                    current.Inventory.Find(row.Item.Position) is { } item && item == row.Item);
                // 只对原未安装旧快照的精确恢复提供增量。新会话已装载完整库存不能重复ACK34。
                return new QuestCompletionCommit(current, clear, reward, granted, true, projectionCurrent, checked((byte)reader.GetInt32(19)), expected.Learning);
            }
        }
        if (current.Revision != expected.Revision || current.Level != expected.Level || current.Job != expected.Job ||
            current.GrowType != expected.GrowType || !SameLearning(current.Learning, expected.Learning) ||
            !current.Skills.SequenceEqual(expected.Skills) || !current.ActiveMainQuests.SequenceEqual(expected.ActiveMainQuests) ||
            !current.CompletedQuests.SequenceEqual(expected.CompletedQuests))
            throw new InvalidOperationException("领奖规划的等级、两树、技能、任务或revision已变化");
        // 复用现有资产事务，绝不另开内部写事务；其revision+1也是整个领奖唯一的revision推进。
        var inventoryCommit = CommitInventoryInTransaction(db, tx, owner, expected, plan.Inventory);
        command.Parameters.AddWithValue("$level", plan.Growth.TargetLevel);
        command.Parameters.AddWithValue("$experience", checked((long)plan.Growth.Learning.Experience));
        command.Parameters.AddWithValue("$revision", inventoryCommit.Revision);
        command.CommandText = "UPDATE characters SET level=$level,experience=$experience WHERE id=$id AND account=$account AND revision=$revision";
        if (command.ExecuteNonQuery() != 1)
            throw new InvalidOperationException("领奖成长的原资产提交revision已变化");
        WriteLearning(db, tx, current.Id, plan.Growth.Learning);
        WriteCanonicalSkills(db, tx, current.Id, plan.Growth.Learning);
        command.Parameters.AddWithValue("$slot", clear.QuestSlot);
        command.CommandText = """
            DELETE FROM character_active_main_quests
            WHERE character_id=$id AND slot=$slot AND quest_id=3145 AND condition_state=0 AND acceptance_id=$acceptance
            """;
        if (command.ExecuteNonQuery() != 1)
            throw new InvalidOperationException("原已达成活动任务不能原子清槽");
        command.CommandText = "INSERT INTO character_completed_quests(character_id,quest_id) VALUES($id,3145)";
        command.ExecuteNonQuery();
        var grantedItems = plan.Rewards.Select(row => new QuestGrantedItem(
            inventoryCommit.Inventory.Find(row.Position) ?? throw new InvalidOperationException("持久奖励缺少实际目的槽"), row.GrantedCount)).ToImmutableArray();
        VerifyQuestGrantedItems(plan, grantedItems);
        command.Parameters.AddWithValue("$event", clear.EventId.ToString("N"));
        command.Parameters.AddWithValue("$instance", clear.InstanceId.ToString("N"));
        command.Parameters.AddWithValue("$attempt", clear.TaskAttempt);
        command.Parameters.AddWithValue("$epoch", clear.Epoch);
        command.Parameters.AddWithValue("$generation", owner.Generation);
        command.Parameters.AddWithValue("$rule", reward.RuleId);
        command.Parameters.AddWithValue("$resource", reward.ResourcePath);
        command.Parameters.AddWithValue("$quest", reward.QuestPath);
        command.Parameters.AddWithValue("$index", reward.NativeIndex);
        command.Parameters.AddWithValue("$basis", reward.BaseExperience);
        command.Parameters.AddWithValue("$additional", reward.RawAdditionalPercent);
        command.Parameters.AddWithValue("$difficulty", reward.DifficultyKey);
        command.Parameters.AddWithValue("$weight", reward.DifficultyWeight);
        command.Parameters.AddWithValue("$amount", reward.Amount);
        command.Parameters.AddWithValue("$beforeLevel", expected.Level);
        command.Parameters.AddWithValue("$beforeExperience", checked((long)expected.Learning.Experience));
        command.Parameters.AddWithValue("$beforeRevision", expected.Revision);
        command.Parameters.AddWithValue("$rewards", JsonSerializer.Serialize(grantedItems));
        command.CommandText = """
            INSERT INTO character_quest_completion_receipts(character_id,account,acceptance_id,quest_id,event_id,instance_id,
              task_attempt,epoch,generation,rule_id,resource_source,quest_source,native_index,base_experience,raw_additional_percent,
              difficulty_key,difficulty_weight,experience_amount,gold_amount,before_level,after_level,before_experience,after_experience,
              before_revision,revision,rewards)
            VALUES($id,$account,$acceptance,3145,$event,$instance,$attempt,$epoch,$generation,$rule,$resource,$quest,$index,$basis,$additional,
              $difficulty,$weight,$amount,0,$beforeLevel,$level,$beforeExperience,$experience,$beforeRevision,$revision,$rewards)
            """;
        command.ExecuteNonQuery();
        var committed = Require(db, tx, owner.Account, expected.Name);
        if (committed.Revision != expected.Revision + 1 || committed.Level != plan.Growth.TargetLevel ||
            !SameLearning(committed.Learning, plan.Growth.Learning) ||
            !committed.ActiveMainQuests.SequenceEqual(plan.ActiveAfter) || !committed.CompletedQuests.SequenceEqual(plan.CompletedAfter))
            throw new InvalidOperationException("持久领奖结果与原完整规划不同");
        tx.Commit();
        return new QuestCompletionCommit(committed, clear, reward, grantedItems, false, true, expected.Level, expected.Learning);
    });

    private static void VerifyQuestGrantedItems(QuestCompletionPlan plan, ImmutableArray<QuestGrantedItem> rewards)
    {
        if (rewards.IsDefault || rewards.Length != 7 || rewards.Select(row => row.Item.Uid).Distinct().Count() != 7)
            throw new InvalidDataException("领奖账本奖励缺失或UID重复");
        for (int index = 0; index < rewards.Length; index++)
        {
            var actual = rewards[index];
            var expected = plan.Rewards[index];
            if (actual.Item.Uid <= 0 || actual.Item.Position != expected.Position || actual.Item.Core != expected.Core ||
                actual.GrantedCount != expected.GrantedCount ||
                expected.ExpectedDestinationUid != 0 && actual.Item.Uid != expected.ExpectedDestinationUid)
                throw new InvalidDataException("实际领奖UID、完整Core、槽位或原序增量与规划不同");
        }
    }
}
