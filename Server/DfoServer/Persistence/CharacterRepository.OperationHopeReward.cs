using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Adventure;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Quests;
using DfoServer.GameWorld.Characters;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    public OperationHopeRewardCommit CommitOperationHopeReward(AccountOwners.Lease owner, OperationHopeRewardPlan plan) => owner.Execute(() =>
    {
        var expected = plan.Expected;
        var active = expected.ActiveMainQuests.SingleOrDefault(row => row.AcceptanceId == plan.AcceptanceId);
        if (expected.Id != plan.Identity.CharacterId || plan.Identity.InstanceId == Guid.Empty || plan.Identity.TaskAttempt <= 0 ||
            plan.Identity.Epoch <= 0 || active is null || active.ConditionState != 0 || active.QuestId != plan.QuestId || active.Slot != plan.QuestSlot ||
            plan.AcceptanceId == Guid.Empty || plan.Binding.TargetDeath is null ||
            !plan.ActiveAfter.Select(row => (row.Slot,row.QuestId,row.AcceptanceId)).SequenceEqual(
                expected.ActiveMainQuests.Where(row => row.AcceptanceId != plan.AcceptanceId).Select(row => (row.Slot,row.QuestId,row.AcceptanceId))) ||
            (plan.GrowthPlan?.RequestedExperience ?? 0) != plan.ExperienceReward.Amount ||
            plan.GrowthPlan is { } growth && (growth.CharacterId != expected.Id || growth.Revision != expected.Revision))
            throw new InvalidDataException("希望领奖必须来自原实例接取与完整奖励规划");
        ValidateInventoryPlan(expected,plan.Inventory);
        using var db = database.Open();
        using var write = db.BeginWrite("CharacterRepository.CommitOperationHopeReward",deferred: true);
        using var tx = write.Transaction;
        var current = Require(db,tx,owner.Account,expected.Name);
        if (current.Id != expected.Id) throw new InvalidOperationException("希望领奖角色已经变化");
        using var command = db.CreateCommand(); command.Transaction = tx;
        foreach (var (name,value) in new (string,object)[] {
            ("$id",expected.Id),("$account",owner.Account),("$instance",plan.Identity.InstanceId.ToString("N")),
            ("$acceptance",plan.AcceptanceId.ToString("N")),("$quest",plan.QuestId) }) command.Parameters.AddWithValue(name,value);
        command.CommandText = """
            SELECT * FROM character_operation_hope_reward_receipts
            WHERE character_id=$id AND account=$account AND (instance_id=$instance OR acceptance_id=$acceptance)
            """;
        using (var reader = command.ExecuteReader())
        {
            if (reader.Read())
            {
                long I(string name) => reader.GetInt64(reader.GetOrdinal(name));
                string S(string name) => reader.GetString(reader.GetOrdinal(name));
                if (S("instance_id") != plan.Identity.InstanceId.ToString("N") || S("acceptance_id") != plan.AcceptanceId.ToString("N") ||
                    I("quest_id") != plan.QuestId || I("task_attempt") != plan.Identity.TaskAttempt || I("maze_ordinal") != plan.Binding.MazeOrdinal ||
                    I("clear_epoch") != plan.Identity.Epoch || I("before_revision") != expected.Revision || S("plan_snapshot") != plan.Snapshot ||
                    I("before_level") != expected.Level || I("after_level") != plan.TargetLevel ||
                    I("before_experience") != checked((long)expected.Learning.Experience) || I("after_experience") != checked((long)plan.TargetLearning.Experience) ||
                    I("requested_experience") != plan.ExperienceReward.Amount)
                    throw new InvalidDataException("相同希望实例或接取不能替换原冻结奖励");
                var rewards = JsonSerializer.Deserialize<ImmutableArray<QuestGrantedItem>>(S("rewards"));
                VerifyHopeRewards(plan,rewards);
                var changed = JsonSerializer.Deserialize<ImmutableArray<InventoryItem>>(S("changed_items"));
                if (changed.IsDefault || !changed.SequenceEqual(rewards.Select(row => row.Item)))
                    throw new InvalidDataException("希望领奖改变槽与实际奖励账本不同");
                var bySlot = changed.ToDictionary(row => row.Position);
                var after = plan.Inventory.After with { Items = plan.Inventory.After.Items.Select(row => bySlot.GetValueOrDefault(row.Position,row)).ToImmutableArray() };
                bool projection = current.Revision == I("revision") && MatchesHopeCharacter(current,expected,plan,after,afterCommit: true);
                return Result(current,rewards,true,projection);
            }
        }
        if (current.Revision != expected.Revision || !MatchesHopeCharacter(current,expected,plan,expected.Inventory,afterCommit: false))
            throw new InvalidOperationException("希望领奖角色、任务、成长或资产前提已经变化");
        if (plan.Proficiency is { } pending && (current.DungeonState is not { } state ||
            state.ProficiencyLevel != pending.Before.Level || state.ProficiencyExperience != pending.Before.Experience))
            throw new InvalidOperationException("希望领奖熟练度前提已经变化");
        var inventory = CommitInventoryInTransaction(db,tx,owner,expected,plan.Inventory);
        if (!plan.Inventory.Changed) throw new InvalidDataException("希望原材料奖励必须实际改变库存");
        foreach (var (name,value) in new (string,object)[] {
            ("$slot",plan.QuestSlot),("$level",plan.TargetLevel),("$experience",checked((long)plan.TargetLearning.Experience)),
            ("$revision",checked(expected.Revision+1)) }) command.Parameters.AddWithValue(name,value);
        command.CommandText = "UPDATE characters SET level=$level,experience=$experience WHERE id=$id AND account=$account AND revision=$revision";
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("希望奖励资产版本已经变化");
        if (plan.GrowthPlan is not null)
        {
            WriteLearning(db,tx,expected.Id,plan.TargetLearning);
            WriteCanonicalSkills(db,tx,expected.Id,plan.TargetLearning);
        }
        if (plan.Proficiency is { } proficiency) WriteProficiency(db,tx,owner.Account,expected.Id,proficiency.Before,proficiency.After);
        if (plan.ExperienceReward.Amount != 0 && CreditAdventure(db,tx,owner.Account,expected.Id,AdventureCreditSource.Earned,
            plan.ExperienceReward.Amount,EffectivePosition(expected.Level,expected.Learning.Experience),expected.Level,plan.TargetLevel,
            plan.FrozenAt) is { } credit)
            WriteAdventureCredit(db,tx,owner.Account,credit,AdventureCreditSource.Earned,adventureCatalog!);
        command.CommandText = """
            DELETE FROM character_active_main_quests
            WHERE character_id=$id AND slot=$slot AND quest_id=$quest AND acceptance_id=$acceptance AND condition_state=0
            """;
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("希望原接取不能原子清槽");
        WriteQuestConditions(db,tx,expected.Id,expected.ActiveMainQuests,plan.ActiveAfter);
        var granted = plan.Rewards.Select(row => new QuestGrantedItem(
            inventory.Inventory.Find(row.Position) ?? throw new InvalidDataException("希望奖励实际槽缺失"),row.GrantedCount)).ToImmutableArray();
        VerifyHopeRewards(plan,granted);
        foreach (var (name,value) in new (string,object)[] {
            ("$attempt",plan.Identity.TaskAttempt),("$maze",plan.Binding.MazeOrdinal),("$epoch",plan.Identity.Epoch),
            ("$generation",owner.Generation),("$beforeRevision",expected.Revision),("$snapshot",plan.Snapshot),
            ("$rewards",JsonSerializer.Serialize(granted)),("$changed",JsonSerializer.Serialize(granted.Select(row => row.Item).ToImmutableArray())),
            ("$created",plan.FrozenAt.ToUnixTimeSeconds()),("$beforeLevel",expected.Level),
            ("$beforeExperience",checked((long)expected.Learning.Experience)),("$requested",plan.ExperienceReward.Amount) }) command.Parameters.AddWithValue(name,value);
        command.CommandText = """
            INSERT INTO character_operation_hope_reward_receipts
                (character_id,account,instance_id,acceptance_id,task_attempt,maze_ordinal,quest_id,clear_epoch,generation,
                 before_revision,revision,plan_snapshot,rewards,created_at,before_level,after_level,before_experience,
                 after_experience,requested_experience,changed_items)
            VALUES($id,$account,$instance,$acceptance,$attempt,$maze,$quest,$epoch,$generation,$beforeRevision,$revision,
                 $snapshot,$rewards,$created,$beforeLevel,$level,$beforeExperience,$experience,$requested,$changed)
            """;
        command.ExecuteNonQuery();
        var committed = Require(db,tx,owner.Account,expected.Name);
        if (committed.Revision != expected.Revision+1 || !MatchesHopeCharacter(committed,expected,plan,inventory.Inventory,afterCommit: true))
            throw new InvalidOperationException("希望持久奖励与冻结结果不同");
        tx.Commit();
        return Result(committed,granted,false,true);

        OperationHopeRewardCommit Result(Character character,ImmutableArray<QuestGrantedItem> rows,bool duplicate,bool projection) =>
            new(character,rows,plan.ExperienceReward,checked((uint)(plan.TargetLearning.Experience-expected.Learning.Experience)),
                duplicate,projection,expected.Level,expected.Learning,plan.Inventory.ChangedSlots);
    });

    private static bool MatchesHopeCharacter(Character current,Character expected,OperationHopeRewardPlan plan,InventorySnapshot inventory,bool afterCommit) =>
        current.Id == expected.Id && current.Job == expected.Job && current.GrowType == expected.GrowType && current.Position == expected.Position &&
        current.Return == expected.Return && current.AcceptedStoryLevel == expected.AcceptedStoryLevel && current.ExpertJobType == expected.ExpertJobType &&
        current.EquipmentSlotFlags == expected.EquipmentSlotFlags && current.Level == (afterCommit ? plan.TargetLevel : expected.Level) &&
        SameLearning(current.Learning,afterCommit ? plan.TargetLearning : expected.Learning) &&
        current.ActiveMainQuests.SequenceEqual(afterCommit ? plan.ActiveAfter : expected.ActiveMainQuests) &&
        current.CompletedQuests.SequenceEqual(expected.CompletedQuests) && current.AccountCompletedQuests.SequenceEqual(expected.AccountCompletedQuests) &&
        QuestCycle.Same(current,expected) && SameInventory(current.Inventory,new(inventory,inventory,"hope_receipt_projection")) &&
        current.Skills.SequenceEqual(afterCommit && plan.GrowthPlan is not null ? plan.TargetLearning.Trees.Single(tree => tree.Context == plan.TargetLearning.ActiveContext).Skills
            .Select(skill => new CharacterSkill(skill.Id,skill.Level)).OrderBy(skill => skill.Id) : expected.Skills);

    private static void VerifyHopeRewards(OperationHopeRewardPlan plan,ImmutableArray<QuestGrantedItem> rewards)
    {
        if (rewards.IsDefault || rewards.Length != plan.Rewards.Length || rewards.Select(row => row.Item.Uid).Distinct().Count() != rewards.Length)
            throw new InvalidDataException("希望账本奖励缺失或真实UID重复");
        for (int index = 0; index < rewards.Length; index++)
        {
            var actual = rewards[index]; var expected = plan.Rewards[index];
            if (actual.Item.Uid <= 0 || actual.Item.Position != expected.Position || actual.Item.Core != expected.Core ||
                actual.GrantedCount != expected.GrantedCount || expected.ExpectedDestinationUid != 0 && actual.Item.Uid != expected.ExpectedDestinationUid ||
                actual.Item.Avatar is not null || actual.Item.Creature is not null)
                throw new InvalidDataException("希望实际奖励UID、槽位、核心或数量不同");
        }
    }
}
