using DfoServer.GameWorld.Dungeons;

namespace DfoServer.Game.Dungeons;

internal sealed class PreparedStrongway2019RoomReplacement(SinglePlayerDungeonRun run, DungeonRunIdentity expected,
    Guid changeId, DungeonPoint target, DungeonMap originalMap, DungeonMonsterPlan plan)
{
    internal SinglePlayerDungeonRun Run { get; } = run;
    public DungeonRunIdentity Expected { get; } = expected;
    public Guid ChangeId { get; } = changeId;
    public DungeonPoint Target { get; } = target;
    internal DungeonMap OriginalMap { get; } = originalMap;
    public DungeonMonsterPlan Plan { get; } = plan;
    public uint NextObjectKey => Plan.NextObjectKey;
}

internal sealed partial class SinglePlayerDungeonRun
{
    // 原Session/实例命令门串行访问；每个候选只保留最新准备对象，不另建副本状态或账号锁。
    readonly Dictionary<int, (PreparedStrongway2019RoomReplacement Prepared, Room Room)> strongwayCandidates = [];
    readonly Dictionary<Guid, PreparedStrongway2019RoomReplacement> committedStrongwayReplacements = [];
    uint strongwayObjectKeyFloor;

    internal bool IsStrongway2019ReplacementTarget(DungeonPoint target) =>
        rooms.TryGetValue(target, out var room) && !ReferenceEquals(room, current) && !room.Loaded &&
        !entry.IsTerminalMap(room.Plan.Room.Map) && target != entry.BossMap.Position &&
        target != entry.BaseBossMap.Position && room.Plan.Room.Map.Kind == "map";

    // 资源由调用方在锁外经DungeonRoomCatalog.Get读取；此处仍用FromRoom校验真实生成行。
    internal bool TryPrepareStrongway2019NextRoom(DungeonRunIdentity expected, Guid changeId,
        DungeonPoint target, Strongway2019Catalog catalog, DungeonRoomDefinition resourceRoom, ushort firstObjectKey,
        out PreparedStrongway2019RoomReplacement? prepared, out string reason, Func<int, int>? draw = null)
    {
        prepared = null;
        reason = "strongway-next-room-invalid-context";
        if (!Matches(expected) || Phase != SinglePlayerDungeonPhase.Dungeon || !CanAdvance() || HasPendingDeath || HasPendingSettlement ||
            changeId == Guid.Empty || committedStrongwayReplacements.ContainsKey(changeId) ||
            !catalog.MatchingDungeons.Any(row => row.DungeonId == entry.DungeonId) || entry.QuestId != 0 ||
            entry.Maze.DummyStages.Count != 0 || entry.Maze.StoryStages.Count != 0 || entry.Maze.LayeredMaps.Count != 0 ||
            entry.Maze.ConditionalMaps.Count != 0 || entry.Maze.MapSpecification is null ||
            !connections.Contains((current.Plan.Room.Map.Position, target)) || !IsStrongway2019ReplacementTarget(target) ||
            !rooms.TryGetValue(target, out var original) ||
            !catalog.RandomMapIds.Contains(resourceRoom.Map.Id) ||
            !resourceRoom.Map.Path.StartsWith("map/", StringComparison.Ordinal) || !resourceRoom.Map.Path.EndsWith(".map", StringComparison.Ordinal))
            return false;

        uint floor = Math.Max(strongwayObjectKeyFloor, allRooms.Max(room => room.Plan.NextObjectKey));
        if (firstObjectKey < floor)
        {
            reason = "strongway-next-room-object-key-reused";
            return false;
        }
        var map = original.Plan.Room.Map with
        {
            Id = resourceRoom.Map.Id, Path = resourceRoom.Map.Path, CandidateIds = [], CandidateWeights = []
        };
        try
        {
            var source = resourceRoom with { Map = map };
            var plan = DungeonMonsterPlan.FromRoom(source, entry.CharacterId, firstObjectKey,
                entry.MonsterBasisLevel ?? entry.BasisLevel, playerTeam: entry.PlayerTeam);
            if (!plan.Monsters.Any(monster => monster.IsBlocking && monster.CanReportDeath))
            {
                reason = "strongway-next-room-no-combat-actor";
                return false;
            }
            if (entry.ChampionPercents.IsEnabled)
                plan = plan.WithChampionRolls(entry.ChampionPercents, entry.Difficulty, draw ?? Random.Shared.Next, entry.NamedMonsters);
            plan = plan with { Monsters = Array.AsReadOnly(plan.Monsters.ToArray()) };
            var replacement = new Room(plan);
            prepared = new(this, expected, changeId, target, original.Plan.Room.Map, plan);
            strongwayCandidates[map.Id] = (prepared, replacement);
            reason = "strongway-next-room-prepared";
            return true;
        }
        catch (Exception error) when (error is InvalidDataException or NotSupportedException or OverflowException)
        {
            reason = "strongway-next-room-resource-unsupported";
            return false;
        }
    }

