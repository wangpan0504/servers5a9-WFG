using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Dungeons;

internal sealed record DungeonMapPoolEntry(int Id, string Path, ImmutableArray<int> Dungeons, string Kind, ushort Layouts);

// 原始MAP的归属、type及greed决定候选，不以相似文件名猜测缺失剧情地图。
internal sealed class DungeonMapPool(ImmutableArray<DungeonMapPoolEntry> entries)
{
    public static DungeonMapPool Load(PvfArchive archive, IReadOnlyDictionary<int,string> paths)
    {
        var rows = ImmutableArray.CreateBuilder<DungeonMapPoolEntry>();
        foreach (var pair in paths.OrderBy(row => row.Key))
        {
            try
            {
                var row = Parse(pair.Key,pair.Value,archive.ReadTokens(pair.Value),archive.ResolveString);
                if (row is not null) rows.Add(row);
            }
            catch (Exception error) when (error is InvalidDataException or FileNotFoundException or NotSupportedException) { }
        }
        return new(rows.ToImmutable());
    }

    internal static DungeonMapPoolEntry? Parse(int id, string path, IReadOnlyList<PvfToken> tokens, Func<int,string> resolve)
    {
        var fields = new Dictionary<string,IReadOnlyList<PvfToken>>(StringComparer.Ordinal);
        for (int at = 0; at < tokens.Count; at++)
        {
            if (tokens[at].Type != 3) continue;
            string name = resolve(tokens[at].Value);
            if (name is not ("[dungeon]" or "[type]" or "[greed]")) continue;
            if (!fields.TryAdd(name,tokens.Skip(at+1).TakeWhile(token => token.Type != 3).ToArray())) return null;
        }
        if (!fields.TryGetValue("[dungeon]",out var owned) || owned.Count == 0 || owned.Any(token => token.Type != 0 || token.Value <= 0) ||
            !fields.TryGetValue("[type]",out var kind) || kind is not [{ Type: 6 }] ||
            !fields.TryGetValue("[greed]",out var greed) || greed is not [{ Type: 6 }]) return null;
        string type = resolve(kind[0].Value);
        if (type is not ("[normal]" or "[boss]")) return null;
        string raw = resolve(greed[0].Value);
        int count = raw.Count(ch => ch is not (' ' or '\r' or '\n')) / 2;
        var grid = DungeonGreedGrid.Read(count,1,raw);
        if (!grid.Supported || grid.Cells!.Count % 2 != 0) return null;
        ushort layouts = 0;
        for (int at = 0; at < grid.Cells.Count; at += 2)
        {
            // Full03 04274282..042742E2按两组greed值存pair；先启用两个值一致的方向，异值消费者另行确认。
            if (grid.Cells[at].Index == grid.Cells[at+1].Index) layouts |= checked((ushort)(1 << grid.Cells[at].Index));
        }
        return layouts == 0 ? null : new(id,path,owned.Select(token => token.Value).Distinct().ToImmutableArray(),type,layouts);
    }

