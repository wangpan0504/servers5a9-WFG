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
        ActiveMainQuest[] successors;
        quests.TryGet(predecessor,out var source);
        var parent = source?.MainQuest is > 0 && quests.TryGet(source.MainQuest.Value,out var owner) ? owner : source;
        if (parent is not null && !completed.Contains(parent.Id) && QuestRelationProgress.SequentialParent(parent,quests))
            successors = SequentialSuccessors(character,entry,parent,quests,completed);
        else
        {
            if (source?.MainQuest is > 0 && completed.Contains(source.MainQuest.Value)) predecessor = source.MainQuest.Value;
            if (!completed.Contains(predecessor)) return null;
            bool epic = quests.TryGet(predecessor,out var predecessorQuest) && predecessorQuest.Grade == "[epic]";
            successors = character.ActiveMainQuests.Where(active => quests.TryGet(active.QuestId,out var quest) &&
                (!epic || quest.Grade == "[epic]" && quest.MainQuest is not > 0) &&
                quest.Definition is { } definition && definition.PrerequisiteGroups.Any(group => group.Contains(predecessor)) &&
                QuestCoverage.HasOrdinaryDeclarations(quest)).ToArray();
        }
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

    // 顺序父任务在末子交付前仍活动，父内续图沿实际完成的子序列和本次Run接取身份查找。
    static ActiveMainQuest[] SequentialSuccessors(Character character, DungeonEntryPlan entry, StoryQuest parent,
        QuestCatalog quests, IReadOnlySet<int> completed)
    {
        var parents = character.ActiveMainQuests.Where(row => row.QuestId == parent.Id).ToArray();
        var bound = entry.QuestAcceptances.Where(row => row.QuestId == parent.Id).ToArray();
        if (parents is not [{ Slot: <= 29, AcceptanceId: var acceptance } active] || acceptance == Guid.Empty ||
            bound.Length != 1 || bound[0].AcceptanceId != acceptance ||
            character.ActiveMainQuests.Count(row => row.Slot == active.Slot) != 1 ||
            character.ActiveMainQuests.Count(row => row.AcceptanceId == acceptance) != 1 ||
            active.ConditionState != QuestRelationProgress.Initial(parent,quests,completed)) return [];
        var prefix = parent.IntData.TakeWhile(completed.Contains).ToArray();
        if (!prefix.Any(id => entry.QuestAcceptances.Any(row => row.QuestId == id && row.AcceptanceId != Guid.Empty))) return [];
        var children = character.ActiveMainQuests.Where(row => parent.IntData.Contains(row.QuestId)).ToArray();
        if (children is not [{ Slot: <= 29, AcceptanceId: var childAcceptance } child] || childAcceptance == Guid.Empty ||
            child.QuestId != parent.IntData.FirstOrDefault(id => !completed.Contains(id)) ||
            character.ActiveMainQuests.Count(row => row.Slot == child.Slot) != 1 ||
            character.ActiveMainQuests.Count(row => row.AcceptanceId == childAcceptance) != 1) return [];
        return [child];
    }
}
