using DfoServer.Game.Characters;
using DfoServer.Game.Quests;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Quests;

namespace DfoServer.Game.Dungeons;

internal sealed record DungeonQuestContinuation(ActiveMainQuest Quest, uint DungeonId, byte Difficulty)
{
    public byte? RequestedDifficulty { get; init; }
}

internal static class DungeonQuestContinuationRules
{
    // 后继来自真实前置和接取身份；不同职业的同名任务不能按资源首行决定。
    public static DungeonQuestContinuation? Resolve(Character character, DungeonEntryPlan entry, QuestCatalog quests,
        DungeonCatalog dungeons, IEnumerable<int> selectableDungeons, out string reason)
    {
        reason = "no-accepted-successor";
        int predecessor = checked((int)entry.RouteQuestId);
        var completed = QuestEligibility.Completed(character);
        if (quests.TryGet(predecessor,out var source) && source.MainQuest is > 0 && completed.Contains(source.MainQuest.Value))
            predecessor = source.MainQuest.Value;
        if (!completed.Contains(predecessor)) return null;
        bool epic = quests.TryGet(predecessor,out var predecessorQuest) && predecessorQuest.Grade == "[epic]";
        var successors = character.ActiveMainQuests.Where(active => quests.TryGet(active.QuestId,out var quest) &&
            (!epic || quest.Grade == "[epic]" && quest.MainQuest is not > 0) &&
            quest.Definition is { } definition && definition.PrerequisiteGroups.Any(group => group.Contains(predecessor)) &&
            QuestCoverage.HasOrdinaryDeclarations(quest)).ToArray();
        if (successors.Length == 0) return null;
        reason = "successor-ambiguous";
        if (successors.Length != 1) return null;
        var active = successors[0];
        var next = quests.Get(active.QuestId);
        var targets = selectableDungeons.Distinct().Where(id => next.Definition!.Dungeons.Length == 0 ||
            next.Definition.Dungeons.Any(row => row.DungeonId == id));
        var routes = new List<DungeonEntryPlan>();
        bool connected = false;
        foreach (int id in targets)
        {
            try
            {
                var dungeon = dungeons.Get(id);
                connected |= dungeon.Mazes.Any(maze => maze.HasActiveQuestConnection(active.QuestId) ||
                    next.MainQuest is > 0 && maze.HasActiveQuestConnection(next.MainQuest.Value));
                if (DungeonEntryPlan.TryCreate(dungeons,character,checked((uint)id),active.QuestId,entry.RequestedDifficulty ?? entry.Difficulty,
                    out var route,out _,quest: next,quests: quests)) routes.Add(route!);
            }
            catch (Exception error) when (error is InvalidDataException or NotSupportedException or FileNotFoundException) { }
        }
        if (routes.Count == 1)
        {
            reason = "successor-dungeon-ready";
            return new(active,routes[0].DungeonId,routes[0].Difficulty) { RequestedDifficulty = routes[0].RequestedDifficulty };
        }
        if (routes.Count == 0 && !connected && next.Definition!.Dungeons.Length == 0 && next.Condition == "[meet npc]")
        {
            reason = "successor-town-ready";
            return new(active,0,0);
        }
        reason = routes.Count == 0 ? "successor-route-unavailable" : "successor-route-ambiguous";
        return null;
    }
}
