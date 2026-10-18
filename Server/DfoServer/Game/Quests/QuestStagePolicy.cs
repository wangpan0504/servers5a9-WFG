using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.GameWorld.Quests;

namespace DfoServer.Game.Quests;

internal enum QuestStageStatus { Deferred, Granted, Satisfied, Invalidated }
internal sealed record QuestStageWitness(Guid SourceAcceptanceId, int SourceQuestId,
    long SourceCompletionRevision, byte TargetLevel, ImmutableArray<int> BoundaryQuestIds,
    ImmutableArray<QuestStageEdge> Evidence, int RuleVersion, QuestStageStatus Status);
internal sealed record QuestStageState(ushort CharacterId, long Revision,
    ImmutableArray<QuestStageWitness> Witnesses);
internal sealed record QuestStageClaim(Guid SourceAcceptanceId, Guid TriggerAcceptanceId,
    byte TargetLevel, ulong BeforeExperience, ulong AfterExperience, uint Amount);
internal sealed record QuestStagePlan(QuestStageState Expected,
    ImmutableArray<QuestStageWitness> WitnessesAfter, QuestStageClaim? Claim, string Reason)
{
    public uint BonusExperience => Claim?.Amount ?? 0;
}

internal static class QuestStagePolicy
{
    const int RuleVersion = 1;

