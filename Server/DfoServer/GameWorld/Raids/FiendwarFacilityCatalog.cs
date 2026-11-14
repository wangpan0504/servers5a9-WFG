using System.Collections.Immutable;
using DfoServer.GameWorld.Dungeons;

namespace DfoServer.GameWorld.Raids;

internal sealed record FiendwarFacilityMaze(int Ordinal, int Boss, int Kind);
internal sealed record FiendwarFacilityDefinition(int Area, int Dungeon, string? RestoreSymbol,
    ImmutableArray<FiendwarFacilityMaze> Mazes);

// Runtime用正式入场目录和共享房间目录预计算；Battle锁内只读取不可变合同。
internal sealed class FiendwarFacilityCatalog
{
    internal RaidDungeonCatalog Entries { get; }
    internal ImmutableDictionary<int, FiendwarFacilityDefinition> Areas { get; }

    internal FiendwarFacilityCatalog(FiendwarScript script, RaidDungeonCatalog entries, DungeonRoomCatalog rooms)
    {
        if (script.Guide || entries.Type != 2 || !entries.Ready || !ReferenceEquals(script.Script, entries.Script))
            throw new InvalidDataException("漩涡设施缺少匹配正式资源合同");
        var kinds = new Dictionary<int, int>
        {
            [107013143] = 5, [107013144] = 5, [107013150] = 6, [107013151] = 7, [107013154] = 8,
            [107013177] = 9, [107013162] = 10, [107013148] = 11, [107013178] = 12,
            [107013163] = 13, [107013164] = 13
        };
        var areas = ImmutableDictionary.CreateBuilder<int, FiendwarFacilityDefinition>();
        foreach (int area in new[] { 15, 17, 19, 21, 23, 27, 28, 29, 30, 31 })
        {
            if (!script.Areas.TryGetValue(area, out var source) || entries.Find(source.DungeonId) is not { } dungeon)
                throw new InvalidDataException("漩涡固定设施地下城未登记");
            var mazes = ImmutableArray.CreateBuilder<FiendwarFacilityMaze>();
            foreach (var maze in dungeon.Mazes.Where(row => row.QuestConnectionTokens.Count == 0))
            {
                var maps = maze.MapSpecification?.Where(map => maze.Bosses.Contains(map.Position)).ToArray() ?? [];
                if (maps.Length != 1 || maps[0].CandidateIds.Count > 1)
                    throw new InvalidDataException("漩涡设施Boss房不是已证固定候选");
                var bosses = rooms.Get(maps[0], requirePassiveDefinitions: false).Monsters
                    .Where(monster => monster.RankKind == "[boss]").Select(monster => monster.MonsterId).Distinct().ToArray();
                if (bosses.Length == 0 || bosses.Any(boss => !kinds.ContainsKey(boss)) ||
                    bosses.Select(boss => kinds[boss]).Distinct().Count() != 1)
                    throw new InvalidDataException("漩涡设施Boss头像未登记");
                mazes.Add(new(maze.SourceOrdinal, bosses[0], kinds[bosses[0]]));
            }
            if (mazes.Count == 0) throw new InvalidDataException("漩涡设施没有合法迷宫");
            string? restore = source.Roles.SingleOrDefault(role => role.Contains("outpost", StringComparison.Ordinal) ||
                role.Contains("command center", StringComparison.Ordinal))?.Replace(" area]", " state]", StringComparison.Ordinal);
            areas.Add(area, new(area, source.DungeonId, restore, mazes.ToImmutable()));
        }
        Entries = entries; Areas = areas.ToImmutable();
    }
}
