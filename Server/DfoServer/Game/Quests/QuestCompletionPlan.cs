using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
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
internal sealed record QuestConsumedItem(InventorySlot Position, long Uid, int ItemId, int Count);
internal sealed record QuestCurrencyReward(CurrencyKind Kind,int ItemId,int Count);

// 没有在线状态或提交能力；原角色、稳定clear和全部奖励候选由同一Session/Repository事务接管。
internal sealed class QuestCompletionPlan
{
    QuestCompletionPlan(Character expected, ActiveMainQuest active, StoryQuest quest, ushort selector,
        QuestExperienceReward experience, InventoryPlan inventory, CharacterGrowthPlan? growth,
        ImmutableArray<QuestPlannedItemReward> rewards, ImmutableArray<QuestConsumedItem> consumed,
        uint gold, int goldBase, int goldMultiple, int penalty, DateTimeOffset now, QuestCatalog quests, QuestClearReceipt? clear,
        byte? profession, CharacterLearningState? rewardLearning, ushort multiplier, ImmutableArray<QuestCurrencyReward> currencies,
        byte equipmentSlots, ushort tpAmount, ImmutableArray<ActiveMainQuest> closedChildren, InventoryPlan? reclaim, uint mailedGold,
        int? completedParentId = null)
    {
        Expected = expected; QuestId = active.QuestId; AcceptanceId = active.AcceptanceId; QuestSlot = active.Slot;
        CompletedParentId = completedParentId;
        AuthoritativeClear = clear;
        ConditionType = quest.Condition switch { "[clear map]" => 3, "[condition under clear]" => 1, "[seeking]" => 0, "[seek n meet npc]" => 25, "[hunt enemy]" => 6, "[hunt monster]" => 2,
            "[custom quest]" when QuestRentalProgress.Supports(quest) => 19,
            "[quest clear]" => 44, "[clear multi quest]" => 70, "[question]" => 42, "[reach the range]" => 64, "[look cinematic]" => 65,
            _ => QuestStateProgress.Type(quest) ?? (byte)4 };
        // 历史归档不是新的通关领奖，沿用已有无清图凭据的零奖励回执类型，不改库结构。
        if (completedParentId is not null && ConditionType is 1 or 3) ConditionType = 4;
        RewardSelector = selector;
        ExperienceReward = experience; Inventory = inventory; GrowthPlan = growth; Rewards = rewards; Consumed = consumed;
        Multiplier = multiplier;
        Currencies = currencies;
        GoldAmount = gold; MailedGoldAmount = mailedGold; FrozenAt = now;
        DailyPeriod = QuestCycle.Daily(quest) ? QuestCycle.Day(now) : 0;
        AccountQuest = quest.Definition!.AccountQuest;
        AccountDailyClaimsAfter = AccountQuest && DailyPeriod != 0 ? expected.AccountQuestDailyClaims.SetItem(quest.Id,DailyPeriod) : expected.AccountQuestDailyClaims;
        Answer = quest.Condition == "[question]" ? checked((byte)active.ConditionState) : null;
        AnswersAfter = Answer is { } answer ? expected.QuestAnswers.SetItem(quest.Id,answer) : expected.QuestAnswers;
        AccountAnswersAfter = AccountQuest && Answer is { } accountAnswer ? expected.AccountQuestAnswers.SetItem(quest.Id,accountAnswer) : expected.AccountQuestAnswers;
        ProfessionGrowType = profession; RewardLearning = rewardLearning; TargetEquipmentSlotFlags = equipmentSlots; TpAmount = tpAmount;
        ClosedChildren = closedChildren;
        CascadeMarkerQuestId = QuestRelationProgress.CascadeMarker(quest,quests);
        SyncedQuestIds = (CascadeMarkerQuestId is { } synced && QuestRelationProgress.IsAccountSyncMarker(quests.Get(synced))
            ? new[] { synced } : []).Except(expected.AccountCompletedQuests).ToImmutableArray();
        AccountCompletedAfter = (AccountQuest ? expected.AccountCompletedQuests.Append(quest.Id) : expected.AccountCompletedQuests)
            .Concat(SyncedQuestIds).Distinct().Order().ToImmutableArray();
        NewCompletedQuestIds = new[] { (int)quest.Id }.Concat(CascadeMarkerQuestId is { } marker ? new[] { marker } : [])
            .Except(QuestEligibility.Completed(expected)).Order().ToImmutableArray();
        CompletedAfter = expected.CompletedQuests.Concat(NewCompletedQuestIds).Distinct().Order().ToImmutableArray();
        var activeAfter = QuestRelationProgress.Refresh(QuestInventoryProgress.Refresh(
            expected.ActiveMainQuests.Where(row => row.AcceptanceId != active.AcceptanceId &&
                !ClosedChildren.Any(child => child.AcceptanceId == row.AcceptanceId)).ToArray(), inventory.After, quests, now.ToUnixTimeSeconds()),
            CompletedAfter.Concat(AccountCompletedAfter).ToArray(), quests);
        ActiveAfter = QuestStateProgress.Refresh(activeAfter,expected with { Inventory = inventory.After, Level = TargetLevel,
            GrowType = TargetGrowType, CompletedQuests = CompletedAfter.ToArray(), AccountCompletedQuests = AccountCompletedAfter.ToArray() },quests);
        Snapshot = JsonSerializer.Serialize(new { QuestId, AcceptanceId, QuestSlot, ConditionType, RewardSelector, Multiplier,
            ExperienceReward, GoldAmount, goldBase, goldMultiple, penalty, Consumed, Rewards, Currencies, FrozenAt, DailyPeriod, AccountQuest,
            AuthoritativeClear, Answer, Learning = TargetLearning, Level = TargetLevel, GrowType = TargetGrowType, EquipmentSlotFlags = TargetEquipmentSlotFlags });
        if (CompletedParentId is { } completedParent)
        {
            var snapshot = System.Text.Json.Nodes.JsonNode.Parse(Snapshot)!.AsObject();
            snapshot[nameof(CompletedParentId)] = completedParent;
            Snapshot = snapshot.ToJsonString();
        }
        if (CascadeMarkerQuestId is { } cascade)
        {
            // 普通任务保持历史Snapshot原字节；仅真实cascade规划追加已确认的关联ID。
            var snapshot = System.Text.Json.Nodes.JsonNode.Parse(Snapshot)!.AsObject();
            snapshot[nameof(CascadeMarkerQuestId)] = cascade;
            Snapshot = snapshot.ToJsonString();
        }
        if (TpAmount != 0)
        {
            // 只有TP领奖追加赠点身份；普通任务及其历史回执Snapshot保持原字节。
            var snapshot = System.Text.Json.Nodes.JsonNode.Parse(Snapshot)!.AsObject();
            snapshot[nameof(TpAmount)] = TpAmount;
            Snapshot = snapshot.ToJsonString();
        }
        if (MailedGoldAmount != 0)
        {
            // 只有整笔金币转邮件时追加字段；普通任务及历史回执保持原Snapshot字节。
            var snapshot = System.Text.Json.Nodes.JsonNode.Parse(Snapshot)!.AsObject();
            snapshot[nameof(MailedGoldAmount)] = MailedGoldAmount;
            Snapshot = snapshot.ToJsonString();
        }
        if (ConditionType == 70)
        {
            var snapshot = System.Text.Json.Nodes.JsonNode.Parse(Snapshot)!.AsObject();
            snapshot["Threshold"] = JsonSerializer.SerializeToNode(new {
                RequiredChildren = quest.IntData, quest.Definition.MinimumClearCount, ClosedChildren,
                ReclaimedItems = reclaim!.ChangedSlots.Select(slot => new {
                    Slot = slot, Before = reclaim.Before.Find(slot), After = reclaim.After.Find(slot)
                }).ToArray()
            });
            Snapshot = snapshot.ToJsonString();
        }
    }

