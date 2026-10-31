using System.Collections.Immutable;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Raids;

namespace DfoServer.Game.Raids;

// 生命周期归原共享RaidDungeonRun。只登记潜在出现，不代表销毁、更不写入攻坚符号。
// 同Run回房/四人重复加载共用同一集合；epoch不进入出现key，不能借换房刷新一次性身份。
internal sealed class LukeRaidObjectRegistration
{
    readonly ImmutableDictionary<LukeObjectRoom, ImmutableArray<LukeObjectBinding>> rooms;
    readonly HashSet<LukeObjectRoom> observed = [];
    public Guid Run { get; }
    public int Dungeon { get; }
    public int MazeOrdinal { get; }
    public int ObservedRooms => observed.Count;
    public int ObservedObjects => observed.Sum(room => rooms[room].Length);
    public LukeObjectBinding? Energy { get; }

    public LukeRaidObjectRegistration(Guid run, int dungeon, DungeonMaze maze, LukeRaidObjectCatalog catalog)
    {
        if (run == Guid.Empty) throw new ArgumentException("对象登记缺少原Run身份", nameof(run));
        Run = run; Dungeon = dungeon; MazeOrdinal = maze.SourceOrdinal;
        Energy = catalog.Energy(dungeon, maze);
        var builder = ImmutableDictionary.CreateBuilder<LukeObjectRoom, ImmutableArray<LukeObjectBinding>>();
        if (maze.MapSpecification is { Count: <= 64 } maps)
            foreach (var map in maps)
            {
                if (map.IsConditional || map.LayerIndex >= 0 || map.StoryStageIndex >= 0 || map.DummyStageIndex >= 0 ||
                    map.CandidateIds.Count > 1) continue;
                var key = Key(map);
                if (catalog.Find(key) is { } rows && !builder.ContainsKey(key)) builder.Add(key, rows);
            }
        rooms = builder.ToImmutable();
    }

    LukeObjectRoom Key(DungeonMap map) => new(Dungeon, MazeOrdinal, map.Kind, map.Position, map.Id, map.Path);
    // 仅由Battle在原Run全员加载事实通过后调用；返回null与已审但无对象的空数组不同。
    public ImmutableArray<LukeObjectBinding>? Observe(DungeonMap map)
    {
        if (map.IsConditional || map.LayerIndex >= 0 || map.StoryStageIndex >= 0 || map.DummyStageIndex >= 0 || map.CandidateIds.Count > 1)
            return null;
        var key = Key(map);
        if (!rooms.TryGetValue(key, out var rows)) return null;
        observed.Add(key);
        return rows;
    }
}
