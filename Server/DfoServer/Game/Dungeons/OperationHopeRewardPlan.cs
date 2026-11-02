using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Quests;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Quests;

namespace DfoServer.Game.Dungeons;

internal sealed record OperationHopeRewardPreparation(QuestCompletionOutcome Outcome, OperationHopeRewardPlan? Plan, string Reason)
{
    public QuestCompletionFailure Failure { get; init; }
}

internal sealed record OperationHopeRewardCommit(Character Character, ImmutableArray<QuestGrantedItem> Rewards,
    QuestExperienceReward ExperienceReward, uint GainedExperience, bool Duplicate, bool ProjectionCurrent,
    byte PreviousLevel, CharacterLearningState PreviousLearning, ImmutableArray<InventorySlot> ChangedSlots);

// 只由原 Run 的已提交死亡和终末通关冻结，不接收客户端上报的完成标记。
internal sealed class OperationHopeRewardPlan
{
    private OperationHopeRewardPlan(Character expected, OperationHopeMissionBinding binding, DungeonClearFact clear,
        InventoryPlan inventory, ImmutableArray<QuestPlannedItemReward> rewards, QuestExperienceReward experience,
        CharacterGrowthPlan? growth, ProficiencyAdvance? proficiency, ImmutableArray<ActiveMainQuest> activeAfter, DateTimeOffset now)
    {
        Expected = expected; Binding = binding; Identity = clear.Identity; Inventory = inventory; Rewards = rewards;
        ExperienceReward = experience; GrowthPlan = growth; Proficiency = proficiency; ActiveAfter = activeAfter; FrozenAt = now;
        Snapshot = JsonSerializer.Serialize(new { Identity, binding.MazeOrdinal, binding.Acceptance, binding.Mission,
            TargetDeathEvent = binding.TargetDeath!.Death.EventId, ClearMap = clear.ClearMap,
            ConfirmedDeathEvents = clear.ConfirmedDeathEvents.ToArray(), Rewards, ExperienceReward,
            TargetLevel, TargetLearning, Proficiency, FrozenAt });
    }

    internal DfoServer.Game.BattlePass.BattlePassOperationHopeCompletion? BattlePassCompletion { get; set; }
    public Character Expected { get; }
    public OperationHopeMissionBinding Binding { get; }
    public DungeonRunIdentity Identity { get; }
    public ushort QuestId => Binding.Acceptance.QuestId;
    public Guid AcceptanceId => Binding.Acceptance.AcceptanceId;
    public byte QuestSlot => Binding.Acceptance.Slot;
    public InventoryPlan Inventory { get; }
    public ImmutableArray<QuestPlannedItemReward> Rewards { get; }
    public QuestExperienceReward ExperienceReward { get; }
    public CharacterGrowthPlan? GrowthPlan { get; }
    public ProficiencyAdvance? Proficiency { get; }
    public CharacterLearningState TargetLearning => GrowthPlan?.Learning ?? Expected.Learning;
    public byte TargetLevel => GrowthPlan?.TargetLevel ?? Expected.Level;
    public ImmutableArray<ActiveMainQuest> ActiveAfter { get; }
    public DateTimeOffset FrozenAt { get; }
    public string Snapshot { get; }