    public Character Expected { get; }
    public QuestClearReceipt? AuthoritativeClear { get; }
    public QuestClearReceipt Clear => AuthoritativeClear ?? throw new InvalidOperationException("此任务不使用清图凭据");
    public ushort QuestId { get; }
    public int? CompletedParentId { get; }
    public Guid AcceptanceId { get; }
    public byte QuestSlot { get; }
    public byte ConditionType { get; }
    public ushort RewardSelector { get; } = ushort.MaxValue;
    public ushort Multiplier { get; }
    public string? Snapshot { get; }
    public QuestExperienceReward ExperienceReward { get; }
    public InventoryPlan Inventory { get; }
    public CharacterGrowthPlan? GrowthPlan { get; }
    public CharacterGrowthPlan Growth => GrowthPlan ?? throw new InvalidOperationException("零经验任务不需要成长规划");
    public byte? ProfessionGrowType { get; }
    private CharacterLearningState? RewardLearning { get; }
    public bool UpdatesLearning => GrowthPlan is not null || RewardLearning is not null;
    public byte TargetEquipmentSlotFlags { get; }
    public ushort TpAmount { get; }
    public ImmutableArray<ActiveMainQuest> ClosedChildren { get; }
    public byte TargetGrowType => ProfessionGrowType ?? Expected.GrowType;
    public CharacterLearningState TargetLearning => RewardLearning ?? GrowthPlan?.Learning ?? Expected.Learning;
    public byte TargetLevel => GrowthPlan?.TargetLevel ?? Expected.Level;
    public ImmutableArray<QuestPlannedItemReward> Rewards { get; }
    public ImmutableArray<QuestConsumedItem> Consumed { get; } = [];
    public ImmutableArray<QuestCurrencyReward> Currencies { get; }
    public ImmutableArray<ActiveMainQuest> ActiveAfter { get; }
    public ImmutableArray<int> CompletedAfter { get; }
    public int? CascadeMarkerQuestId { get; }
    public ImmutableArray<int> SyncedQuestIds { get; }
    public ImmutableArray<int> NewCompletedQuestIds { get; }
    public DateTimeOffset FrozenAt { get; }
    public int DailyPeriod { get; }
    public bool AccountQuest { get; }
    public ImmutableArray<int> AccountCompletedAfter { get; }
    public ImmutableDictionary<int,int> AccountDailyClaimsAfter { get; }
    public byte? Answer { get; }
    public ImmutableDictionary<int,byte> AnswersAfter { get; }
    public ImmutableDictionary<int,byte> AccountAnswersAfter { get; }
    // 只在任务资源声明金币时计算。
    public uint GoldAmount { get; }
    public uint MailedGoldAmount { get; }
    public uint DirectGoldAmount => GoldAmount - MailedGoldAmount;

