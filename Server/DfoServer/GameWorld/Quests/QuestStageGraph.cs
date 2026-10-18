using System.Collections.Immutable;

namespace DfoServer.GameWorld.Quests;

internal sealed record QuestStageEdge(int FromQuestId, int NextQuestId,
    ImmutableArray<int> PrerequisiteGroup);

internal sealed class QuestStageGraph
{
    readonly ImmutableDictionary<int, ImmutableArray<QuestStageEdge>> successors;

    QuestStageGraph(ImmutableDictionary<int, ImmutableArray<QuestStageEdge>> successors)
        => this.successors = successors;

    // 保留完整AND组及OR替代，不把episode顺序解释成前置。
    public static QuestStageGraph Build(QuestCatalog quests)
    {
        var edges = new Dictionary<int, List<QuestStageEdge>>();
        foreach (var quest in quests.All.OrderBy(row => row.Id))
            foreach (var group in quest.Definition?.PrerequisiteGroups ?? [])
                foreach (int from in group.Distinct())
                {
                    if (!edges.TryGetValue(from, out var rows)) edges.Add(from, rows = []);
                    rows.Add(new(from, quest.Id, group));
                }
        return new(edges.ToImmutableDictionary(row => row.Key, row => row.Value.ToImmutableArray()));
    }

    public ImmutableArray<QuestStageEdge> Successors(int questId)
        => successors.GetValueOrDefault(questId, []);
}