    // 原队调用方必须先取得固定全员命令门/owner，再对全员调用本预验；中间不得await或解锁。
    // 预验不推进epoch、不换房间、不占对象key；已提交的同一原准备对象可幂等重放。
    internal bool CanApplyStrongway2019NextRoom(PreparedStrongway2019RoomReplacement prepared)
    {
        if (!ReferenceEquals(prepared.Run, this)) return false;
        if (committedStrongwayReplacements.TryGetValue(prepared.ChangeId, out var committed))
            return ReferenceEquals(committed, prepared);
        return Matches(prepared.Expected) && Phase == SinglePlayerDungeonPhase.Dungeon && CanAdvance() &&
            !HasPendingDeath && !HasPendingSettlement &&
            strongwayCandidates.TryGetValue(prepared.Plan.Room.Map.Id, out var candidate) && ReferenceEquals(candidate.Prepared, prepared) &&
            connections.Contains((current.Plan.Room.Map.Position, prepared.Target)) &&
            rooms.TryGetValue(prepared.Target, out var original) && !ReferenceEquals(original, current) && !original.Loaded &&
            ReferenceEquals(original.Plan.Room.Map, prepared.OriginalMap) && !entry.IsTerminalMap(prepared.OriginalMap) &&
            entry.Maze.MapSpecification!.Contains(prepared.OriginalMap) && Array.IndexOf(allRooms, original) >= 0 &&
            prepared.Plan.Monsters.Min(monster => (uint)monster.ObjectKey) >= strongwayObjectKeyFloor;
    }

    internal DungeonRunResult ApplyStrongway2019NextRoom(PreparedStrongway2019RoomReplacement prepared)
    {
        if (!CanApplyStrongway2019NextRoom(prepared)) return Result(DungeonRunOutcome.Rejected);
        if (committedStrongwayReplacements.ContainsKey(prepared.ChangeId)) return Result(DungeonRunOutcome.Duplicate);
        var original = rooms[prepared.Target];
        var replacement = strongwayCandidates[prepared.Plan.Room.Map.Id].Room;
        var map = prepared.Plan.Room.Map;
        var nextEntry = entry with
        {
            Maze = ReplaceStrongwayMap(entry.Maze, prepared.Target, map),
            PartyRoute = entry.PartyRoute is { } physical ? physical with { Maze = ReplaceStrongwayMap(physical.Maze, prepared.Target, map) } : null
        };
        allRooms[Array.IndexOf(allRooms, original)] = replacement;
        rooms[prepared.Target] = replacement;
        entry = nextEntry;
        strongwayObjectKeyFloor = prepared.NextObjectKey;
        committedStrongwayReplacements.Add(prepared.ChangeId, prepared);
        strongwayCandidates.Clear();
        Advance();
        return Result(DungeonRunOutcome.Accepted);
    }

    static DungeonMaze ReplaceStrongwayMap(DungeonMaze maze, DungeonPoint target, DungeonMap replacement) => maze with
    {
        MapSpecification = Array.AsReadOnly(maze.MapSpecification!.Select(map => map.Position == target ? replacement : map).ToArray())
    };
}