    public static OperationHopeRewardPreparation Prepare(Character expected, OperationHopeCatalog catalog,
        OperationHopeMissionBinding binding, SinglePlayerDungeonRun run, QuestCatalog quests,
        QuestRewardParameterCatalog parameters, ItemCatalog items, GoldLimitCatalog goldLimits, EpicPieceCatalog pieces,
        CharacterGrowthCatalog growthCatalog, DateTimeOffset now, GrowthContractBenefits? growthContract = null,
        ExperienceBonusService? experienceBonuses = null, bool premiumPcRoom = false)
    {
        try
        {
            var active = expected.ActiveMainQuests.SingleOrDefault(row => row.AcceptanceId == binding.Acceptance.AcceptanceId);
            if (active is null || active.Slot >= 30 || expected.Id != binding.Identity.CharacterId ||
                !catalog.Missions.TryGetValue(active.QuestId,out var mission) || !ReferenceEquals(mission,binding.Mission) ||
                !OperationHopePlan.CanClaim(catalog,binding,run,active))
                throw new InvalidDataException("希望领奖缺少原接取、目标死亡或本次终末通关");
            var grant = InventoryRewardGrantService.TryGrantBatch(expected,
                [new InventoryReward(mission.RewardItemId,mission.RewardCount)],items,goldLimits,pieces,now);
            if (!grant.Accepted || grant.Plan is null)
                return new(QuestCompletionOutcome.InsufficientSpace,null,"希望奖励无法整批进入背包") {
                    Failure = grant.Error == 17 ? QuestCompletionFailure.InventoryCapacity : QuestCompletionFailure.InventoryUnsupported };
            if (!grant.Plan.ChangedCurrencies.IsEmpty || !grant.Plan.ChangedEpicPieces.IsEmpty ||
                grant.Plan.ChangedSlots.Any(slot => slot.List != InventoryListType.Main))
                throw new NotSupportedException("希望原材料奖励不能改写其他持有域");
            var rewards = ImmutableArray.CreateBuilder<QuestPlannedItemReward>();
            int total = 0;
            foreach (var slot in grant.Plan.ChangedSlots)
            {
                var item = grant.Plan.After.Find(slot) ?? throw new InvalidDataException("希望奖励缺少目的槽");
                var old = expected.Inventory.Find(slot);
                if (item.Core.ItemId != mission.RewardItemId || !item.Core.IsStackable)
                    throw new InvalidDataException("希望奖励必须为原材料堆叠");
                int delta = checked(item.Core.Count - (old?.Core.ItemId == item.Core.ItemId ? old.Core.Count : 0));
                if (delta <= 0) throw new InvalidDataException("希望奖励增量必须为正");
                rewards.Add(new(slot,item.Core,delta,item.Uid)); total = checked(total + delta);
            }
            if (total != mission.RewardCount) throw new InvalidDataException("希望奖励不能截断原数量");
            var experience = QuestExperiencePolicy.Prepare(expected,mission.Quest,parameters);
            // grade29 默认 not give exp quest；保留原 F 权重/基础表证据，不伪改 QST 或叠加经验加成。
            if (mission.SuppressExperience) experience = experience with { Amount = 0, GrowthContractExperience = 0, Bonuses = null };
            uint contract = mission.SuppressExperience ? 0 : GrowthContractPolicy.ExperienceBonus(expected,experience.Amount,growthContract,now.ToUnixTimeSeconds());
            var bonuses = mission.SuppressExperience ? null : experienceBonuses?.Plan(new(expected,ExperienceEvent.Quest,8523,now.ToUnixTimeSeconds(),premiumPcRoom) {
                UnixMilliseconds = now.ToUnixTimeMilliseconds() },experience.Amount,0,growthContract);
            experience = experience with { Amount = bonuses?.Total ?? checked(experience.Amount + contract),
                GrowthContractExperience = bonuses?.Amount(ExperienceBonusSource.GrowthContract) ?? contract, Bonuses = bonuses };
            CharacterGrowthPlan? growth = null;
            ProficiencyAdvance? proficiency = null;
            if (experience.Amount != 0)
            {
                if (expected.DungeonState is not { } dungeon) throw new InvalidDataException("希望经验缺少成长附属状态");
                var result = CharacterGrowthPlan.AddExperience(expected,experience.Amount,growthCatalog);
                if (!result.Success || result.Plan is null) throw new InvalidDataException("希望经验成长规划失败：" + result.Failure);
                growth = result.Plan;
                proficiency = ProficiencyProgression.Advance(new(dungeon.ProficiencyLevel,dungeon.ProficiencyExperience),
                    ProficiencyProgression.Gain(growth.PreviousLevel,growth.Before.Experience,experience.Amount,growthCatalog),growthCatalog);
            }
            // 不加入普通永久完成集合；新实例的新接取仍能领取。其它任务只按真正的新库存/等级刷新。
            var after = QuestInventoryProgress.Refresh(expected.ActiveMainQuests.Where(row => row.AcceptanceId != active.AcceptanceId).ToArray(),
                grant.Plan.After,quests,now.ToUnixTimeSeconds());
            after = QuestStateProgress.Refresh(after,expected with { Inventory = grant.Plan.After, Level = growth?.TargetLevel ?? expected.Level },quests);
            return new(QuestCompletionOutcome.Accepted,new(expected,binding,run.ClearFact!,
                new(expected.Inventory,grant.Plan.After,"operation_hope_reward"),rewards.ToImmutable(),experience,growth,proficiency,after.ToImmutableArray(),now),"希望实例奖励已冻结");
        }
        catch (Exception error) when (error is InvalidDataException or NotSupportedException or OverflowException or ArgumentOutOfRangeException)
        {
            return new(QuestCompletionOutcome.Rejected,null,error.Message) { Failure = QuestCompletionFailure.ResourceUnsupported };
        }
    }
}
