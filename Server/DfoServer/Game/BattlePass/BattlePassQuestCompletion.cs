using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Quests;
using DfoServer.GameWorld.BattlePass;

namespace DfoServer.Game.BattlePass;

// 完成身份取自原领奖规划；归档和同步标记不生成新的任务完成事实。
internal sealed class BattlePassQuestCompletion
{
    readonly QuestCompletionPlan plan;
    public ImmutableArray<int> QuestIds { get; }
    public TimeProvider Clock { get; }
    public DateTimeOffset PlannedAt { get; }
    private BattlePassQuestCompletion(QuestCompletionPlan plan, ImmutableArray<int> quests, TimeProvider clock)
    {
        this.plan = plan; QuestIds = quests; Clock = clock; PlannedAt = clock.GetUtcNow();
    }

    public static BattlePassQuestCompletion? Prepare(QuestCompletionPlan plan, TimeProvider clock)
    {
        var targets = CompletedTargets(plan.Expected, plan.QuestId, plan.AcceptanceId, plan.CompletedParentId, plan.CompletedChild, plan.ConditionType);
        return targets.IsEmpty || targets.Any(id => !plan.NewCompletedQuestIds.Contains(id))
            ? null : new(plan, targets, clock);
    }

    public bool Matches(QuestCompletionPlan candidate) => ReferenceEquals(plan, candidate) &&
        QuestIds.SequenceEqual(CompletedTargets(candidate.Expected, candidate.QuestId, candidate.AcceptanceId,
            candidate.CompletedParentId, candidate.CompletedChild, candidate.ConditionType)) && QuestIds.All(candidate.NewCompletedQuestIds.Contains);

    internal static ImmutableArray<int> CompletedTargets(Character before, ushort questId, Guid acceptance,
        int? completedParent, ActiveMainQuest? child, byte conditionType)
    {
        if (before.Id == 0 || questId is 0 or >= 30000 || acceptance == Guid.Empty || completedParent is not null)
            return [];
        var completed = QuestEligibility.Completed(before);
        var active = before.ActiveMainQuests.SingleOrDefault(row => row.QuestId == questId && row.AcceptanceId == acceptance);
        if (active is null || completed.Contains(questId) || active.ConditionState != 0 && child is null && conditionType is not (0 or 42)) return [];
        if (child is not null && (child.AcceptanceId == Guid.Empty || child.AcceptanceId == acceptance ||
            child.QuestId == questId || child.ConditionState != 0 || !before.ActiveMainQuests.Contains(child) || completed.Contains(child.QuestId)))
            return [];
        return child is null ? [questId] : [questId, child.QuestId];
    }

    public static bool MatchesMission(byte category, BattlePassMission mission) => mission.RawTypeCode == 13 && mission.RawParameter == -1 &&
        (category == 0 && mission is { Id: 13, RequiredCount: 1, Experience: 100 } ||
         category == 1 && mission is { Id: 41, RequiredCount: 3, Experience: 1000 });
}
