using DfoServer.Game.Quests;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Quests;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    // 只闭合已达成、禁经验且无奖励的真实对话子任务；清图仍复验本次接取的持久通关凭据。
    public QuestCompletionReceipt? FinishReadyQuestChildren(QuestCatalog quests, QuestRewardParameterCatalog parameters,
        ItemCatalog items, GoldLimitCatalog goldLimits, EpicPieceCatalog pieces, CharacterGrowthCatalog growth)
    {
        var children = owner.Execute(() => Selected?.ActiveMainQuests.Where(row => row.ConditionState == 0 &&
            quests.TryGet(row.QuestId,out var quest) && QuestRelationProgress.ZeroRewardChild(quest))
            .Select(row => row.QuestId).ToArray() ?? []);
        QuestCompletionReceipt? last = null;
        foreach (ushort id in children)
        {
            var result = FinishQuest(id,ushort.MaxValue,quests,parameters,items,goldLimits,pieces,growth);
            if (result.Receipt is { } receipt) last = receipt;
            if (last is { Commit.ProjectionCurrent: false }) break;
        }
        return last;
    }

    private sealed record PendingQuestCompletion(QuestCompletionPlan Plan, long SelectionEpoch,
        SessionPhase Phase, DungeonRunIdentity? DungeonIdentity);
    private PendingQuestCompletion? pendingQuestCompletion;

    public QuestFinishResult FinishSingleClearQuest(ushort questId, QuestCatalog quests, QuestRewardParameterCatalog parameters,
        ItemCatalog items, GoldLimitCatalog goldLimits, EpicPieceCatalog pieces, CharacterGrowthCatalog growth)
        => FinishQuest(questId, ushort.MaxValue, quests, parameters, items, goldLimits, pieces, growth);

    public QuestFinishResult FinishQuest(ushort questId, ushort selector, QuestCatalog quests, QuestRewardParameterCatalog parameters,
        ItemCatalog items, GoldLimitCatalog goldLimits, EpicPieceCatalog pieces, CharacterGrowthCatalog growth, ushort multiplier = 1)
    {
        var captured = owner.Execute(() =>
        {
            if (!Ready || Selected is null || Phase is not (SessionPhase.Town or SessionPhase.Dungeon or SessionPhase.Settlement) ||
                formalDungeon?.Ground.HasPendingPickup == true || formalDungeon?.Run.HasPendingDeath == true || formalDungeon?.Run.HasPendingSettlement == true || HasPendingQuestScene)
                return (Character: (DfoServer.Game.Characters.Character?)null, SelectionEpoch, Phase,
                    Identity: (DungeonRunIdentity?)null, Clear: (QuestClearReceipt?)null, Pending: (PendingQuestCompletion?)null);
            var identity = Phase is SessionPhase.Dungeon or SessionPhase.Settlement ? formalDungeon?.Run.Snapshot.Identity : null;
            var pending = pendingQuestCompletion;
            bool samePending = pending is not null && ReferenceEquals(pending.Plan.Expected, Selected) &&
                pending.SelectionEpoch == SelectionEpoch && pending.Phase == Phase && pending.DungeonIdentity == identity &&
                pending.Plan.QuestId == questId && pending.Plan.RewardSelector == selector && pending.Plan.Multiplier == multiplier;
            // 提交已发生但返回值丢失时，Selected仍是原旧快照；先以原计划查receipt，不能先用旧revision重读活动槽。
            var clear = !Selected.CompletedQuests.Contains(questId)
                ? samePending ? pending!.Plan.AuthoritativeClear : ReadClearedQuest(questId)?.Clear : null;
            return (Character: Selected, SelectionEpoch, Phase, Identity: identity, Clear: clear, Pending: pendingQuestCompletion);
        });
        if (captured.Character is not { } expected)
            return new(QuestFinishOutcome.Unsupported);
        var rejection = new QuestRefusalReceipt(expected, owner.Generation, captured.SelectionEpoch, captured.Identity) { Phase = captured.Phase };
        if (!quests.TryGet(questId, out var quest))
            return new(QuestFinishOutcome.Unsupported, RejectionReceipt: rejection);
        if (QuestEligibility.Completed(expected).Contains(questId))
            return new(QuestFinishOutcome.AlreadyCompleted, RejectionReceipt: rejection);
        bool dungeonClear = QuestDungeonClearProgress.TryTarget(quest,out _);
        if ((quest.Condition == "[clear map]" || dungeonClear) && captured.Clear is null)
            return new(QuestFinishOutcome.Rejected, RejectionReceipt: rejection);
        // 地点、NPC和实例目标在进度达成时验证；交付使用本次接取的持久事实，不因离开原场景而失效。
        // 各目标的实际材料、回答、子任务、角色状态及清图凭据仍由统一领奖规划复验。
        if (captured.Pending is { } incomplete && (incomplete.Plan.QuestId != questId || incomplete.Plan.RewardSelector != selector || incomplete.Plan.Multiplier != multiplier))
            return new(QuestFinishOutcome.Rejected, RejectionReceipt: rejection);
        QuestCompletionPreparation preparation;
        if (captured.Pending is { } pending && pending.SelectionEpoch == captured.SelectionEpoch && pending.Phase == captured.Phase &&
            pending.DungeonIdentity == captured.Identity && ReferenceEquals(pending.Plan.Expected, expected) &&
            pending.Plan.AuthoritativeClear == captured.Clear && pending.Plan.RewardSelector == selector && pending.Plan.Multiplier == multiplier)
            preparation = new(QuestCompletionOutcome.Accepted, pending.Plan, "原领奖计划精确重试");
        else
            preparation = QuestCompletionPlan.Prepare(expected, quest, selector, quests, parameters, items, goldLimits, pieces,
                growth, DateTimeOffset.UtcNow, captured.Clear, multiplier);
        return owner.Execute(() =>
        {
            if (!CanPublishQuestRefusal(rejection))
                throw new InvalidOperationException("领奖资源规划的原选角、角色快照或实例已失效");
            if (preparation.Outcome != QuestCompletionOutcome.Accepted || preparation.Plan is null)
                return new QuestFinishResult(preparation.Outcome == QuestCompletionOutcome.InsufficientSpace &&
                    preparation.Failure == QuestCompletionFailure.InventoryCapacity
                    ? QuestFinishOutcome.InsufficientSpace : QuestFinishOutcome.Rejected, RejectionReceipt: rejection);
            var plan = preparation.Plan;
            // 不明提交结果保留这份冻结计划；精确查询同接取receipt，不生成新的领奖身份。
            pendingQuestCompletion = new(plan, captured.SelectionEpoch, captured.Phase, captured.Identity);
            var commit = repository.CommitQuestCompletion(owner, plan);
            Selected = commit.Character;
            completed = QuestEligibility.Completed(Selected);
            pendingQuestCompletion = null;
            return new QuestFinishResult(QuestFinishOutcome.Committed,
                new(commit, owner.Generation, captured.SelectionEpoch, captured.Identity) { Phase = captured.Phase });
        });
    }

    public bool CanPublishQuestCompletion(QuestCompletionReceipt receipt) => owner.Execute(() =>
        Ready && owner.Generation == receipt.Generation && SelectionEpoch == receipt.SelectionEpoch &&
        receipt.Commit.ProjectionCurrent && ReferenceEquals(Selected, receipt.Commit.Character) && Phase == receipt.Phase &&
        (receipt.DungeonIdentity is null ? Phase == SessionPhase.Town :
            formalDungeon?.Run.Snapshot.Identity == receipt.DungeonIdentity));

    public bool CanPublishQuestRefusal(QuestRefusalReceipt receipt) => owner.Execute(() =>
        Ready && owner.Generation == receipt.Generation && SelectionEpoch == receipt.SelectionEpoch &&
        ReferenceEquals(Selected, receipt.Character) && Phase == receipt.Phase &&
        (receipt.DungeonIdentity is null ? Phase == SessionPhase.Town :
            formalDungeon?.Run.Snapshot.Identity == receipt.DungeonIdentity));

    public QuestConditionReceipt? ReadClearedQuest(ushort questId) => owner.Execute(() =>
    {
        if (!Ready || Selected is null || Phase is not (SessionPhase.Town or SessionPhase.Settlement))
            return null;
        var clear = repository.LoadQuestClear(owner, Selected, questId);
        if (clear is null)
            return null;
        var identity = Phase == SessionPhase.Settlement ? formalDungeon?.Run.Snapshot.Identity : null;
        if (Phase == SessionPhase.Settlement && (identity is null || identity.InstanceId != clear.InstanceId ||
            identity.TaskAttempt != clear.TaskAttempt || identity.Epoch != clear.Epoch))
            return null;
        return new QuestConditionReceipt(Selected, clear, owner.Generation, SelectionEpoch, identity);
    });

    public bool CanPublishQuestCondition(QuestConditionReceipt receipt) => owner.Execute(() =>
        Ready && owner.Generation == receipt.Generation && SelectionEpoch == receipt.SelectionEpoch &&
        ReferenceEquals(Selected, receipt.Character) &&
        (receipt.DungeonIdentity is null ? Phase == SessionPhase.Town :
            Phase == SessionPhase.Settlement && formalDungeon?.Run.Snapshot.Identity == receipt.DungeonIdentity));
}
