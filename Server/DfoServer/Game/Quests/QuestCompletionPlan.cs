using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Quests;

namespace DfoServer.Game.Quests;

internal enum QuestCompletionOutcome { Accepted, Rejected, InsufficientSpace, Unsupported }
internal enum QuestCompletionFailure { None, EligibilityChanged, InventoryCapacity, InventoryUnsupported, GrowthRejected, ResourceUnsupported }
internal sealed record QuestCompletionPreparation(QuestCompletionOutcome Outcome, QuestCompletionPlan? Plan, string Reason)
{
    public QuestCompletionFailure Failure { get; init; }
}
internal sealed record QuestPlannedItemReward(InventorySlot Position, ItemCore Core, int GrantedCount, long ExpectedDestinationUid);

// 没有在线状态或提交能力；原角色、稳定clear和全部奖励候选由同一Session/Repository事务接管。
internal sealed class QuestCompletionPlan
{
    QuestCompletionPlan(Character expected, QuestClearReceipt clear, QuestExperienceReward experience, InventoryPlan inventory,
        CharacterGrowthPlan growth, ImmutableArray<QuestPlannedItemReward> rewards, DateTimeOffset frozenAt)
    {
        Expected = expected;
        Clear = clear;
        ExperienceReward = experience;
        Inventory = inventory;
        Growth = growth;
        Rewards = rewards;
        FrozenAt = frozenAt;
        ActiveAfter = expected.ActiveMainQuests.Where(active => active.AcceptanceId != clear.AcceptanceId).ToImmutableArray();
        CompletedAfter = expected.CompletedQuests.Append(clear.QuestId).Order().ToImmutableArray();
    }

    public Character Expected { get; }
    public QuestClearReceipt Clear { get; }
    public QuestExperienceReward ExperienceReward { get; }
    public InventoryPlan Inventory { get; }
    public CharacterGrowthPlan Growth { get; }
    public ImmutableArray<QuestPlannedItemReward> Rewards { get; }
    public ImmutableArray<ActiveMainQuest> ActiveAfter { get; }
    public ImmutableArray<int> CompletedAfter { get; }
    public DateTimeOffset FrozenAt { get; }
    // native3145固定奖励没有item0行；不从Gold参数表补一个原资源未声明的币种。
    public uint GoldAmount => 0;

    public static QuestCompletionPreparation Prepare(Character expected, QuestClearReceipt clear, StoryQuest quest,
        QuestRewardParameterCatalog parameters, ItemCatalog items, GoldLimitCatalog goldLimits, EpicPieceCatalog pieces,
        CharacterGrowthCatalog growthCatalog, DateTimeOffset utcNow)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(clear);
        if (!MatchesClearedActiveQuest(expected, clear))
            return new(QuestCompletionOutcome.Rejected, null, "原3145接取/clear/活动槽或完成表已变化")
                { Failure = QuestCompletionFailure.EligibilityChanged };
        QuestExperienceReward experience;
        InventoryResult inventory;
        try
        {
            experience = QuestExperiencePolicy.Prepare(expected, quest, parameters);
            inventory = QuestFixedRewardGrant.Prepare(expected, quest, items, goldLimits, pieces, utcNow);
        }
        catch (NotSupportedException error)
        {
            return new(QuestCompletionOutcome.Unsupported, null, error.Message) { Failure = QuestCompletionFailure.ResourceUnsupported };
        }
        if (!inventory.Accepted || inventory.Plan is null || !inventory.Plan.Changed)
            return new(QuestCompletionOutcome.InsufficientSpace, null, "七项固定奖励无法完整进入实际背包")
                { Failure = inventory.Error == 17 ? QuestCompletionFailure.InventoryCapacity : QuestCompletionFailure.InventoryUnsupported };
        var growth = CharacterGrowthPlan.AddExperience(expected, experience.Amount, growthCatalog);
        if (!growth.Success || growth.Plan is null)
            return new(QuestCompletionOutcome.Rejected, null, "本次任务成长资格无效: " + growth.Failure)
                { Failure = QuestCompletionFailure.GrowthRejected };
        var rewards = ImmutableArray.CreateBuilder<QuestPlannedItemReward>();
        foreach (var resourceReward in QuestFixedRewardDefinition.Parse(quest).ForJob(expected.Job))
        {
            var destinations = inventory.Plan.ChangedSlots.Select(inventory.Plan.AfterIndex.Find)
                .Where(item => item is not null && item.Core.ItemId == resourceReward.ItemId).ToArray();
            if (destinations.Length != 1 || destinations[0] is not { } destination || destination.Position.List != InventoryListType.Main)
                throw new InvalidDataException("任务奖励没有唯一实际背包目的槽");
            var before = inventory.Plan.BeforeIndex.Find(destination.Position);
            int actualCount = destination.Core.IsStackable ? destination.Core.Count - (before?.Core.Count ?? 0) : 1;
            if (actualCount != resourceReward.Count)
                throw new InvalidDataException("固定任务奖励规划的实际增量与资源不同");
            rewards.Add(new(destination.Position, destination.Core, actualCount, before?.Uid ?? 0));
        }
        if (rewards.Count != 7 || rewards.Select(reward => reward.Position).Distinct().Count() != rewards.Count ||
            inventory.Plan.After.Gold != expected.Inventory.Gold || inventory.Plan.ChangedCurrencies.Length != 0 ||
            inventory.Plan.ChangedEpicPieces.Length != 0)
            throw new InvalidDataException("3145计划不能缩减固定奖励或夹带货币/碎片变化");
        return new(QuestCompletionOutcome.Accepted, new(expected, clear, experience, inventory.Plan, growth.Plan,
            rewards.ToImmutable(), utcNow), "3145完整领奖计划已冻结，尚未提交");
    }

    public static bool MatchesClearedActiveQuest(Character character, QuestClearReceipt clear)
    {
        if (character.Id == 0 || character.Revision < clear.Revision || clear.Revision <= 0 || clear.QuestId != 3145 || clear.QuestSlot > 29 ||
            clear.AcceptanceId == Guid.Empty || clear.EventId == Guid.Empty || clear.InstanceId == Guid.Empty ||
            clear.TaskAttempt <= 0 || clear.Epoch <= 0 || character.CompletedQuests.Contains(3145) ||
            character.CompletedQuests.Distinct().Count() != character.CompletedQuests.Length)
            return false;
        var matches = character.ActiveMainQuests.Where(active => active.QuestId == clear.QuestId || active.Slot == clear.QuestSlot ||
            active.AcceptanceId == clear.AcceptanceId).ToArray();
        return matches.Length == 1 && matches[0].QuestId == clear.QuestId && matches[0].Slot == clear.QuestSlot &&
            matches[0].AcceptanceId == clear.AcceptanceId && matches[0].ConditionState == 0;
    }
}
