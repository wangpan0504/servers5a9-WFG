using System.Collections.Immutable;

namespace DfoServer.GameWorld.Dungeons;

// 只冻结断头谷普通迷宫的真实终点来源，不从客户端资源推导材料数量或概率。
internal sealed class SleepyHollowRewardCatalog
{
    public const int DungeonId = 7322;
    readonly ImmutableDictionary<int, (DungeonMaze Maze, DungeonRoomDefinition Room, int BossOrdinal)> bosses;

    internal SleepyHollowRewardCatalog(IEnumerable<(DungeonMaze Maze, DungeonRoomDefinition Room)> sources)
    {
        bosses = sources.ToImmutableDictionary(row => row.Maze.SourceOrdinal, row =>
        {
            var candidates = row.Room.Monsters.Select((monster, ordinal) => (monster, ordinal))
                .Where(pair => pair.monster.RankKind == "[boss]").ToArray();
            if (candidates.Length != 1 || candidates[0].monster is not { PlacementKind: "[fixed]", Modifiers.Count: 0,
                ScriptedRetirement: false, SpecialParent: null, SpecialChild: null } boss || boss.Team is >= 0 and < 100 ||
                row.Maze.QuestConnectionTokens.Count != 0 || !row.Maze.Bosses.Contains(row.Room.Map.Position))
                throw new InvalidDataException("断头谷终点须有唯一普通敌对领主来源");
            return (row.Maze, row.Room, candidates[0].ordinal);
        });
        if (bosses.Count == 0) throw new InvalidDataException("断头谷缺少普通终点来源");
    }

    public static SleepyHollowRewardCatalog Load(DungeonCatalog dungeons, DungeonRoomCatalog rooms)
    {
        var dungeon = dungeons.Get(DungeonId);
        if (!dungeon.TryGetDesignatedDifficulty(out byte difficulty) || difficulty != 0)
            throw new InvalidDataException("断头谷材料规则仅支持资源固定难度0");
        return new(dungeon.Mazes.Where(maze => maze.QuestConnectionTokens.Count == 0).Select(maze =>
        {
            if (maze.Bosses.Count != 1 || maze.MapSpecification is null)
                throw new InvalidDataException("断头谷普通路线终点声明不完整");
            var map = maze.MapSpecification.Single(row => row.Position == maze.Bosses[0] && row.Kind == "boss");
            return (maze, rooms.Get(map));
        }));
    }

    public bool Matches(DungeonMaze maze, DungeonMap map, int ordinal, DungeonRoomMonster? source) =>
        source is not null && maze.QuestConnectionTokens.Count == 0 && bosses.TryGetValue(maze.SourceOrdinal, out var expected) &&
        expected.Maze.SameLayout(maze) && expected.Room.Map == map && expected.BossOrdinal == ordinal &&
        source.MonsterId == expected.Room.Monsters[ordinal].MonsterId && source.Path == expected.Room.Monsters[ordinal].Path &&
        source.PlacementKind == "[fixed]" && source.RankKind == "[boss]" && source.Modifiers.Count == 0 &&
        source.Team == expected.Room.Monsters[ordinal].Team && !source.ScriptedRetirement &&
        source.SpecialChild is null && source.SpecialParent is null && source.NpcParameter == expected.Room.Monsters[ordinal].NpcParameter &&
        source.RankParameterCount == expected.Room.Monsters[ordinal].RankParameterCount &&
        source.RawValues.SequenceEqual(expected.Room.Monsters[ordinal].RawValues);
}