    public static QuestStagePlan Prepare(Character before, Character normalAfter,
        StoryQuest source, Guid acceptanceId, QuestCatalog quests, QuestStageGraph graph,
        IReadOnlyList<ulong> cumulativeExperience, QuestStageState state)
    {
        QuestStagePlan Unchanged(string reason) => new(state, state.Witnesses, null, reason);
        if (state.CharacterId != before.Id || state.Revision != before.Revision || normalAfter.Id != before.Id ||
            normalAfter.Revision != before.Revision + 1 || state.Witnesses.IsDefault)
            return Unchanged("stage-unknown-state");
        if (acceptanceId == Guid.Empty || !OrdinaryStory(source, quests) ||
            !before.ActiveMainQuests.Any(row => row.QuestId == source.Id && row.AcceptanceId == acceptanceId &&
                (source.Condition == "[question]" ? QuestQuestionProgress.OrdinaryChoice(source, row.ConditionState, quests) &&
                    normalAfter.QuestAnswers.TryGetValue(source.Id, out byte answer) && answer == row.ConditionState : row.ConditionState == 0)) ||
            QuestEligibility.Completed(before).Contains(source.Id) || !normalAfter.CompletedQuests.Contains(source.Id) ||
            normalAfter.ActiveMainQuests.Any(row => row.QuestId == source.Id))
            return Unchanged("stage-ineligible-source");

        bool mayCreate = !source.Attributes.Contains("not give exp quest");
        if (!mayCreate)
        {
            bool related = false;
            var previousCompleted = QuestEligibility.Completed(before);
            foreach (var witness in state.Witnesses.Where(row => row.Status == QuestStageStatus.Deferred))
            {
                if (!TryFrontier(witness.SourceQuestId, before, quests, graph, previousCompleted,
                    out var previousFrontier, out _, out string reason)) return Unchanged(reason);
                related |= previousFrontier.Any(row => row.Id == source.Id);
            }
            // 禁经验任务只可作为已证当前工作的真实触发，不能借它创设新的来源资格。
            if (!related) return Unchanged("stage-ineligible-trigger");
        }

        var completed = QuestEligibility.Completed(normalAfter);
        var witnesses = state.Witnesses.ToBuilder();
        var frontiers = new Dictionary<Guid, ImmutableArray<StoryQuest>>();
        // 所有候选先证明完整，再决定金额；未知图不能部分冻结资格。
        for (int at = 0; at < witnesses.Count; at++)
        {
            var witness = witnesses[at];
            if (witness.Status != QuestStageStatus.Deferred) continue;
            if (witness.SourceAcceptanceId == Guid.Empty || witness.TargetLevel is < 1 or > 95 ||
                witness.SourceCompletionRevision > before.Revision || witness.RuleVersion != RuleVersion ||
                witness.Evidence.IsDefaultOrEmpty || witness.BoundaryQuestIds.IsDefaultOrEmpty ||
                witness.Evidence.Any(edge => !graph.Successors(edge.FromQuestId).Any(current => SameEdge(edge, current))))
                return Unchanged("stage-unknown-witness");
            if (!completed.Contains(witness.SourceQuestId))
            {
                witnesses[at] = witness with { Status = QuestStageStatus.Invalidated };
                continue;
            }
            if (!TryFrontier(witness.SourceQuestId, normalAfter, quests, graph, completed,
                out var frontier, out _, out string reason)) return Unchanged(reason);
            frontiers.Add(witness.SourceAcceptanceId, frontier);
        }

        if (mayCreate && !witnesses.Any(row => row.SourceAcceptanceId == acceptanceId))
        {
            if (!TryFrontier(source.Id, normalAfter, quests, graph, completed,
                out var frontier, out var evidence, out string reason)) return Unchanged(reason);
            // 普通奖励可能已跨过目标；仍以领奖前等级固定最早未来目标并记录Satisfied0。
            var future = frontier.Where(row => row.MinimumLevel > before.Level).ToArray();
            if (future.Length != 0)
            {
                byte target = (byte)future.Min(row => row.MinimumLevel);
                var witness = new QuestStageWitness(acceptanceId, source.Id, normalAfter.Revision, target,
                    frontier.Select(row => row.Id).ToImmutableArray(), evidence, RuleVersion, QuestStageStatus.Deferred);
                witnesses.Add(witness);
                frontiers.Add(acceptanceId, frontier);
            }
        }

        var consumed = witnesses.Where(row => row.Status is QuestStageStatus.Granted or QuestStageStatus.Satisfied)
            .Select(row => row.TargetLevel).ToHashSet();
        var ready = new List<int>();
        for (int at = 0; at < witnesses.Count; at++)
        {
            var witness = witnesses[at];
            if (witness.Status != QuestStageStatus.Deferred) continue;
            if (witness.TargetLevel > cumulativeExperience.Count)
                return Unchanged("stage-unknown-experience-table");
            ulong targetExperience = cumulativeExperience[witness.TargetLevel - 1];
            if (consumed.Contains(witness.TargetLevel) || normalAfter.Learning.Experience >= targetExperience)
            {
                witnesses[at] = witness with { Status = QuestStageStatus.Satisfied };
                consumed.Add(witness.TargetLevel);
                continue;
            }
            var frontier = frontiers[witness.SourceAcceptanceId];
            if (!frontier.Any(row => row.MinimumLevel == witness.TargetLevel && witness.BoundaryQuestIds.Contains(row.Id)))
            {
                witnesses[at] = witness with { Status = QuestStageStatus.Invalidated };
                continue;
            }
            if (frontier.Any(row => row.MinimumLevel < witness.TargetLevel || row.MinimumLevel <= normalAfter.Level)) continue;
            ready.Add(at);
        }

        QuestStageClaim? claim = null;
        if (ready.Count != 0)
        {
            int selected = ready.OrderBy(at => witnesses[at].TargetLevel).ThenBy(at => witnesses[at].SourceCompletionRevision)
                .ThenBy(at => witnesses[at].SourceQuestId).ThenBy(at => witnesses[at].SourceAcceptanceId).First();
            var witness = witnesses[selected];
            ulong targetExperience = cumulativeExperience[witness.TargetLevel - 1];
            ulong amount = targetExperience - normalAfter.Learning.Experience;
            if (amount > int.MaxValue) return Unchanged("stage-unknown-experience-amount");
            claim = new(witness.SourceAcceptanceId, acceptanceId, witness.TargetLevel,
                normalAfter.Learning.Experience, targetExperience, (uint)amount);
            for (int at = 0; at < witnesses.Count; at++)
                if (witnesses[at].Status == QuestStageStatus.Deferred && witnesses[at].TargetLevel == witness.TargetLevel)
                    witnesses[at] = witnesses[at] with { Status = at == selected ? QuestStageStatus.Granted : QuestStageStatus.Satisfied };
        }
        return new(state, witnesses.ToImmutable(), claim, claim is null ? "stage-no-grant" : "stage-granted");
    }

    static bool SameEdge(QuestStageEdge first, QuestStageEdge second) => first.FromQuestId == second.FromQuestId &&
        first.NextQuestId == second.NextQuestId && first.PrerequisiteGroup.SequenceEqual(second.PrerequisiteGroup);

    static bool OrdinaryStory(StoryQuest quest, QuestCatalog quests) => quest is { Grade: "[epic]", MainQuest: null,
        Definition: { AccountQuest: false } } && !QuestCycle.Daily(quest) && !QuestCycle.Repeat(quest) &&
        !QuestRelationProgress.IsCascadeMarker(quest) &&
        QuestEligibility.InspectResource(quest).Entry == QuestResourceEntry.Ordinary &&
        QuestCoverage.HasOrdinaryDeclarations(quest) && QuestCoverage.HasSupportedTarget(quest, quests);

