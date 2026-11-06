using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Towns;

namespace DfoServer.Game.Dungeons;

// 只登记有正数费用与可达迷宫的资源消费者；角色资格和实际扣费仍由正式入场校验。
internal static class HellPartyTicketConsumers
{
    public static IReadOnlySet<int> From(TownCatalog towns, DungeonCatalog dungeons) =>
        From(towns.DungeonMaps.Values, dungeons.Get,
            (definition, maze) => dungeons.ResolveStaticConsumerMaze(definition, maze, hellParty: true));

    // 隔离测试注入只读定义，不创建或加载额外资源目录。
    internal static IReadOnlySet<int> From(IEnumerable<TownDungeonMap> maps,
        Func<int, DungeonDefinition> get, Func<DungeonDefinition, DungeonMaze, DungeonMaze> resolve)
    {
        var tickets = new HashSet<int>();
        foreach (var map in maps.Where(map => map.Error is null && map.HellDungeon))
        {
            bool connected = false;
            foreach (int id in map.Options.Select(option => option.DungeonId).Distinct())
            {
                try
                {
                    var definition = get(id);
                    if (definition.Id == id && HasPaidEntry(definition, resolve)) { connected = true; break; }
                }
                catch (Exception error) when (error is InvalidDataException or FileNotFoundException or NotSupportedException)
                { /* 无效副本不建立消费者，继续检查同区域的其它选项。 */ }
            }
            if (connected) tickets.UnionWith(map.HellItemConditions.Where(id => id > 0));
        }
        return tickets;
    }

    static bool HasPaidEntry(DungeonDefinition definition,
        Func<DungeonDefinition, DungeonMaze, DungeonMaze> resolve)
    {
        if (definition.Id is >= 5335 and <= 5339) return false; // 沿用实际入口的退役副本限制。
        bool cosmo = definition.RequiresCosmoEntryContract;
        bool inferno = definition.IsInfernoDungeon;
        // 超星空与实际入场共用同一收费及模式合同。
        if (cosmo && !DungeonEntryPlan.SupportsCosmoEntryContract(definition)) return false;
        if (inferno && definition.HellPartyItemCount <= 0) return false;
        if (!Enumerable.Range(0, 5).Any(value => definition.AllowsDifficulty((byte)value, false))) return false;

        foreach (var declared in definition.Mazes.Where(maze => maze.QuestConnectionTokens.Count == 0 && maze.ResourceError is null))
        {
            if (!cosmo && !inferno && declared.HellParty is null) continue;
            DungeonMaze maze;
            try { maze = resolve(definition, declared); }
            catch (Exception error) when (error is InvalidDataException or FileNotFoundException or NotSupportedException)
            { continue; }
            if (maze.ResourceError is not null || maze.SourceOrdinal < 0 || maze.Starts.Count != 1 ||
                maze.Bosses.Count != 1 || maze.MapSpecification is not { Count: > 0 } || !maze.StaticTopology.Supported)
                continue;
            if (!cosmo && !inferno && (maze.HellParty is not { } ordinary ||
                HellPartyEntryCost.RequiredCount(ordinary, definition.MinimumLevel) <= 0)) continue;

            // 深渊替换格沿用实际入场的起点、领主和分层冲突规则。
            if (maze.HellParty is { } room)
            {
                if (string.IsNullOrWhiteSpace(maze.HellPartyPath) ||
                    !maze.MapSpecification.Any(map => map.Kind == "map" && map.Position == room.Room) ||
                    maze.Starts.Contains(room.Room) || maze.Bosses.Contains(room.Room) ||
                    maze.DummyStages.Any(stage => stage.Position == room.Room) ||
                    maze.LayeredMaps.Concat(maze.ConditionalMaps).Concat(maze.StoryStages).Any(map => map.Position == room.Room))
                    continue;
            }
            return true;
        }
        return false;
    }
}
