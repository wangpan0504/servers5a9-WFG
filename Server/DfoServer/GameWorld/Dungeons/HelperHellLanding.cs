using PvfLib;
using DfoServer.GameWorld.Maps;

namespace DfoServer.GameWorld.Dungeons;

internal readonly record struct DungeonLanding(int X, int Y);

// 只为助手轮换读取柱子附近的静态通行格；条件地形仍交匹配客户端原生加载器校正。
internal static class HelperHellLanding
{
    internal static DungeonLanding? Read(DungeonRoomDefinition room, IReadOnlyList<PvfToken> map,
        Func<int, string> resolve, Func<string, PvfToken[]> read, DungeonCollisionPlan? collision)
    {
        try { return ReadCore(room, map, resolve, read, collision); }
        // 可选落点元数据不支持时保留既有入场；不把解析失败伪装成安全坐标。
        catch (Exception error) when (error is InvalidDataException or NotSupportedException or ArgumentException) { return null; }
    }

    static DungeonLanding? ReadCore(DungeonRoomDefinition room, IReadOnlyList<PvfToken> map,
        Func<int, string> resolve, Func<string, PvfToken[]> read, DungeonCollisionPlan? collision)
    {
        var pillars = room.SpecialObjects.Where(row => row.Children.Any(child => child.Kind == "[hellparty]")).ToArray();
        if (pillars is not [var pillar] || pillar.RawValues is not [_, >= 0 and <= 1048576, >= 0 and <= 1048576, 0, ..]) return null;
        var bases = Section(map, "[tile]");
        var files = Section(map, "[tile files]");
        var matrix = Section(map, "[tile map]");
        var width = Section(map, "[force map width]");
        var virtualArea = Section(map, "[virtual movable area]");
        if (bases.Length is < 1 or > 256 || bases.Any(row => row.Type != 6) ||
            files.Length % 2 != 0 || matrix.Length % bases.Length != 0 || matrix.Length > 65536) return null;
        int columns = bases.Length * 14;
        if (virtualArea.Length > 0)
        {
            // 已证原生逐条OR的脚底点矩形；只接完整静态整数段，不解释条件嵌套。
            if (virtualArea.Length % 4 != 0 || virtualArea.Length > 4096 ||
                virtualArea.Any(row => row.Type != 0 || row.Value is < -1048576 or > 1048576) ||
                map.Count(row => row.Type == 3 && resolve(row.Value).Equals("[/virtual movable area]", StringComparison.OrdinalIgnoreCase)) != 1 ||
                width.Length != 0 && width is not [{ Type: 0, Value: > 0 and <= 1048576 }]) return null;
            var rectangles = virtualArea.Chunk(4).Select(row => row.Select(value => value.Value).ToArray()).ToArray();
            if (rectangles.Any(row => row[2] <= 0 || row[3] <= 0)) return null;
            int worldWidth = width.Length == 0 ? columns * 16 : width[0].Value;
            int worldHeight = rectangles.Max(row => row[1] + row[3]);
            columns = worldWidth / 16; int virtualRows = worldHeight / 16;
            if (columns < 1 || virtualRows < 1 || (long)columns * virtualRows > 1048576) return null;
            byte[] virtualGrid = Enumerable.Repeat((byte)2, columns * virtualRows).ToArray();
            for (int y = 0; y < virtualRows; y++) for (int x = 0; x < columns; x++)
                if (rectangles.Any(row => x * 16 >= row[0] && y * 16 >= row[1] &&
                    x * 16 + 15 < row[0] + row[2] && y * 16 + 15 < row[1] + row[3]))
                    virtualGrid[y * columns + x] = 0;
            return Pick(pillar.RawValues[1], pillar.RawValues[2], columns, virtualRows, virtualGrid);
        }
        if (width.Length != 0 && (width is not [{ Type: 0, Value: > 0 }] ||
            width[0].Value != (collision?.TargetColumns ?? columns) * 16)) return null;
        byte[]? Pass(string path)
        {
            var values = Section(read(path), "[pass type]");
            return values.Length is > 0 and <= 65536 && values.Length % 14 == 0 &&
                values.All(row => row.Type == 0 && row.Value is >= 0 and <= 255)
                ? values.Select(row => (byte)row.Value).ToArray() : null;
        }
        string Reference(string name) => MapGeometryCapabilities.TileReference(room.Map.Path, name);
        var names = bases.Select(row => resolve(row.Value)).ToArray();
        // 原生配置的空tile槽是阻挡占位，不能当作缺失资源或可通行地板。
        var tiles = names.Select(name => name.Length == 0 ? null : Pass(Reference(name))).ToArray();
        if (tiles.Where((row, index) => names[index].Length != 0).Any(row => row is null) || tiles[0] is null) return null;
        int baseRows = tiles[0]!.Length / 14;
        int rows = baseRows + matrix.Length / bases.Length * 10;
        if ((long)columns * rows > 1048576) return null;
        var extended = new Dictionary<int, byte[]>();
        for (int i = 0; i < files.Length; i += 2)
        {
            if (files[i].Type != 0 || files[i].Value < 0 || files[i + 1].Type != 6) return null;
            string name = resolve(files[i + 1].Value);
            var tile = name.Length == 0 ? Enumerable.Repeat((byte)2, 140).ToArray() : Pass(Reference(name));
            if (tile is not { Length: >= 140 } ||
                !extended.TryAdd(files[i].Value, tile)) return null;
        }
        byte[] grid = Enumerable.Repeat((byte)2, columns * rows).ToArray();
        for (int y = 0; y < rows; y++) for (int x = 0; x < columns; x++)
        {
            byte[]? tile = tiles[x / 14]; int localY = y;
            if (y >= baseRows)
            {
                localY = (y - baseRows) % 10;
                var id = matrix[(y - baseRows) / 10 * bases.Length + x / 14];
                if (id.Type != 0 || id.Value < -1 || id.Value != -1 && !extended.ContainsKey(id.Value)) return null;
                tile = id.Value == -1 ? null : extended[id.Value];
            }
            int index = localY * 14 + x % 14;
            if (tile is not null && index < tile.Length) grid[y * columns + x] = tile[index];
        }
        if (collision is not null)
        {
            if (columns != collision.SourceColumns || rows != collision.Rows || !grid.AsSpan().SequenceEqual(collision.Source)) return null;
            columns = collision.TargetColumns; grid = collision.Target;
        }
        return Pick(pillar.RawValues[1], pillar.RawValues[2], columns, rows, grid);

        PvfToken[] Section(IReadOnlyList<PvfToken> tokens, string name)
        {
            PvfToken[] result = []; bool found = false;
            for (int i = 0; i < tokens.Count; i++)
                if (tokens[i].Type == 3 && resolve(tokens[i].Value).Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    if (found) return [new(255, 0)];
                    found = true; int start = ++i;
                    while (i < tokens.Count && tokens[i].Type != 3) i++;
                    result = tokens.Skip(start).Take(i - start).ToArray(); i--;
                }
            return result;
        }
    }

    internal static DungeonLanding? Pick(int pillarX, int pillarY, int columns, int rows, byte[] grid)
    {
        if (columns < 1 || rows < 1 || (long)columns * rows != grid.Length) return null;
        // 优先同水平线左右，再逐圈扩展；保留周围九格，避免贴墙或落在柱子脚下。
        for (int distance = 96; distance <= 192; distance += 16)
            foreach (var offset in new[] { (-distance, 0), (distance, 0), (0, distance), (0, -distance) })
            {
                int x = (pillarX + offset.Item1) / 16, y = (pillarY + offset.Item2) / 16;
                if (pillarX + offset.Item1 < 0 || pillarY + offset.Item2 < 0 || x < 1 || y < 1 || x >= columns - 1 || y >= rows - 1) continue;
                bool safe = true;
                for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++)
                    safe &= grid[(y + dy) * columns + x + dx] == 0;
                if (safe) return new(x * 16 + 8, y * 16 + 8);
            }
        return null;
    }
}
