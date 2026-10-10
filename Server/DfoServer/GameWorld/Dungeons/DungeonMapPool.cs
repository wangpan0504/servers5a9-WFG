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
            .SelectMany(row => (row.MapSpecification ?? []).Concat(row.LayeredMaps))
            .Concat(definition.Mazes.SelectMany(row => row.LayeredMaps))
            .SelectMany(row => row.CandidateIds.Count == 0 ? new[] { row.Id } : row.CandidateIds).ToHashSet();
        var pool = entries.Where(row => row.Dungeons.Contains(definition.Id) && !excluded.Contains(row.Id)).ToArray();
        var start = Choose(maze.Starts); var boss = Choose(maze.Bosses);
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