    static bool TryFrontier(int sourceId, Character character, QuestCatalog quests, QuestStageGraph graph,
        IReadOnlySet<int> completed, out ImmutableArray<StoryQuest> frontier,
        out ImmutableArray<QuestStageEdge> evidence, out string reason)
    {
        var boundary = new Dictionary<int, StoryQuest>();
        var edges = new List<QuestStageEdge>();
        var visiting = new HashSet<int>();
        var visited = new HashSet<int>();
        string failure = "";

        bool Visit(int id)
        {
            if (visiting.Contains(id)) { failure = "stage-unknown-cycle"; return false; }
            if (!visited.Add(id)) return true;
            visiting.Add(id);
            var nextEdges = graph.Successors(id);
            foreach (var alternatives in nextEdges.GroupBy(row => row.NextQuestId))
            {
                if (!quests.TryGet(alternatives.Key, out var next)) { failure = "stage-unknown-definition"; return false; }
                if (next.Grade != "[epic]" || next.MainQuest is not null || next.Definition?.AccountQuest == true) continue;
                var access = QuestEligibility.InspectResource(next);
                if (access.Entry is QuestResourceEntry.Invalid or QuestResourceEntry.OutsideLevels)
                { failure = "stage-unknown-definition"; return false; }
                if (access.Entry != QuestResourceEntry.Ordinary || QuestRelationProgress.IsCascadeMarker(next)) continue;
                if (!OrdinaryStory(next, quests)) { failure = "stage-unknown-definition"; return false; }
                bool unknownAnswer = false;
                if (!completed.Contains(next.Id))
                {
                    if (!QuestEligibility.CharacterRequirements(character, next, quests).Eligible) continue;
                    bool excludedAnswer = false;
                    foreach (var required in next.Definition!.RequiredAnswers)
                    {
                        if (!quests.TryGet(required.QuestId, out _) || required.Answer is < 0 or > 254 ||
                            !completed.Contains(required.QuestId) ||
                            !(character.AccountQuestAnswers.TryGetValue(required.QuestId, out byte answer) ||
                                character.QuestAnswers.TryGetValue(required.QuestId, out answer)))
                            unknownAnswer = true;
                        else if (answer != required.Answer) excludedAnswer = true;
                    }
                    // 已选其它答案可排除本支；尚未选择的问题不能被当作永久不适用。
                    if (excludedAnswer) continue;
                }
                var matching = alternatives.FirstOrDefault(edge => edge.PrerequisiteGroup.Length != 0 &&
                    edge.PrerequisiteGroup.All(prerequisite => quests.TryGet(prerequisite, out var required) &&
                        required.Definition is { Diagnostics.Length: 0 } && completed.Contains(prerequisite)));
                if (matching is null)
                {
                    // OR已有另一完整组时，本支未选的旧引用不构成未知；但不能借它扩展本支来源。
                    if (next.Definition?.PrerequisiteGroups.Any(group => group.Length != 0 &&
                        group.All(prerequisite => quests.TryGet(prerequisite, out var required) &&
                            required.Definition is { Diagnostics.Length: 0 } && completed.Contains(prerequisite))) == true) continue;
                    failure = "stage-unknown-prerequisite";
                    return false;
                }
                if (next.Definition is null || next.Definition.Diagnostics.Length != 0)
                { failure = "stage-unknown-definition"; return false; }
                // 只有当前来源的因果组已证成立，未知回答才属于本次阶段的阻挡范围。
                if (unknownAnswer) { failure = "stage-unknown-answer"; return false; }
                edges.Add(matching);
                if (completed.Contains(next.Id))
                {
                    // 历史完成只沿已证因果边穿行，不按现职业重新判断旧节点，也不扫描episode。
                    if (!Visit(next.Id)) return false;
                    continue;
                }
                // 活动槽即使已经归零仍是工作；只移除当前候选来复用全部其余资格检查。
                var eligibilityCharacter = character.ActiveMainQuests.Any(row => row.QuestId == next.Id)
                    ? character with { ActiveMainQuests = character.ActiveMainQuests.Where(row => row.QuestId != next.Id).ToArray() }
                    : character;
                if (QuestEligibility.Evaluate(eligibilityCharacter, next, quests,
                    mode: QuestEligibilityMode.IgnoreMinimumLevel).Eligible) boundary.TryAdd(next.Id, next);
            }
            visiting.Remove(id);
            return true;
        }

        bool success;
        if (!quests.TryGet(sourceId, out var source)) success = false;
        else
        {
            try
            {
                success = Visit(sourceId);
                if (success && QuestRelationProgress.CascadeMarker(source, quests) is { } marker)
                {
                    if (!completed.Contains(marker)) { success = false; failure = "stage-unknown-cascade"; }
                    else success = Visit(marker);
                }
            }
            catch (InvalidDataException) { success = false; failure = "stage-unknown-cascade"; }
        }
        frontier = boundary.Values.OrderBy(row => row.Id).ToImmutableArray();
        evidence = edges.ToImmutableArray();
        reason = failure.Length == 0 && !success ? "stage-unknown-definition" : failure;
        return success;
    }
}