    public static QuestCompletionPreparation Prepare(Character expected, StoryQuest quest, ushort selector,
        QuestCatalog quests, QuestRewardParameterCatalog parameters, ItemCatalog items, GoldLimitCatalog goldLimits,
        EpicPieceCatalog pieces, CharacterGrowthCatalog growthCatalog, DateTimeOffset now, QuestClearReceipt? clear = null, ushort multiplier = 1,
        GrowthContractBenefits? growthContract = null)
    {
        try
        {
            QuestCoverage.RequireOrdinaryDeclarations(quest);
            // 参考两端的批量合同只用于普通重复寻物；一次接取、一次事务，不循环签发清图或职业奖励。
            if (multiplier == 0 || multiplier != 1 && !(QuestCycle.Repeat(quest) && quest.Condition == "[seeking]" &&
                QuestLifecyclePlan.ValidSeeking(quest) && quest.RewardType == "[item]"))
                throw new InvalidDataException("交付倍数只适用于具有实际材料的普通重复寻物任务");
            var active = expected.ActiveMainQuests.SingleOrDefault(row => row.QuestId == quest.Id);
            var completedParent = QuestRelationProgress.CompletedParentChild(expected,quest,quests);
            if (active is null || active.AcceptanceId == Guid.Empty || active.Slot >= 30 ||
                QuestEligibility.Completed(expected).Contains(quest.Id) || expected.CompletedQuests.Distinct().Count() != expected.CompletedQuests.Length ||
                QuestEligibility.InspectResource(quest).Entry != QuestResourceEntry.Ordinary && !QuestRelationProgress.DialogueChild(quest, quests) &&
                    !QuestRelationProgress.ThresholdParent(quest,quests) && !QuestHiddenEntry.Direct(quest,quests) ||
                completedParent is null && !QuestEligibility.Evaluate(expected with { ActiveMainQuests = expected.ActiveMainQuests.Where(row => row != active).ToArray() }, quest, quests).Eligible)
                throw new InvalidDataException("任务接取身份、完成位或当前资格不符");
            if (completedParent is not null)
            {
                if (selector != ushort.MaxValue || multiplier != 1 || clear is not null)
                    throw new InvalidDataException("已完成父任务的子任务归档不能附带领奖选择、倍数或清图交付");
                var zero = QuestExperiencePolicy.Prepare(expected,quest,parameters,out int zeroPenalty);
                return new(QuestCompletionOutcome.Accepted,new(expected,active,quest,selector,zero,
                    new(expected.Inventory,expected.Inventory,"quest_completed_parent_child"),null,[],[],0,0,0,zeroPenalty,
                    now,quests,null,null,null,1,[],expected.EquipmentSlotFlags,0,[],null,0,completedParent),"已完成父任务的终末子任务归档");
            }
            bool threshold = QuestRelationProgress.ThresholdParent(quest,quests);
            bool parent = QuestRelationProgress.SequentialParent(quest,quests) || threshold;
            byte? profession = quest.RewardType is "[grow type]" or "[awakening type]"
                ? QuestProfessionReward.Target(expected, quest, growthCatalog.Learning ?? throw new NotSupportedException("职业变化技能资源未装配")) : null;
            _ = QuestCharacterReward.TryTp(quest,out ushort tpAmount);
            bool characterReward = QuestCharacterReward.TryScalar(quest,out _) || tpAmount != 0;
            bool dungeonClear = QuestDungeonClearProgress.TryTarget(quest,out var dungeonTarget);
            bool rental = QuestRentalProgress.Supports(quest);
            if (quest.SubType != -1 && !parent && !QuestClientSceneProgress.TownRange(quest) && !dungeonClear && !rental && QuestStateProgress.Type(quest) is null || quest.RewardType != "[item]" && profession is null && !characterReward && !QuestRelationProgress.ZeroRewardChild(quest) ||
                !QuestCoverage.HasOrdinaryAttributes(quest) ||
                quest.Definition!.Has("send postal"))
                throw new NotSupportedException("任务需要尚未具备的周期、特殊奖励或附属交付规则");
            if (QuestCycle.Daily(quest) && expected.QuestCycleDay != QuestCycle.Day(now))
                throw new InvalidDataException("每日任务交付需要当前游戏日快照");
            if (quest.Condition == "[clear map]")
            {
                if (!QuestClearEligibility.IsSingleMap(quest) || clear is null || !MatchesClearedActiveQuest(expected, clear) ||
                    clear.QuestId != quest.Id || clear.MapId != quest.IntData[0] ||
                    quest.DungeonIndex is >= 0 && clear.DungeonId != quest.DungeonIndex)
                    throw new InvalidDataException("清图交付必须使用当前接取及目标地图的权威凭据");
            }
            else if (dungeonClear)
            {
                if (clear is null || !MatchesClearedActiveQuest(expected,clear) || clear.QuestId != quest.Id ||
                    clear.DungeonId != dungeonTarget.DungeonId || clear.MapId <= 0)
                    throw new InvalidDataException("条件通关交付需要当前接取次数归零及最后达成的权威凭据");
            }
            else if (clear is not null) throw new InvalidDataException("此任务条件不能使用清图凭据");
            else if (QuestStateProgress.Type(quest) is not null)
            {
                if (active.ConditionState != 0 || QuestStateProgress.Scalar(expected,quest) != 0)
                    throw new InvalidDataException("角色实际状态或本次接取事实尚未满足目标");
            }
            else if (quest.Condition == "[meet npc]")
            {
                if (active.ConditionState != 0 || quest.IntData.Count != 1 || quest.IntData[0] <= 0)
                    throw new InvalidDataException("会面尚未由当前场景完成");
            }
            else if (quest.Condition == "[question]")
            {
                if (!QuestQuestionProgress.OrdinaryChoice(quest,active.ConditionState,quests))
                    throw new InvalidDataException("回答尚未从当前NPC选定，或选项需要专用跳过入口");
            }
            else if (quest.Condition is "[hunt enemy]" or "[hunt monster]")
            {
                if (active.ConditionState != 0 || !QuestHuntProgress.TryTargets(quest,out _))
                    throw new InvalidDataException("猎杀尚未由本次接取的权威死亡事实达成");
            }
            else if (quest.Condition == "[quest clear]")
            {
                if (!parent || active.ConditionState != 0 || !quest.IntData.All(expected.CompletedQuests.Contains))
                    throw new InvalidDataException("父任务必须具有全部真实子任务完成记录");
            }
            else if (quest.Condition == "[clear multi quest]")
            {
                if (!threshold || active.ConditionState != 0 ||
                    QuestRelationProgress.ThresholdScalar(quest,QuestEligibility.Completed(expected)) != 0)
                    throw new InvalidDataException("阈值父任务必须达到真实子任务完成数量");
            }
            else if (QuestClientSceneProgress.TownRange(quest) || QuestClientSceneProgress.Cinematic(quest,out _,out _) ||
                QuestClientSceneProgress.DungeonCinematic(quest))
            {
                if (active.ConditionState != 0) throw new InvalidDataException("范围或演出的原生终末事件尚未达成");
            }
            else if (rental)
            {
                if (active.ConditionState != 0) throw new InvalidDataException("租赁任务需要本次接取的已提交租赁事实");
            }
            else if (quest.Condition is not ("[seeking]" or "[seek n meet npc]") || !QuestLifecyclePlan.ValidSeeking(quest))
                throw new NotSupportedException("此交付条件尚未接入权威事件");
            else if (quest.Condition == "[seek n meet npc]" && active.ConditionState != 0)
                throw new InvalidDataException("寻物会面必须同时满足实际材料和会面通道");

            InventoryPlan? reclaim = null;
            ImmutableArray<ActiveMainQuest> closedChildren = [];
            if (threshold)
            {
                var closure = QuestLifecyclePlan.CloseThresholdChildren(expected,quest,quests,now);
                reclaim = closure.Inventory;
                closedChildren = closure.Removed;
            }
            // 先关闭未选子任务并回收其任务物品，再规划父奖励，避免回收刚发的同ID奖励。
            var paid = reclaim?.After ?? expected.Inventory;
            var consumed = ImmutableArray.CreateBuilder<QuestConsumedItem>();
            if (quest.Condition is "[seeking]" or "[seek n meet npc]")
            {
                var wanted = new Dictionary<int, long>();
                for (int at = 0; at < QuestLifecyclePlan.MaterialLength(quest); at += 2)
                    wanted[quest.IntData[at]] = checked(wanted.GetValueOrDefault(quest.IntData[at]) + (long)quest.IntData[at + 1]*multiplier);
                foreach (var requirement in wanted)
                {
                    if (CurrencyWallet.RewardKind(requirement.Key) is { } kind)
                    {
                        if (ItemSlotBoundService.CurrencySlot(kind) is not { } slot)
                            throw new NotSupportedException("此任务材料没有本版背包持有域");
                        var spend = CurrencyService.Spend(paid,kind,checked((int)requirement.Value));
                        if (!spend.Accepted) throw new InvalidDataException("任务钱包材料余额不足");
                        paid = spend.Plan!.After;
                        consumed.Add(new(new(InventoryListType.Main,slot),0,requirement.Key,checked((int)requirement.Value)));
                        continue;
                    }
                    long remaining = requirement.Value;
                    foreach (var item in paid.Items.Where(item => item.Position.List == InventoryListType.Main &&
                        item.Core.ItemId == requirement.Key && !item.Core.IsExpired(now.ToUnixTimeSeconds()) && item.Core.EquipmentLockId == 0)
                        .OrderBy(item => item.Position.Index).ToArray())
                    {
                        int count = (int)Math.Min(remaining, item.Core.Count);
                        var deletion = InventoryDeleteService.Plan(paid, item.Position, item.Core.Value, count);
                        if (!deletion.Accepted) throw new InvalidDataException("任务消耗实例不能扣除");
                        paid = deletion.Plan!.After; consumed.Add(new(item.Position, item.Uid, item.Core.ItemId, count));
                        remaining -= count;
                        if (remaining == 0) break;
                    }
                    if (remaining != 0) throw new InvalidDataException("实际未锁定、未过期库存不足");
                }
                if (consumed.Count > byte.MaxValue) throw new NotSupportedException("任务消耗数组超过本版容量");
            }
            var definition = QuestFixedRewardDefinition.Parse(profession is not null || characterReward ? quest with { Rewards = [] } : quest, allowGold: true);
            var requested = definition.ForJob(expected.Job,expected.GrowType).ToBuilder();
            var selected = QuestFixedRewardDefinition.Parse(quest with { Rewards = quest.Definition!.SelectRewards }).ForJob(expected.Job,expected.GrowType);
            if (selected.Length == 0 ? selector != ushort.MaxValue : selector >= selected.Length)
                throw new InvalidDataException("选择奖励索引不属于当前职业候选");
            if (selected.Length != 0) requested.Add(selected[selector]);
            var experience = QuestExperiencePolicy.Prepare(expected, quest, parameters, out int penalty);
            uint contractBonus = GrowthContractPolicy.ExperienceBonus(expected, experience.Amount, growthContract, now.ToUnixTimeSeconds());
            experience = experience with { Amount = checked(experience.Amount + contractBonus), GrowthContractExperience = contractBonus };
            ulong scaledExperience = checked((ulong)experience.Amount*multiplier);
            if (scaledExperience > int.MaxValue) throw new InvalidDataException("批量任务经验超出本版范围");
            experience = experience with { Amount = (uint)scaledExperience,
                GrowthContractExperience = checked(experience.GrowthContractExperience * multiplier) };
            for (int at = 0; at < requested.Count; at++) requested[at] = requested[at] with { Count = checked(requested[at].Count*multiplier) };
            int goldBase = 0, goldMultiple = quest.DeclaredGoldMultiple ?? 100;
            uint gold = 0;
            // 禁经验子任务的0/0是零奖励占位，不能借普通任务的动态金币marker发奖。
            if (!QuestRelationProgress.ZeroRewardChild(quest) && (definition.HasGoldMarker || quest.DeclaredGoldMultiple > 0))
            {
                if (definition.FixedGoldAmount > 0 && quest.DeclaredGoldMultiple is not > 0)
                    gold = definition.FixedGoldAmount;
                else
                {
                    if (experience.NativeIndex >= parameters.Gold.Length || goldMultiple <= 0)
                        throw new NotSupportedException("金币声明需要完整表索引及正倍率");
                    goldBase = parameters.Gold[experience.NativeIndex];
                    if (goldBase < 0) throw new InvalidDataException("任务Gold基础不能为负");
                    ulong amount = checked((ulong)goldBase * (ulong)goldMultiple) / 100;
                    amount = checked(amount * (ulong)penalty) / 100;
                    if (amount > int.MaxValue) throw new NotSupportedException("任务金币超出本版额度");
                    gold = (uint)amount;
                }
            }
            ulong scaledGold = checked((ulong)gold*multiplier);
            if (scaledGold > int.MaxValue) throw new InvalidDataException("批量任务金币超出本版范围");
            gold = (uint)scaledGold;
            var grant = InventoryRewardGrantService.TryGrantBatch(expected with { Inventory = paid },
                requested.Select(row => new InventoryReward(row.ItemId, row.Count)).ToArray(), items, goldLimits, pieces, now);
            if (!grant.Accepted || grant.Plan is null)
                return new(QuestCompletionOutcome.InsufficientSpace, null, "奖励无法整批进入持有域")
                    { Failure = grant.Error == 17 ? QuestCompletionFailure.InventoryCapacity : QuestCompletionFailure.InventoryUnsupported };
            var after = grant.Plan.After;
            if (grant.Plan.ChangedCurrencies.Any(kind => ItemSlotBoundService.CurrencySlot(kind) is null) || grant.Plan.ChangedEpicPieces.Length != 0 ||
                grant.Plan.ChangedSlots.Any(slot => slot.List != InventoryListType.Main))
                throw new NotSupportedException("任务特殊持有域奖励尚未确认");
            uint mailedGold = 0;
            if (gold != 0)
            {
                var currency = new CurrencyService(goldLimits).Grant(expected with { Inventory = after }, CurrencyKind.Gold, checked((int)gold));
                if (!currency.Accepted || currency.Plan is null)
                    throw new InvalidDataException("任务金币无法完整接纳");
                if (currency.Plan.After.Gold - after.Gold == gold)
                    after = currency.Plan.After;
                else
                    // 整笔本次奖励保留到永久邮件；不部分截断，也不改变玩家已有超限余额。
                    mailedGold = gold;
            }
            var rewards = ImmutableArray.CreateBuilder<QuestPlannedItemReward>();
            var currencies = ImmutableArray.CreateBuilder<QuestCurrencyReward>();
            foreach (int id in requested.Select(row => row.ItemId).Distinct())
            {
                if (CurrencyWallet.RewardKind(id) is { } currency)
                {
                    if (currencies.Any(row => row.Kind == currency)) continue;
                    int amount = requested.Where(row => CurrencyWallet.RewardKind(row.ItemId) == currency).Sum(row => row.Count);
                    if (ItemSlotBoundService.CurrencySlot(currency) is null || CurrencyWallet.Get(after,currency)-CurrencyWallet.Get(paid,currency) != amount)
                        throw new InvalidDataException("任务钱包奖励无法完整接纳");
                    currencies.Add(new(currency,currency == CurrencyKind.ReviveCoin ? 1 : id,amount));
                    continue;
                }
                int total = 0;
                foreach (var item in after.Items.Where(item => item.Core.ItemId == id).OrderBy(item => item.Position.Index))
                {
                    var old = paid.Find(item.Position);
                    int delta = item.Core.IsStackable ? item.Core.Count - (old?.Core.ItemId == id ? old.Core.Count : 0) : old == item ? 0 : 1;
                    if (delta <= 0) continue;
                    rewards.Add(new(item.Position, item.Core, delta, item.Uid)); total = checked(total + delta);
                }
                if (total != requested.Where(row => row.ItemId == id).Sum(row => row.Count))
                    throw new InvalidDataException("奖励增量与资源整批数量不同");
            }
            if (rewards.Count + currencies.Count + (gold == 0 ? 0 : 1) > byte.MaxValue)
                throw new NotSupportedException("任务奖励投影超过本版容量");
            CharacterGrowthPlan? growth = null;
            if (experience.Amount != 0)
            {
                if (expected.DungeonState is null) throw new InvalidDataException("任务经验缺少现有成长附属状态");
                var result = CharacterGrowthPlan.AddExperience(expected, experience.Amount, growthCatalog);
                if (!result.Success || result.Plan is null) throw new InvalidDataException("任务成长被拒绝: " + result.Failure);
                growth = result.Plan;
            }
            var rewardLearning = profession is { } target ? QuestProfessionReward.Prepare(expected,target,
                growth?.TargetLevel ?? expected.Level, growth?.Learning ?? expected.Learning, growthCatalog.Learning!, now) : null;
            if (tpAmount != 0) rewardLearning = QuestCharacterReward.AddTp(growth?.Learning ?? expected.Learning,tpAmount);
            return new(QuestCompletionOutcome.Accepted, new(expected, active, quest, selector, experience,
                new(expected.Inventory, after, "quest_complete"), growth, rewards.ToImmutable(), consumed.ToImmutable(),
                gold, goldBase, goldMultiple, penalty, now, quests, clear, profession, rewardLearning, multiplier, currencies.ToImmutable(),
                QuestCharacterReward.EquipmentSlots(expected,quest),tpAmount,closedChildren,reclaim,mailedGold), "通用领奖计划已冻结");
        }
        catch (Exception error) when (error is InvalidDataException or NotSupportedException or OverflowException or ArgumentOutOfRangeException)
        {
            return new(QuestCompletionOutcome.Rejected, null, error.Message) { Failure = QuestCompletionFailure.ResourceUnsupported };
        }
    }

    public static bool MatchesClearedActiveQuest(Character character, QuestClearReceipt clear)
    {
        if (character.Id == 0 || character.Revision < clear.Revision || clear.Revision <= 0 || clear.QuestId is 0 or >= 30000 || clear.QuestSlot > 29 ||
            clear.AcceptanceId == Guid.Empty || clear.EventId == Guid.Empty || clear.InstanceId == Guid.Empty ||
            clear.TaskAttempt <= 0 || clear.Epoch <= 0 || !Enum.IsDefined(clear.SourceType) || character.CompletedQuests.Contains(clear.QuestId) ||
            character.CompletedQuests.Distinct().Count() != character.CompletedQuests.Length)
            return false;
        var matches = character.ActiveMainQuests.Where(active => active.QuestId == clear.QuestId || active.Slot == clear.QuestSlot ||
            active.AcceptanceId == clear.AcceptanceId).ToArray();
        return matches.Length == 1 && matches[0].QuestId == clear.QuestId && matches[0].Slot == clear.QuestSlot &&
            matches[0].AcceptanceId == clear.AcceptanceId && matches[0].ConditionState == 0;
    }
}
