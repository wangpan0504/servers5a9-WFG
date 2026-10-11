using DfoServer.Game.Characters;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Quests;
using DfoServer.Game.Quests;
using System.Collections.Immutable;

namespace DfoServer.Game.Dungeons;

// 普通请求的任务字段为0，与任务专用maze分别准入；计划不建立实例或证明客户端加载。
// 调用方仍须在有效账号owner下绑定epoch，并在提交时复验原角色快照。
internal sealed record DungeonEntryPlan(
    ushort CharacterId,
    long CharacterRevision,
    uint DungeonId,
    uint QuestId,
    byte ActiveQuestSlot,
    byte Difficulty,
    DungeonMaze Maze,
    DungeonMap StartMap,
    DungeonMap BossMap)
{
    public int? BasisLevel { get; init; }
    public Guid QuestAcceptanceId { get; init; }
    public bool IsOrdinary => QuestId == 0 && RouteQuestId == 0;
    public int? MonsterBasisLevel { get; init; }
    public DungeonMap BaseBossMap { get; init; } = BossMap;
    public uint RouteQuestId { get; init; } = QuestId;
    public ImmutableArray<ActiveMainQuest> QuestAcceptances { get; init; } = [];
    public float ExperienceWeight { get; init; } = 1;
    public IReadOnlyList<int> NamedMonsters { get; init; } = Array.Empty<int>();
    public bool IndividualMapMovement { get; init; }
    public bool InitializationCounterSupported { get; init; }
    public bool IsQuestReplay { get; init; }
    public bool IsTerminalMap(DungeonMap map) => map == BossMap || BossMap.Kind != "layered" && map.IsConditional &&
        map.Position == BaseBossMap.Position && Maze.ConditionalMaps.Contains(map);

    public static bool TryCreateReplay(DungeonCatalog catalog,QuestReplayPlan replay,out DungeonEntryPlan? plan,out string reason)
    {
        plan = null;
        DungeonDefinition definition;
        try { definition = catalog.Get(checked((int)replay.DungeonId)); }
        catch (Exception error) when (error is InvalidDataException or NotSupportedException or FileNotFoundException)
        { reason = "dungeon-resource-unavailable"; return false; }
        return TryCreate(definition,replay.Expected,replay.DungeonId,replay.QuestId,0,out plan,out reason,
            quest: replay.Quest,resolveMaze: maze => catalog.ResolveOrdinaryMaze(definition,maze),isReplay: true);
    }

    public static bool TryCreate(DungeonCatalog catalog, Character character, uint dungeonId, uint questId,
        byte difficulty, out DungeonEntryPlan? plan, out string reason, bool allowClearedQuest = false, StoryQuest? quest = null,
        Func<int,int>? draw = null, QuestCatalog? quests = null)
    {
        plan = null;
        reason = ValidateRequest(character, dungeonId, questId, difficulty, allowClearedQuest, quest);
        if (reason != "resource-request-ready")
            return false;
        DungeonDefinition definition;
        try
        {
            definition = catalog.Get((int)dungeonId);
        }
        catch (Exception error) when (error is InvalidDataException or NotSupportedException or FileNotFoundException)
        {
            reason = "dungeon-resource-unavailable";
            return false;
        }

        return TryCreate(definition, character, dungeonId, questId, difficulty, out plan, out reason, allowClearedQuest, quest,draw,quests,
            maze => catalog.ResolveOrdinaryMaze(definition,maze,draw));
    }

    internal static bool TryCreate(DungeonDefinition definition, Character character, uint dungeonId, uint questId,
        byte difficulty, out DungeonEntryPlan? plan, out string reason, bool allowClearedQuest = false, StoryQuest? quest = null,
        Func<int,int>? draw = null, QuestCatalog? quests = null, Func<DungeonMaze,DungeonMaze>? resolveMaze = null,bool isReplay = false)
    {
        plan = null;
        reason = ValidateRequest(character, dungeonId, questId, difficulty, allowClearedQuest, quest,isReplay);
        if (reason != "resource-request-ready")
            return false;
        if (definition.Id != dungeonId)
        {
            reason = "dungeon-resource-mismatch";
            return false;
        }

        if (definition.MinimumLevel is null)
        {
            reason = "dungeon-level-unknown";
            return false;
        }

        if (character.Level < definition.MinimumLevel)
        {
            reason = "dungeon-level-too-low";
            return false;
        }

        // 仅识别匹配 PVF 已确认的三个整数；未知引用和其他条件形状不能变成默认迷宫。
        uint routeQuest = !isReplay && quest?.MainQuest is > 0 ? checked((uint)quest.MainQuest.Value) : questId;
        var candidates = DistinctQuestRoutes(definition.Mazes.Where(maze => maze.ConnectsQuest(questId,difficulty)));
        if (questId == 0 && quests is not null)
        {
            candidates = DistinctQuestRoutes(definition.Mazes.Where(maze => character.ActiveMainQuests.Any(active => maze.ConnectsQuest(active.QuestId,difficulty))));
            if (candidates.Length == 0)
                candidates = DistinctQuestRoutes(definition.Mazes.Where(maze => QuestEligibility.Completed(character).Any(id => maze.ConnectsQuest((uint)id,difficulty,1))));
            if (candidates.Length == 0)
            {
                candidates = definition.Mazes.Where(maze => maze.QuestConnectionTokens.Count == 0).ToArray();
                if (candidates.Length > 0)
                {
                    int index = candidates.Length == 1 ? 0 : (draw ?? System.Security.Cryptography.RandomNumberGenerator.GetInt32)(candidates.Length);
                    if (index < 0 || index >= candidates.Length) throw new InvalidDataException("迷宫随机索引越界");
                    candidates = [candidates[index]];
                }
            }
            else if (candidates.Length == 1)
            {
                routeQuest = checked((uint)candidates[0].ConnectionQuestId!.Value);
                if (quests is null || !quests.TryGet((int)routeQuest,out quest) || !QuestCoverage.HasOrdinaryDeclarations(quest))
                { reason = "quest-route-definition-unavailable"; return false; }
            }
        }
        else if (candidates.Length == 0 && routeQuest != questId)
            candidates = DistinctQuestRoutes(definition.Mazes.Where(maze => maze.ConnectsQuest(routeQuest,difficulty)));
        else if (questId == 0) candidates = definition.Mazes.Where(maze => maze.QuestConnectionTokens.Count == 0).ToArray();
        else routeQuest = questId;
        if (candidates.Length != 1)
        {
            reason = candidates.Length == 0 ? "route-maze-unavailable" : "route-maze-ambiguous";
            if (questId != 0 || quests is not null) reason = candidates.Length == 0 ? "quest-maze-unavailable" : "quest-maze-ambiguous";
            return false;
        }

        DungeonMaze maze;
        try { maze = resolveMaze is null ? candidates[0] : resolveMaze(candidates[0]); }
        catch (Exception error) when (error is InvalidDataException or NotSupportedException or FileNotFoundException)
        { reason = "dungeon-resource-unavailable"; return false; }
        if (!definition.AllowsDifficulty(difficulty,maze.QuestConnectionTokens.Count != 0))
        { reason = "difficulty-unsupported"; return false; }
        if (maze.SourceOrdinal < 0 || maze.Width <= 0 || maze.Height <= 0 || maze.Starts.Count != 1 ||
            (maze.StoryStages.Count == 0 ? maze.Bosses.Count != 1 : maze.StoryStages.Count != maze.Bosses.Count)
            || maze.MapSpecification is not { Count: > 0 } maps)
        {
            reason = "quest-maze-layout-unsupported";
            return false;
        }

        var start = maze.Starts[0];
        var boss = maze.StoryStages.Count > 0 ? maze.StoryStages[^1].Position : maze.Bosses[0];
        if (!InBounds(start, maze) || !InBounds(boss, maze))
        {
            reason = "quest-maze-layout-unsupported";
            return false;
        }

        var starts = maps.Where(map => map.Position == start).ToArray();
        if (maze.StoryStages.Count > 0 && starts.Length > 1)
            starts = [starts.FirstOrDefault(map => map.StoryStageIndex < 0) ?? starts[0]];
        var bosses = maze.StoryStages.Count > 0 ? new[] { maze.StoryStages[^1] } : maps.Where(map => map.Position == boss).ToArray();
        if (starts.Length != 1 || bosses.Length != 1 || bosses[0].Kind != "boss")
        {
            reason = "quest-maze-map-unavailable";
            return false;
        }

        var terminal = bosses[0];
        if (quest is not null && QuestClearEligibility.IsSingleMap(quest))
        {
            var layers = maze.LayeredMaps.Where(map => map.Position == boss && map.Id == quest.IntData[0]).ToArray();
            if (layers.Length != 0) terminal = layers[^1];
            else if (quest.IntData[0] != terminal.Id)
            {
                var replacements = maze.ConditionalMaps.Where(map => map.Position == boss && map.Id == quest.IntData[0]).ToArray();
                if (replacements.Length != 1) { reason = "quest-terminal-map-unavailable"; return false; }
                terminal = replacements[0];
            }
        }
        if (quest is not null && (quest.Id != (questId == 0 ? routeQuest : questId) || quest.Definition is null ||
            quest.Definition.Dungeons.Length != 0 && !quest.Definition.Dungeons.Any(row => row.DungeonId == dungeonId)))
        {
            reason = "quest-dungeon-mismatch";
            return false;
        }

        // SourceOrdinal 仅保留资源顺序，不能直接作为 NOTI28 的 wire 枚举。
        var active = questId == 0 || isReplay ? null : character.ActiveMainQuests.Single(quest => quest.QuestId == questId);
        plan = new(character.Id, character.Revision, dungeonId,isReplay ? 0 : questId, active?.Slot ?? byte.MaxValue, difficulty, maze, starts[0], terminal)
        {
            BasisLevel = definition.BasisLevel,
            MonsterBasisLevel = definition.BasisLevel,
            QuestAcceptanceId = active?.AcceptanceId ?? Guid.Empty,
            BaseBossMap = bosses[0],
            RouteQuestId = routeQuest,
            QuestAcceptances = character.ActiveMainQuests.ToImmutableArray(),
            ExperienceWeight = DungeonKillExperienceCatalog.GetDungeonWeight(definition),
            NamedMonsters = DungeonKillExperienceCatalog.GetNamedMonsters(definition),
            IndividualMapMovement = definition.IndividualMapMovement,
            IsQuestReplay = isReplay,
            InitializationCounterSupported = maze.LayeredMaps.Count == 0 && maze.StoryStages.Count == 0 &&
                !definition.RawSections.Any(section => section.Tag is "mercenary broker dungeon" or "dnfaos dungeon")
        };
        reason = "resource-plan-ready";
        return true;
    }

    static DungeonMaze[] DistinctQuestRoutes(IEnumerable<DungeonMaze> source)
    {
        var routes = new List<DungeonMaze>();
        foreach (var maze in source)
            if (maze.QuestConnectionTokens.Count == 0 || !routes.Any(existing =>
                existing.QuestConnectionTokens.SequenceEqual(maze.QuestConnectionTokens) && existing.SameLayout(maze)))
                routes.Add(maze);
        return routes.ToArray();
    }

    private static string ValidateRequest(Character character, uint dungeonId, uint questId, byte difficulty, bool allowClearedQuest, StoryQuest? quest,bool isReplay = false)
    {
        if (dungeonId is 0 or > int.MaxValue)
            return "dungeon-unsupported";
        if (questId >= 30000)
            return "quest-unsupported";
        if (difficulty > 4 || questId == 0 && dungeonId is 3 or 5 or 6 or 7 or 8 or 9 or 1000 && difficulty != 0)
            return "difficulty-unsupported";
        if (character.Level is < 1 or > 95)
            return "character-level-invalid";
        if (isReplay)
            return questId > 0 && difficulty == 0 && quest?.Id == questId && QuestEligibility.Completed(character).Contains((int)questId)
                ? "resource-request-ready" : "quest-replay-unavailable";
        if (questId == 0) return "resource-request-ready";
        if (QuestEligibility.Completed(character).Contains((int)questId))
            return "quest-already-completed";
        var active = character.ActiveMainQuests.Where(quest => quest.QuestId == questId).ToArray();
        if (active.Length == 0)
            return "quest-not-active";
        if (active.Length != 1 || active[0].Slot > 29
            || character.ActiveMainQuests.Count(quest => quest.Slot == active[0].Slot) != 1)
            return "quest-active-conflict";
        // 已达成条件只有持久清图资格才能继续挑战；裸条件值不能伪装正式通关。
        if ((quest is null || QuestClearEligibility.IsSingleMap(quest)) && active[0].ConditionState != 1 &&
            !(allowClearedQuest && active[0].ConditionState == 0) ||
            quest?.Condition == "[condition under clear]" && (!QuestDungeonClearProgress.ValidScalar(quest,active[0].ConditionState) ||
                active[0].ConditionState == 0 && !allowClearedQuest) ||
            quest?.Condition is "[hunt enemy]" or "[hunt monster]" && !QuestHuntProgress.ValidScalar(quest, active[0].ConditionState))
            return "quest-condition-not-pending";
        return "resource-request-ready";
    }

    private static bool InBounds(DungeonPoint point, DungeonMaze maze) =>
        point.X >= 0 && point.Y >= 0 && point.X < maze.Width && point.Y < maze.Height;
}
