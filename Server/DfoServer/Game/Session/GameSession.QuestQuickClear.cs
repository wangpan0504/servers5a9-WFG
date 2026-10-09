using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Quests;
using DfoServer.GameWorld.Quests;

namespace DfoServer.Game.Session;

internal sealed record QuestQuickClearReceipt(Character Character, QuestQuickClearCommit? Commit, long Generation,
    long SelectionEpoch, SessionPhase Phase, DungeonRunIdentity? Identity, string Reason);

internal sealed partial class GameSession
{
    sealed record PendingQuestQuickClear(QuestQuickClearPlan Plan, long SelectionEpoch, SessionPhase Phase, DungeonRunIdentity? Identity);
    PendingQuestQuickClear? pendingQuestQuickClear;

    public QuestQuickClearReceipt? QuickClearQuest(uint parentKey, QuestCatalog quests, QuestConnectionCatalog connections)
    {
        var capture = owner.Execute(() =>
        {
            Character? current = Ready && Selected is not null && Phase is SessionPhase.Town or SessionPhase.Dungeon or SessionPhase.Settlement &&
                pendingQuestCompletion is null && pendingQuestLifecycle is null && !HasPendingQuestScene &&
                formalDungeon?.Ground.HasPendingPickup != true && formalDungeon?.Run.HasPendingDeath != true &&
                formalDungeon?.Run.HasPendingSettlement != true ? Selected : null;
            return (Character: current, SelectionEpoch, Phase, Identity: formalDungeon?.Run.Snapshot.Identity, Pending: pendingQuestQuickClear);
        });
        if (capture.Character is not { } expected) return null;
        var receipt = new QuestQuickClearReceipt(expected,null,owner.Generation,capture.SelectionEpoch,capture.Phase,capture.Identity,"问题及跳过资格不符");
        QuestQuickClearPlan? plan;
        if (capture.Pending is { } pending)
        {
            if (pending.Plan.Unit.ParentKey != parentKey || !ReferenceEquals(pending.Plan.Expected,expected) ||
                pending.SelectionEpoch != capture.SelectionEpoch || pending.Phase != capture.Phase || pending.Identity != capture.Identity)
                return receipt with { Reason = "前一跳过事务尚待恢复" };
            plan = pending.Plan;
        }
        else if (!QuestQuickClearPlan.TryCreate(expected,parentKey,quests,connections,out plan,out string reason))
            return receipt with { Reason = reason };
        return owner.Execute(() =>
        {
            if (!CanPublishQuestQuickClear(receipt)) throw new InvalidOperationException("跳过规划的原选角或场景已失效");
            pendingQuestQuickClear = new(plan!,capture.SelectionEpoch,capture.Phase,capture.Identity);
            var commit = repository.CommitQuestQuickClear(owner,plan!);
            if (!commit.ProjectionCurrent) throw new InvalidOperationException("跳过已提交，但原角色状态已变化，需要重连恢复");
            Selected = commit.Character;
            completed = QuestEligibility.Completed(Selected);
            pendingQuestQuickClear = null;
            return receipt with { Character = commit.Character, Commit = commit, Reason = "无奖励跳过已提交" };
        });
    }

    public bool CanPublishQuestQuickClear(QuestQuickClearReceipt receipt) => owner.Execute(() =>
        Ready && owner.Generation == receipt.Generation && SelectionEpoch == receipt.SelectionEpoch && Phase == receipt.Phase &&
        ReferenceEquals(Selected,receipt.Character) && (receipt.Commit?.ProjectionCurrent ?? true) &&
        (receipt.Identity is null || formalDungeon?.Run.Snapshot.Identity == receipt.Identity));
}
