using System.Text.Json;
using PvfLib;

namespace DfoServer.GameWorld.Maps;

// 仅编译并签名客户端碰撞计划；角色的条件通行仍由匹配客户端原生 switch 执行。
internal sealed record DungeonCollisionPlan(string Map, int SourceColumns, int Rows, int TargetColumns, byte[] Source, byte[] Target)
{
    internal static IReadOnlyDictionary<string, DungeonCollisionPlan> Parse(JsonElement rows, Func<string, string> normalize)
    {
        if (rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() > 40) throw new InvalidDataException("Collision plan budget");
        var plans = new Dictionary<string, DungeonCollisionPlan>(StringComparer.Ordinal);
        foreach (var row in rows.EnumerateArray())
        {
            string[] keys = ["map", "policy", "source_columns", "rows", "target_columns", "source_grid"];
            if (row.ValueKind != JsonValueKind.Object || row.EnumerateObject().Count() != keys.Length ||
                row.EnumerateObject().Any(field => !keys.Contains(field.Name, StringComparer.Ordinal)))
                throw new InvalidDataException("Collision plan keys");
            string raw = row.GetProperty("map").GetString() ?? throw new InvalidDataException("Collision map");
            string path = normalize(raw);
            if (path != raw || !path.EndsWith(".map", StringComparison.Ordinal) || raw.Contains("..", StringComparison.Ordinal) || row.GetProperty("policy").GetInt32() != 1)
                throw new InvalidDataException("Collision plan identity");
            int columns = row.GetProperty("source_columns").GetInt32(), height = row.GetProperty("rows").GetInt32(), target = row.GetProperty("target_columns").GetInt32();
            var cells = row.GetProperty("source_grid");
            if (columns < 1 || columns >= target || target != 70 || height is < 1 or > 4096 || cells.ValueKind != JsonValueKind.Array || cells.GetArrayLength() != columns * height)
                throw new InvalidDataException("Collision plan shape");
            byte[] source = cells.EnumerateArray().Select(value => value.GetByte()).ToArray();
            var expanded = new List<byte>(target * height);
            for (int y = 0; y < height; ++y)
            {
                int start = y * columns, edge = start + columns;
                while (edge != start && source[edge - 1] == 2) --edge;
                if (edge == start) expanded.AddRange(Enumerable.Repeat((byte)2, target));
                else
                {
                    expanded.AddRange(source.AsSpan(start, edge - start).ToArray());
                    expanded.AddRange(Enumerable.Repeat(source[edge - 1], target - columns));
                    expanded.AddRange(source.AsSpan(edge, start + columns - edge).ToArray());
                }
            }
            if (!plans.TryAdd(path, new(path, columns, height, target, source, expanded.ToArray())))
                throw new InvalidDataException("Collision duplicate map");
        }
        return plans;
    }

    internal byte[] Canonical(byte[] dungeonCanonical)
    {
        if (dungeonCanonical.Length < 4 || !dungeonCanonical.AsSpan(0, 4).SequenceEqual("DCG2"u8))
            throw new InvalidDataException("Collision base signature kind");
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        void Data(byte[] value) { writer.Write(value.Length); writer.Write(value); }
        writer.Write("DCG4"u8); Data(dungeonCanonical); writer.Write(1); writer.Write(SourceColumns); writer.Write(Rows); writer.Write(TargetColumns);
        Data(Source); Data(Target); return stream.ToArray();
    }

    internal void VerifySource(IReadOnlyList<PvfToken> map, Func<int, string> resolve,
        Func<string, PvfToken[]> read, Func<string, string, string> reference)
    {
        PvfToken[] Section(IReadOnlyList<PvfToken> tokens, string name)
        {
            var result = new List<PvfToken>(); bool found = false;
            for (int i = 0; i < tokens.Count; ++i)
            {
                if (tokens[i].Type != 3 || !resolve(tokens[i].Value).Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
                if (found) throw new InvalidDataException("Collision duplicate section");
                found = true; for (++i; i < tokens.Count && tokens[i].Type != 3; ++i) result.Add(tokens[i]);
                --i;
            }
            return result.ToArray();
        }
        byte[] Pass(string path)
        {
            var values = Section(read(path), "[pass type]");
            if (values.Length == 0 || values.Length % 14 != 0 || values.Any(value => value.Type != 0 || value.Value is < 0 or > 255))
                throw new InvalidDataException("Collision tile pass shape");
            return values.Select(value => (byte)value.Value).ToArray();
        }
        var bases = Section(map, "[tile]"); var files = Section(map, "[tile files]"); var matrix = Section(map, "[tile map]");
        var width = Section(map, "[force map width]");
        if (width.Length != 1 || width[0].Type != 0 || width[0].Value != TargetColumns * 16)
            throw new InvalidDataException("Collision target world width");
        if (bases.Length == 0 || bases.Length * 14 != SourceColumns || files.Length % 2 != 0 || matrix.Length % bases.Length != 0 ||
            Section(map, "[virtual movable area]").Length != 0 || bases.Any(value => value.Type != 6))
            throw new InvalidDataException("Collision source map shape");
        var tiles = bases.Select(value => Pass(reference(Map, resolve(value.Value)))).ToArray();
        int baseRows = tiles[0].Length / 14;
        if (baseRows + matrix.Length / bases.Length * 10 != Rows) throw new InvalidDataException("Collision source height");
        var extended = new Dictionary<int, byte[]>();
        for (int i = 0; i < files.Length; i += 2)
            if (files[i].Type != 0 || files[i].Value < 0 || files[i + 1].Type != 6 ||
                !extended.TryAdd(files[i].Value, Pass(reference(Map, resolve(files[i + 1].Value)))))
                throw new InvalidDataException("Collision extended definition");
        byte[] actual = Enumerable.Repeat((byte)2, SourceColumns * Rows).ToArray();
        for (int y = 0; y < Rows; ++y) for (int x = 0; x < SourceColumns; ++x)
        {
            byte[]? tile = null; int row = y;
            if (y < baseRows) tile = tiles[x / 14];
            else
            {
                row = (y - baseRows) % 10;
                var id = matrix[(y - baseRows) / 10 * bases.Length + x / 14];
                if (id.Type != 0 || id.Value < -1) throw new InvalidDataException("Collision extended cell");
                if (id.Value != -1 && !extended.TryGetValue(id.Value, out tile)) throw new InvalidDataException("Collision extended id");
            }
            if (tile is not null && row * 14 + x % 14 < tile.Length) actual[y * SourceColumns + x] = tile[row * 14 + x % 14];
        }
        if (!actual.AsSpan().SequenceEqual(Source)) throw new InvalidDataException("Collision source grid mismatch: " + Map);
    }
}
