using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.ExpertJob;
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
internal sealed record QuestCurrencyReward(CurrencyKind Kind,int ItemId,int Count)
{
    // 现金没有Main槽；已提交的三个余额由原生53统一投影，不伪造奖励物品实例。
    public static bool HasBalanceNotice(CurrencyKind kind) => kind is CurrencyKind.Cera or CurrencyKind.TokenCera or CurrencyKind.HappyTokenCera;
    public static bool CanProject(CurrencyKind kind) => ItemSlotBoundService.CurrencySlot(kind) is not null || HasBalanceNotice(kind);
}

// 没有在线状态或提交能力；原角色、稳定clear和全部奖励候选由同一Session/Repository事务接管。
internal sealed class QuestCompletionPlan
{
    QuestCompletionPlan(Character expected, ActiveMainQuest active, StoryQuest quest, ushort selector,
        QuestExperienceReward experience, InventoryPlan inventory, CharacterGrowthPlan? growth,
        ImmutableArray<QuestPlannedItemReward> rewards, ImmutableArray<QuestConsumedItem> consumed,
        uint gold, int goldBase, int goldMultiple, int penalty, DateTimeOffset now, QuestCatalog quests, QuestClearReceipt? clear,
        byte? profession, CharacterLearningState? rewardLearning, ushort multiplier, ImmutableArray<QuestCurrencyReward> currencies,
        byte equipmentSlots, ushort tpAmount, ImmutableArray<ActiveMainQuest> closedChildren, InventoryPlan? reclaim, uint mailedGold,
        int? completedParentId = null, QuestStagePlan? stagePlan = null, byte? expertJob = null,
        ProficiencyAdvance? proficiency = null, ActiveMainQuest? completedChild = null, StoryQuest? terminalSeeking = null, QuestMailPlan? resourceMail = null)
    {
        ResourceMail = resourceMail;
        Expected = expected; QuestId = active.QuestId; AcceptanceId = active.AcceptanceId; QuestSlot = active.Slot;
        CompletedParentId = completedParentId;
        StagePlan = stagePlan;
        AuthoritativeClear = clear;
        ConditionType = quest.Condition switch { "[clear map]" => 3, "[condition under clear]" => 1, "[clear a dungeon]" => 45, "[seeking]" => 0, "[seek n meet npc]" => 25, "[hunt enemy]" => 6, "[hunt monster]" => 2,
            "[custom quest]" when QuestRentalProgress.Supports(quest) || QuestExpertJobProgress.Craft(quest) => 19,
            "[epic production]" when QuestEpicProductionProgress.Supports(quest) => 67,
            "[self disjoint item]" when QuestExpertJobProgress.SelfDisjoint(quest) => 17,
            "[quest clear]" => 44, "[clear multi quest]" => 70, "[question]" => 42, "[reach the range]" => 64, "[look cinematic]" => 65,
            "[custom multi condition]" when QuestDazzlementStorage.Matches(quest) || DfoServer.Game.Pvp.PvpTrainingProgress.Matches(quest) => 60,
            _ => QuestStateProgress.Type(quest) ?? (byte)4 };
        // 历史归档不是新的通关领奖，沿用已有无清图凭据的零奖励回执类型，不改库结构。
        if (completedParentId is not null && ConditionType is 1 or 3 or 45) ConditionType = 4;
        RewardSelector = selector;
        ExperienceReward = experience; Inventory = inventory; GrowthPlan = growth; Proficiency = proficiency;
        Rewards = rewards; Consumed = consumed;
        Multiplier = multiplier;
        Currencies = currencies;
        GoldAmount = gold; MailedGoldAmount = mailedGold; FrozenAt = now;
        DailyPeriod = QuestCycle.Daily(quest) ? QuestCycle.Day(now) : 0;
        AccountQuest = QuestAccountPolicy.SharedCompletion(quest);
        AccountDailyClaimsAfter = AccountQuest && DailyPeriod != 0 ? expected.AccountQuestDailyClaims.SetItem(quest.Id,DailyPeriod) : expected.AccountQuestDailyClaims;
        Answer = quest.Condition == "[question]" ? checked((byte)active.ConditionState) : null;
        AnswersAfter = Answer is { } answer ? expected.QuestAnswers.SetItem(quest.Id,answer) : expected.QuestAnswers;
        AccountAnswersAfter = AccountQuest && Answer is { } accountAnswer ? expected.AccountQuestAnswers.SetItem(quest.Id,accountAnswer) : expected.AccountQuestAnswers;
        ProfessionGrowType = profession; RewardLearning = rewardLearning; TargetEquipmentSlotFlags = equipmentSlots; TpAmount = tpAmount;
        GrantedExpertJob = expertJob;
        // mode3 解锁提示只对已逆向确认的 [hell challenge] 成立；inferno/cosmo/6081 为假设性放行，仍走普通 mode0（Refs #256）。
        HellChallenge = quest.RewardType == "[hell challenge]" && QuestCharacterReward.TryHellChallenge(quest);
        ClosedChildren = closedChildren;
        CompletedChild = completedChild;
        CascadeMarkerQuestId = QuestRelationProgress.CascadeMarker(quest,quests);
        SyncedQuestIds = (CascadeMarkerQuestId is { } synced && QuestRelationProgress.IsAccountSyncMarker(quests.Get(synced))
            ? new[] { synced } : []).Except(expected.AccountCompletedQuests).ToImmutableArray();
        AccountCompletedAfter = (AccountQuest ? expected.AccountCompletedQuests.Append(quest.Id) : expected.AccountCompletedQuests)
            .Concat(SyncedQuestIds).Distinct().Order().ToImmutableArray();
        NewCompletedQuestIds = new[] { (int)quest.Id }.Concat(CascadeMarkerQuestId is { } marker ? new[] { marker } : [])
            .Concat(completedChild is { } terminal ? new[] { (int)terminal.QuestId } : [])
            .Except(QuestEligibility.Completed(expected)).Order().ToImmutableArray();
        CompletedAfter = expected.CompletedQuests.Concat(NewCompletedQuestIds).Distinct().Order().ToImmutableArray();
        var activeAfter = QuestRelationProgress.Refresh(QuestInventoryProgress.Refresh(
            expected.ActiveMainQuests.Where(row => row.AcceptanceId != active.AcceptanceId &&
                row.AcceptanceId != completedChild?.AcceptanceId &&
                !ClosedChildren.Any(child => child.AcceptanceId == row.AcceptanceId)).ToArray(), inventory.After, quests, now.ToUnixTimeSeconds()),
            CompletedAfter.Concat(AccountCompletedAfter).ToArray(), quests);
        ActiveAfter = QuestStateProgress.Refresh(activeAfter,expected with { Inventory = inventory.After, Level = TargetLevel,
            GrowType = TargetGrowType, CompletedQuests = CompletedAfter.ToArray(), AccountCompletedQuests = AccountCompletedAfter.ToArray() },quests);
        Snapshot = JsonSerializer.Serialize(new { QuestId, AcceptanceId, QuestSlot, ConditionType, RewardSelector, Multiplier,
            ExperienceReward, GoldAmount, goldBase, goldMultiple, penalty, Consumed, Rewards, Currencies, FrozenAt, DailyPeriod, AccountQuest,
            AuthoritativeClear, Answer, Learning = TargetLearning, Level = TargetLevel, GrowType = TargetGrowType, EquipmentSlotFlags = TargetEquipmentSlotFlags });
        if (ResourceMail is not null)
        {
            var snapshot = System.Text.Json.Nodes.JsonNode.Parse(Snapshot)!.AsObject();
            snapshot["ResourceMail"] = JsonSerializer.SerializeToNode(ResourceMail);
            Snapshot = snapshot.ToJsonString();
        }
        if (completedChild is not null)
        {
            var snapshot = System.Text.Json.Nodes.JsonNode.Parse(Snapshot)!.AsObject();
            snapshot["TerminalSeeking"] = JsonSerializer.SerializeToNode(new {
                Acceptance = completedChild, terminalSeeking!.Path, terminalSeeking.IntData, terminalSeeking.Rewards });
            Snapshot = snapshot.ToJsonString();
        }
        if (StagePlan is not null)
        {
            var snapshot = System.Text.Json.Nodes.JsonNode.Parse(Snapshot)!.AsObject();
            snapshot["Stage"] = JsonSerializer.SerializeToNode(new { StagePlan, OrdinaryExperience, ContractExperience,
                StageExperience, TotalRequestedExperience, ActualExperience });
            Snapshot = snapshot.ToJsonString();
        }
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
        if (GrantedExpertJob is { } grantedExpertJob)
        {
            var snapshot = System.Text.Json.Nodes.JsonNode.Parse(Snapshot)!.AsObject();
            snapshot[nameof(GrantedExpertJob)] = grantedExpertJob;
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
                RequiredChildren = quest.IntData, quest.Definition!.MinimumClearCount, ClosedChildren,
                ReclaimedItems = reclaim!.ChangedSlots.Select(slot => new {
                    Slot = slot, Before = reclaim.Before.Find(slot), After = reclaim.After.Find(slot)
                }).ToArray()
            });
            Snapshot = snapshot.ToJsonString();
        }
    }

    public QuestStagePlan? StagePlan { get; }
    public uint OrdinaryExperience => ExperienceReward.Bonuses?.Ordinary ?? ExperienceReward.Amount - ExperienceReward.GrowthContractExperience;
    public uint ContractExperience => ExperienceReward.GrowthContractExperience;
    public uint StageExperience => StagePlan?.BonusExperience ?? 0;
    public uint TotalRequestedExperience => checked(ExperienceReward.Amount + StageExperience);
    public uint ActualExperience => checked((uint)(TargetLearning.Experience - Expected.Learning.Experience));
    Character ProjectedCharacter => Expected with { Revision = checked(Expected.Revision + 1),
        Inventory = Inventory.After, Level = TargetLevel, GrowType = TargetGrowType, Learning = TargetLearning,
        ExpertJobType = TargetExpertJob,
        EquipmentSlotFlags = TargetEquipmentSlotFlags, CompletedQuests = CompletedAfter.ToArray(),
        AccountCompletedQuests = AccountCompletedAfter.ToArray(), ActiveMainQuests = ActiveAfter.ToArray(),
        QuestAnswers = AnswersAfter, AccountQuestAnswers = AccountAnswersAfter, AccountQuestDailyClaims = AccountDailyClaimsAfter,
        Skills = !UpdatesLearning ? Expected.Skills : TargetLearning.Trees.Single(tree => tree.Context == TargetLearning.ActiveContext).Skills
            .Select(skill => new CharacterSkill(skill.Id, skill.Level)).OrderBy(skill => skill.Id).ToArray() };

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
    internal DfoServer.Game.Pvp.PvpRatingSnapshot? PvpRating { get; set; }
    public QuestExperienceReward ExperienceReward { get; }
    public InventoryPlan Inventory { get; }
    public CharacterGrowthPlan? GrowthPlan { get; }
    // 任务经验与副本经验走同一条熟练度判定；不写入Snapshot，历史回执字节与重放条件保持不变。
    public ProficiencyAdvance? Proficiency { get; }
    public CharacterGrowthPlan Growth => GrowthPlan ?? throw new InvalidOperationException("零经验任务不需要成长规划");
    public byte? ProfessionGrowType { get; }
    public byte? GrantedExpertJob { get; }
    public byte TargetExpertJob => GrantedExpertJob ?? Expected.ExpertJobType;
    private CharacterLearningState? RewardLearning { get; }
    public bool UpdatesLearning => GrowthPlan is not null || RewardLearning is not null;
    public byte TargetEquipmentSlotFlags { get; }
    public ushort TpAmount { get; }
    public bool HellChallenge { get; }
    public ImmutableArray<ActiveMainQuest> ClosedChildren { get; }
    public ActiveMainQuest? CompletedChild { get; }
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
    public QuestMailPlan? ResourceMail { get; }
    public uint DirectGoldAmount => GoldAmount - MailedGoldAmount;

    public static QuestCompletionPreparation Prepare(Character expected, StoryQuest quest, ushort selector,
        QuestCatalog quests, QuestRewardParameterCatalog parameters, ItemCatalog items, GoldLimitCatalog goldLimits,
        EpicPieceCatalog pieces, CharacterGrowthCatalog growthCatalog, DateTimeOffset now, QuestClearReceipt? clear = null, ushort multiplier = 1,
        GrowthContractBenefits? growthContract = null, QuestStageGraph? stageGraph = null, QuestStageState? stageState = null, ExperienceBonusService? experienceBonuses = null, bool premiumPcRoom = false,
        ExpertJobScriptCatalog? expertJobs = null, int? pvpGrade = null)
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
            // 地区每日上限只约束分配；同日原接取不因后续升级失去领奖资格，其余资格与目标仍逐项复验。
            if (!DfoServer.Game.Pvp.PvpTrainingExposure.Allows(expected,quest) || active is null || active.AcceptanceId == Guid.Empty || active.Slot >= 30 ||
                QuestEligibility.Completed(expected).Contains(quest.Id) || expected.CompletedQuests.Distinct().Count() != expected.CompletedQuests.Length ||
                QuestEligibility.InspectResource(quest).Entry is not (QuestResourceEntry.Ordinary or QuestResourceEntry.Creature) && !QuestRelationProgress.DialogueChild(quest, quests) &&
                    !QuestRelationProgress.ThresholdParent(quest,quests) && !QuestHiddenEntry.Direct(quest,quests) && !QuestAutoDailyPlan.Supports(quest) ||
                completedParent is null && !QuestEligibility.Evaluate(expected with { ActiveMainQuests = expected.ActiveMainQuests.Where(row => row != active).ToArray() }, quest, quests,
                    allowAboveMaximum: QuestAutoDailyPlan.Hunt(quest)).Eligible)
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
            StoryQuest? terminalSeeking = null;
            ActiveMainQuest? completedChild = null;
            if (QuestRelationProgress.TerminalSeeking(quest,quests) is { } terminal &&
                !expected.CompletedQuests.Contains(terminal.Id) &&
                quest.IntData.Where(id => id != terminal.Id).All(expected.CompletedQuests.Contains))
            {
                completedChild = expected.ActiveMainQuests.SingleOrDefault(row => row.QuestId == terminal.Id);
                if (completedChild is not { ConditionState: 0 } || completedChild.AcceptanceId == Guid.Empty ||
                    active.ConditionState > 1 ||
                    !QuestEligibility.Evaluate(expected with { ActiveMainQuests = expected.ActiveMainQuests.Where(row => row != completedChild).ToArray() },terminal,quests).Eligible)
                    throw new InvalidDataException("顺序末子任务需要原接取、已达成条件及全部前序完成记录");
                terminalSeeking = terminal;
            }
            byte? profession = quest.RewardType is "[grow type]" or "[awakening type]"
                ? QuestProfessionReward.Target(expected, quest, growthCatalog.Learning ?? throw new NotSupportedException("职业变化技能资源未装配")) : null;
            _ = QuestCharacterReward.TryTp(quest,out ushort tpAmount);
            bool characterReward = QuestCharacterReward.TryScalar(quest,out _) || tpAmount != 0 || QuestCharacterReward.TryHellChallenge(quest) ||
                QuestCharacterReward.TryBeastQualification(quest);
            bool creatureReward = QuestCreatureReward.Evolution(quest);
            byte? expertJob = QuestExpertJobReward.Type(quest);
            bool dungeonClear = QuestDungeonClearProgress.Supports(quest);
            bool rental = QuestRentalProgress.Supports(quest);
            bool expertTutorial = QuestExpertJobProgress.Tutorial(quest);
            // 同一拒绝不能掩盖资源差异；保留原门禁，仅报告实际未支持的声明。
            if (quest.SubType != -1 && !parent && !QuestClientSceneProgress.TownRange(quest) && !dungeonClear && !rental && !expertTutorial && QuestStateProgress.Type(quest) is null && !QuestLifecyclePlan.HarlemSeeking(quest) && !QuestLifecyclePlan.BeastSeeking(quest) && !QuestCycle.NormalDungeonMission(quest) && !QuestDazzlementStorage.Matches(quest))
                throw new NotSupportedException($"任务交付子类型未支持: condition={quest.Condition}, sub_type={quest.SubType}");
            if (quest.RewardType != "[item]" && profession is null && expertJob is null && !characterReward && !creatureReward && !QuestRelationProgress.ZeroRewardChild(quest) && !QuestCoverage.GoldOnlyReward(quest))
                throw new NotSupportedException($"任务交付奖励声明未支持: reward_type={quest.RewardType ?? "<absent>"}, gold_multiple={quest.DeclaredGoldMultiple}, reward_tokens={quest.Rewards.Count}");
            if (!QuestCoverage.HasOrdinaryAttributes(quest))
                throw new NotSupportedException("任务交付属性未支持: attribute=" + string.Join(",",quest.Attributes));
            if (quest.Definition!.Has("send postal"))
                throw new NotSupportedException("任务交付附属声明未支持: send postal");
            if (QuestCycle.Daily(quest) && expected.QuestCycleDay != QuestCycle.Day(now))
                throw new InvalidDataException("每日任务交付需要当前游戏日快照");
            if (quest.Condition == "[clear map]")
            {
                if (!QuestClearEligibility.IsSingleMap(quest) || clear is null || !MatchesClearedActiveQuest(expected, clear) ||
                    clear.QuestId != quest.Id || !QuestClearEligibility.MatchesClearMap(quest, clear) ||
                    quest.DungeonIndex is >= 0 && clear.DungeonId != quest.DungeonIndex)
                    throw new InvalidDataException("清图交付必须使用当前接取及目标地图的权威凭据");
            }
            else if (dungeonClear)
            {
                if (clear is null || !MatchesClearedActiveQuest(expected,clear) || clear.QuestId != quest.Id ||
                    !QuestDungeonClearProgress.ContainsDungeon(quest,clear.DungeonId) || clear.MapId <= 0)
                    throw new InvalidDataException("条件通关交付需要当前接取次数归零及最后达成的权威凭据");
            }
            else if (clear is not null) throw new InvalidDataException("此任务条件不能使用清图凭据");
            else if (QuestStateProgress.Type(quest) is not null)
            {
                if (active.ConditionState != 0 || QuestStateProgress.Scalar(expected,quest,pvpGrade) != 0)
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
                if (!parent || completedChild is null && (active.ConditionState != 0 || !quest.IntData.All(expected.CompletedQuests.Contains)))
                    throw new InvalidDataException("父任务必须具有全部真实子任务完成记录");
            }
            else if (quest.Condition == "[clear multi quest]")
            {
                if (!threshold || active.ConditionState != 0 ||
                    QuestRelationProgress.ThresholdScalar(quest,QuestEligibility.Completed(expected)) != 0)
                    throw new InvalidDataException("阈值父任务必须达到真实子任务完成数量");
            }
            else if (DfoServer.Game.Pvp.PvpTrainingProgress.Matches(quest))
            {
                if (active.ConditionState != 0) throw new InvalidDataException("本次接取的训练通道尚未全部完成");
            }
            else if (QuestDazzlementStorage.Matches(quest))
            {
                if (active.ConditionState != 0) throw new InvalidDataException("贮藏箱尚未在任务NPC所在城镇开启");
            }
            else if (QuestClientSceneProgress.TownRange(quest) || QuestClientSceneProgress.Cinematic(quest,out _,out _) ||
                QuestClientSceneProgress.DungeonCinematic(quest))
            {
                if (active.ConditionState != 0) throw new InvalidDataException("范围或演出的原生终末事件尚未达成");
            }
            else if (QuestEpicProductionProgress.Supports(quest))
            {
                if (active.ConditionState!=0) throw new InvalidDataException("史诗加工尚未由本次接取后的成功事务达成");
            }
            else if (rental)
            {
                if (active.ConditionState != 0) throw new InvalidDataException("租赁任务需要本次接取的已提交租赁事实");
            }
            else if (expertTutorial)
            {
                if (active.ConditionState != 0) throw new InvalidDataException("副职业教程需要本次接取的已提交商店事实");
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
            if(DfoServer.Game.Pvp.PvpTrainingProgress.Manual(quest))
            {
                var manual=paid.Items.Where(item=>item.Position.List==InventoryListType.Main &&
                    item.Core.ItemId==DfoServer.Game.Pvp.PvpTrainingProgress.ManualItemId && item.Core.Count>0 &&
                    item.Core.EquipmentLockId==0 && !item.Core.IsExpired(now.ToUnixTimeSeconds()))
                    .OrderBy(item=>item.Position.Index).FirstOrDefault();
                if(manual is null) throw new InvalidDataException("训练秘籍尚未实际取得或不能消耗");
                var deletion=InventoryDeleteService.Plan(paid,manual.Position,manual.Core.Value,1);
                if(!deletion.Accepted) throw new InvalidDataException("训练秘籍不能消耗");
                paid=deletion.Plan!.After;
                consumed.Add(new(manual.Position,manual.Uid,manual.Core.ItemId,1));
            }
            var materialQuest = terminalSeeking ?? quest;
            if (materialQuest.Condition is "[seeking]" or "[seek n meet npc]")
            {
                var wanted = new Dictionary<int, long>();
                for (int at = 0; at < QuestLifecyclePlan.MaterialLength(materialQuest); at += 2)
                    wanted[materialQuest.IntData[at]] = checked(wanted.GetValueOrDefault(materialQuest.IntData[at]) + (long)materialQuest.IntData[at + 1]*multiplier);
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
            var definition = QuestFixedRewardDefinition.Parse(profession is not null || characterReward || creatureReward || expertJob is not null ? quest with { Rewards = [] } : quest, allowGold: true);
            var requested = definition.ForJob(expected.Job,expected.GrowType).ToBuilder();
            if (terminalSeeking is not null && !QuestRelationProgress.ZeroRewardChild(terminalSeeking))
                requested.AddRange(QuestFixedRewardDefinition.Parse(terminalSeeking).ForJob(expected.Job,expected.GrowType));
            var selected = QuestFixedRewardDefinition.Parse(quest with { Rewards = quest.Definition!.SelectRewards }).ForJob(expected.Job,expected.GrowType);
            if (selected.Length == 0 ? selector != ushort.MaxValue : selector >= selected.Length)
                throw new InvalidDataException("选择奖励索引不属于当前职业候选");
            if (selected.Length != 0) requested.Add(selected[selector]);
            var experience = QuestExperiencePolicy.Prepare(expected, quest, parameters, out int penalty);
            uint contractBonus = GrowthContractPolicy.ExperienceBonus(expected, experience.Amount, growthContract, now.ToUnixTimeSeconds());
            var bonuses = experienceBonuses?.Plan(new(expected, ExperienceEvent.Quest, checked((uint)(clear?.DungeonId ?? 0)),
                now.ToUnixTimeSeconds(), premiumPcRoom) { UnixMilliseconds = now.ToUnixTimeMilliseconds() }, experience.Amount, 0, growthContract);
            experience = experience with { Amount = bonuses?.Total ?? checked(experience.Amount + contractBonus),
                GrowthContractExperience = bonuses?.Amount(ExperienceBonusSource.GrowthContract) ?? contractBonus, Bonuses = bonuses };
            ulong scaledExperience = checked((ulong)experience.Amount*multiplier);
            if (scaledExperience > int.MaxValue) throw new InvalidDataException("批量任务经验超出本版范围");
            experience = experience with { Amount = (uint)scaledExperience,
                GrowthContractExperience = checked(experience.GrowthContractExperience * multiplier), Bonuses = experience.Bonuses?.Scale(multiplier) };
            for (int at = 0; at < requested.Count; at++) requested[at] = requested[at] with { Count = checked(requested[at].Count*multiplier) };
            int goldBase = 0, goldMultiple = quest.DeclaredGoldMultiple ?? 100;
            uint gold = 0;
            // 禁经验子任务的0/0是零奖励占位，不能借普通任务的动态金币marker发奖。
            if (!QuestRelationProgress.ZeroRewardChild(quest) && (definition.HasGoldMarker || quest.DeclaredGoldMultiple > 0))
            {
                if (expertTutorial && definition.HasGoldMarker && definition.FixedGoldAmount == 0 && quest.DeclaredGoldMultiple is not > 0)
                    gold = 0;
                else if (definition.FixedGoldAmount > 0 && quest.DeclaredGoldMultiple is not > 0)
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
            if (grant.Plan.ChangedCurrencies.Any(kind => !QuestCurrencyReward.CanProject(kind)) || grant.Plan.ChangedEpicPieces.Length != 0 ||
                grant.Plan.ChangedSlots.Any(slot => slot.List != InventoryListType.Main &&
                    !(slot.List == InventoryListType.Pet && after.Find(slot) is { Core.Kind: ItemKind.Creature, Creature: not null })))
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
                    if (!QuestCurrencyReward.CanProject(currency) || CurrencyWallet.Get(after,currency)-CurrencyWallet.Get(paid,currency) != amount)
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
            if (rewards.Count + currencies.Count(row => !QuestCurrencyReward.HasBalanceNotice(row.Kind)) + (gold == 0 ? 0 : 1) > byte.MaxValue)
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
            if (expertJob is { } grantedJob)
                rewardLearning = ExpertJobSkills.Grant(expected.Job, growth?.Learning ?? expected.Learning,
                    (expertJobs ?? throw new NotSupportedException("副职业赠技资源未装配")).Skills(grantedJob),
                    growthCatalog.Learning ?? throw new NotSupportedException("副职业技能资源未装配"));
            InventoryCharacterPrecondition? creaturePrecondition = null;
            if (creatureReward)
            {
                var evolution = QuestCreatureReward.Evolve(expected with { Inventory = after }, quest, quests, now.ToUnixTimeSeconds());
                after = evolution.After;
                int deadline = expected.Inventory.Find(DfoServer.Game.Creatures.CreatureRules.EquippedSlot)!.ExpireTime;
                creaturePrecondition = new(expected, deadline == 0 ? null : deadline);
            }
            var resourceMail = QuestMailPlan.HasMail(quest) && QuestMailPlan.Read(quest).Time == 1
                ? QuestMailPlan.Prepare(quest, items, pieces, now) : null;
            if (resourceMail is not null && multiplier != 1)
                throw new NotSupportedException("任务声明邮件不与批量领奖倍数合并");
            QuestCompletionPlan Create(CharacterGrowthPlan? finalGrowth, CharacterLearningState? finalLearning, QuestStagePlan? stage = null)
            {
                ProficiencyAdvance? proficiency = null;
                if (finalGrowth is not null && expected.DungeonState is { } dungeon)
                    proficiency = ProficiencyProgression.Advance(new(dungeon.ProficiencyLevel, dungeon.ProficiencyExperience),
                        ProficiencyProgression.Gain(finalGrowth.PreviousLevel, finalGrowth.Before.Experience,
                            checked((uint)finalGrowth.RequestedExperience), growthCatalog), growthCatalog);
                return new(expected, active, quest, selector, experience,
                    new(expected.Inventory, after, "quest_complete", creaturePrecondition), finalGrowth, rewards.ToImmutable(), consumed.ToImmutable(),
                    gold, goldBase, goldMultiple, penalty, now, quests, clear, profession, finalLearning, multiplier, currencies.ToImmutable(),
                    QuestCharacterReward.EquipmentSlots(expected,quest),tpAmount,closedChildren,reclaim,mailedGold, stagePlan: stage,
                    expertJob: expertJob, proficiency: proficiency, completedChild: completedChild, terminalSeeking: terminalSeeking, resourceMail: resourceMail);
            }
            var normal = Create(growth, rewardLearning);
            if ((stageGraph is null) != (stageState is null)) throw new InvalidDataException("阶段图与持久状态必须同时提供");
            if (stageGraph is null) return new(QuestCompletionOutcome.Accepted, normal, "通用领奖计划已冻结");
            var stage = QuestStagePolicy.Prepare(expected, normal.ProjectedCharacter, quest, active.AcceptanceId,
                quests, stageGraph, growthCatalog.CumulativeExperience, stageState!);
            // 已有足够经验同样消费一次目标资格；多个来源按固定来源顺序只落一笔0额claim。
            if (stage.Claim is null)
            {
                var consumedTargets = stage.Expected.Witnesses.Where(row => row.Status is QuestStageStatus.Granted or QuestStageStatus.Satisfied)
                    .Select(row => row.TargetLevel).ToHashSet();
                var satisfied = stage.WitnessesAfter.Where(row => row.Status == QuestStageStatus.Satisfied && !consumedTargets.Contains(row.TargetLevel))
                    .OrderBy(row => row.TargetLevel).ThenBy(row => row.SourceCompletionRevision).ThenBy(row => row.SourceAcceptanceId).FirstOrDefault();
                if (satisfied is not null) stage = stage with { Claim = new(satisfied.SourceAcceptanceId, active.AcceptanceId,
                    satisfied.TargetLevel, normal.TargetLearning.Experience, normal.TargetLearning.Experience, 0) };
            }
            if (stage.BonusExperience != 0)
            {
                if (expected.DungeonState is null) throw new InvalidDataException("任务经验缺少现有成长附属状态");
                if ((ulong)experience.Amount + stage.BonusExperience > int.MaxValue)
                    throw new InvalidDataException("普通及阶段总经验超过本版显示范围");
                var result = CharacterGrowthPlan.AddExperience(expected, checked(experience.Amount + stage.BonusExperience), growthCatalog);
                if (!result.Success || result.Plan is null) throw new InvalidDataException("阶段成长被拒绝: " + result.Failure);
                growth = result.Plan;
                rewardLearning = profession is { } finalProfession ? QuestProfessionReward.Prepare(expected, finalProfession,
                    growth.TargetLevel, growth.Learning, growthCatalog.Learning!, now) : null;
                if (tpAmount != 0) rewardLearning = QuestCharacterReward.AddTp(growth.Learning, tpAmount);
                if (expertJob is { } stageJob)
                    rewardLearning = ExpertJobSkills.Grant(expected.Job, growth.Learning, expertJobs!.Skills(stageJob), growthCatalog.Learning!);
            }
            return new(QuestCompletionOutcome.Accepted, Create(growth, rewardLearning, stage), "通用及阶段领奖计划已冻结");
        }
        catch (Exception error) when (error is InvalidDataException or NotSupportedException or OverflowException or ArgumentOutOfRangeException)
        {
            // 把真实原因写进拒绝结果（服务端日志可见），对客户端的返回不变。
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
