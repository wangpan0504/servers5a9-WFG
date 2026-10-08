using DfoServer.Game.Quests;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Quests;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    private sealed record PendingQuestCompletion(QuestCompletionPlan Plan, long SelectionEpoch,
        SessionPhase Phase, DungeonRunIdentity? DungeonIdentity);
    private PendingQuestCompletion? pendingQuestCompletion;

    public QuestFinishResult FinishSingleClearQuest(ushort questId, QuestCatalog quests, QuestRewardParameterCatalog parameters,
        ItemCatalog items, GoldLimitCatalog goldLimits, EpicPieceCatalog pieces, CharacterGrowthCatalog growth)
    {
        var captured = owner.Execute(() =>
        {
            if (!Ready || Selected is null || Phase is not (SessionPhase.Town or SessionPhase.Settlement) ||
                formalDungeon?.Ground.HasPendingPickup == true || formalDungeon?.Run.HasPendingDeath == true || formalDungeon?.Run.HasPendingSettlement == true)
                return (Character: (DfoServer.Game.Characters.Character?)null, SelectionEpoch, Phase,
                    Identity: (DungeonRunIdentity?)null, Clear: (QuestClearReceipt?)null, Pending: (PendingQuestCompletion?)null);
            var identity = Phase == SessionPhase.Settlement ? formalDungeon?.Run.Snapshot.Identity : null;
            var pending = pendingQuestCompletion;
            bool samePending = pending is not null && ReferenceEquals(pending.Plan.Expected, Selected) &&
                pending.SelectionEpoch == SelectionEpoch && pending.Phase == Phase && pending.DungeonIdentity == identity;
            // 提交已发生但返回值丢失时，Selected仍是原旧快照；先以原计划查receipt，不能先用旧revision重读活动槽。
            var clear = questId == 3145 && !Selected.CompletedQuests.Contains(3145)
                ? samePending ? pending!.Plan.Clear : ReadClearedQuest(questId)?.Clear : null;
            return (Character: Selected, SelectionEpoch, Phase, Identity: identity, Clear: clear, Pending: pendingQuestCompletion);
        });
        if (captured.Character is not { } expected)
            return new(QuestFinishOutcome.Unsupported);
        var rejection = new QuestRefusalReceipt(expected, owner.Generation, captured.SelectionEpoch, captured.Identity);
        if (questId != 3145)
            return new(QuestFinishOutcome.Unsupported, RejectionReceipt: rejection);
        if (expected.CompletedQuests.Contains(3145))
            return new(QuestFinishOutcome.AlreadyCompleted, RejectionReceipt: rejection);
        if (captured.Clear is null)
            return new(QuestFinishOutcome.Rejected, RejectionReceipt: rejection);
        QuestCompletionPreparation preparation;
        if (captured.Pending is { } pending && pending.SelectionEpoch == captured.SelectionEpoch && pending.Phase == captured.Phase &&
            pending.DungeonIdentity == captured.Identity && ReferenceEquals(pending.Plan.Expected, expected) && pending.Plan.Clear == captured.Clear)
            preparation = new(QuestCompletionOutcome.Accepted, pending.Plan, "原领奖计划精确重试");
        else
            preparation = QuestCompletionPlan.Prepare(expected, captured.Clear, quests.Get(questId), parameters, items,
                goldLimits, pieces, growth, DateTimeOffset.UtcNow);
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
            completed = Selected.CompletedQuests.ToHashSet();
            pendingQuestCompletion = null;
            return new QuestFinishResult(QuestFinishOutcome.Committed,
                new(commit, owner.Generation, captured.SelectionEpoch, captured.Identity));
        });
    }

    public bool CanPublishQuestCompletion(QuestCompletionReceipt receipt) => owner.Execute(() =>
        Ready && owner.Generation == receipt.Generation && SelectionEpoch == receipt.SelectionEpoch &&
        receipt.Commit.ProjectionCurrent && ReferenceEquals(Selected, receipt.Commit.Character) &&
        (receipt.DungeonIdentity is null ? Phase == SessionPhase.Town :
            Phase == SessionPhase.Settlement && formalDungeon?.Run.Snapshot.Identity == receipt.DungeonIdentity));

    public bool CanPublishQuestRefusal(QuestRefusalReceipt receipt) => owner.Execute(() =>
        Ready && owner.Generation == receipt.Generation && SelectionEpoch == receipt.SelectionEpoch &&
        ReferenceEquals(Selected, receipt.Character) &&
        (receipt.DungeonIdentity is null ? Phase == SessionPhase.Town :
            Phase == SessionPhase.Settlement && formalDungeon?.Run.Snapshot.Identity == receipt.DungeonIdentity));

    public QuestConditionReceipt? ReadClearedQuest(ushort questId) => owner.Execute(() =>
    {
        if (!Ready || Selected is null || Phase is not (SessionPhase.Town or SessionPhase.Settlement))
            return null;
        var clear = repository.LoadQuestClear(owner, Selected, questId);
        if (clear is null)
            return null;
        var identity = Phase == SessionPhase.Settlement ? formalDungeon?.Run.Snapshot.Identity : null;
        if (Phase == SessionPhase.Settlement && (identity is null || identity.InstanceId != clear.InstanceId ||
            identity.TaskAttempt != clear.TaskAttempt || identity.Epoch != clear.Epoch || identity.QuestId != questId))
            return null;
        return new QuestConditionReceipt(Selected, clear, owner.Generation, SelectionEpoch, identity);
    });

    public bool CanPublishQuestCondition(QuestConditionReceipt receipt) => owner.Execute(() =>
        Ready && owner.Generation == receipt.Generation && SelectionEpoch == receipt.SelectionEpoch &&
        ReferenceEquals(Selected, receipt.Character) &&
        (receipt.DungeonIdentity is null ? Phase == SessionPhase.Town :
            Phase == SessionPhase.Settlement && formalDungeon?.Run.Snapshot.Identity == receipt.DungeonIdentity));
}