    public DungeonMaze Resolve(DungeonDefinition definition, DungeonMaze maze, Func<int,int> draw)
    {
        if (maze.QuestConnectionTokens.Count != 0 || maze.ResourceError is not null || !maze.GreedGrid.Supported ||
            maze.Starts.Count == 0 || maze.Bosses.Count == 0) return maze;
        var maps = (maze.MapSpecification ?? []).ToDictionary(row => row.Position);
        var excluded = definition.Mazes.Where(row => row.QuestConnectionTokens.Count != 0)
            .SelectMany(row => (row.MapSpecification ?? []).Concat(row.LayeredMaps).Concat(row.OverriddenMaps))
            .Concat(definition.Mazes.SelectMany(row => row.LayeredMaps))
            .SelectMany(row => row.CandidateIds.Count == 0 ? new[] { row.Id } : row.CandidateIds).ToHashSet();
        var pool = entries.Where(row => row.Dungeons.Contains(definition.Id) && !excluded.Contains(row.Id)).ToArray();
        var start = Choose(maze.Starts); var boss = Choose(maze.Bosses);
        // 悲叹之塔的原始BB是单房声明，不把右出口补成网格外房间。
        if (definition.RequiresGriefEntryContract)
        {
            if (!definition.IsGriefTower || maze.Width != 1 || maze.Height != 1 ||
                maze.Starts.Count != 1 || maze.Bosses.Count != 1 || start != new DungeonPoint(0,0) || boss != start ||
                maze.MapSpecification is not null || maze.GreedGrid.Cells is not [{ Index: 1 }] ||
                maze.LayeredMaps.Count != 0 || maze.WarpMaps.Count != 0 || maze.ConditionalMaps.Count != 0 ||
                maze.StoryStages.Count != 0 || maze.BossEntrance is not null || maze.HellParty is not null) return maze;
            var grief = pool.Where(row => row.Kind == "[normal]" && (row.Layouts & 2) != 0).ToArray();
            if (grief is not [{ Id: 100247, Layouts: 2, Dungeons: [5115] } candidate] ||
                !candidate.Path.Equals("map/towerofgrief_down/100247_001.map",StringComparison.OrdinalIgnoreCase)) return maze;
            return maze with { IsGriefTower = true, MapSpecification = [new("boss",start,candidate.Id,candidate.Path)] };
        }
        // 本版职业体验是无出口的真实单房间；只接受已核验的资源归属、类型、坐标与AA，不放宽普通空格。
        if (definition.IsClassExperience && maze.Width == 1 && maze.Height == 1 && maze.Starts.Count == 1 && maze.Bosses.Count == 1 &&
            start == new DungeonPoint(0,0) && boss == start && maps.Count == 0 && maze.GreedGrid.Cells is [{ Index: 0 }])
        {
            var experience = pool.Where(row => row.Kind == "[normal]" && (row.Layouts & 1) != 0 &&
                row.Path.EndsWith("/76032_changeclass_s_(0,0).map", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (experience.Length != 1) return maze;
            return maze with { IsClassExperience = true, MapSpecification = [new("boss",start,experience[0].Id,experience[0].Path)] };
        }
        // 本版修炼场起点与终点重合，使用实际归属及坐标命名的start资源。
        if (definition.Id == 5000 && maze.Width == 1 && maze.Height == 1 && maze.Starts.Count == 1 && maze.Bosses.Count == 1 &&
            start == new DungeonPoint(0,0) && boss == start && maps.Count == 0 && maze.GreedGrid.Cells is [{ Index: 1 }])
        {
            var training = pool.Where(row => row.Kind == "[normal]" && (row.Layouts & 2) != 0 &&
                row.Path.EndsWith("/11100(0,0)start.map",StringComparison.OrdinalIgnoreCase)).ToArray();
            if (training.Length != 1) return maze;
            return maze with { IsTrainingRoom = true, MapSpecification = [new("boss",start,training[0].Id,training[0].Path)] };
        }
        // 实验性（#259，依据来自 Go 项目反汇编，未经 V31 客户端验证）：比赛副本是 1x1 单图，起点与 Boss 同为 (0,0)，
        // 地图取 PVF 地图反向 [dungeon] 声明的唯一 [boss] 型比赛图（BB BB，布局位 1）。
        if (definition.IsTournament && maze.Width == 1 && maze.Height == 1 && maze.Starts.Count == 1 && maze.Bosses.Count == 1 &&
            start == new DungeonPoint(0,0) && boss == start && maps.Count == 0 && maze.GreedGrid.Cells is [{ Index: 1 }])
        {
            var tournament = pool.Where(row => row.Kind == "[boss]" && (row.Layouts & 2) != 0 &&
                row.Path.StartsWith("map/tournament/",StringComparison.OrdinalIgnoreCase)).ToArray();
            if (tournament.Length != 1) return maze;
            return maze with { IsTournament = true, MapSpecification = [new("boss",start,tournament[0].Id,tournament[0].Path)] };
        }
        if (maps.Values.Any(row => row.Kind == "boss" && row.Position != boss)) return maze;
        foreach (var cell in maze.GreedGrid.Cells!)
        {
            if (cell.Index == 0) continue;
            var point = new DungeonPoint(cell.SourceOrdinal % maze.Width,cell.SourceOrdinal / maze.Width);
            if (maps.ContainsKey(point)) continue;
            string kind = point == boss ? "[boss]" : "[normal]";
            var candidates = pool.Where(row => row.Kind == kind && (row.Layouts & (1 << cell.Index)) != 0).ToArray();
            if (candidates.Length == 0) return maze;
            var selected = Choose(candidates);
            maps.Add(point,new(point == boss ? "boss" : "map",point,selected.Id,selected.Path));
        }
        var result = maze with { Starts = [start], Bosses = [boss], MapSpecification = maps.Values.OrderBy(row => row.Position.Y).ThenBy(row => row.Position.X).ToImmutableArray() };
        return result.StaticTopology.Supported ? result : maze;

        T Choose<T>(IReadOnlyList<T> rows)
        {
            int index = rows.Count == 1 ? 0 : draw(rows.Count);
            if (index < 0 || index >= rows.Count) throw new InvalidDataException("普通地图随机索引越界");
            return rows[index];
        }
    }
}
