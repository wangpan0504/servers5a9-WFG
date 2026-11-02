using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PvfLib;

namespace DfoServer.GameWorld.Maps;

// 同一份world文件供客户端及服务端读取；仅派生目录数据，不改写归档或角色状态。
internal sealed class MapGeometryConfig
{
    private sealed record Atom(byte Kind, int Number, string? Text);
    private sealed record Rule(string Id, string Tag, int Occurrence, Atom[][] From, Atom[] To);
    private readonly Dictionary<string, Rule[]> maps;
    public static MapGeometryConfig Empty { get; } = new([]);
    public IReadOnlyCollection<string> Paths => maps.Keys;
    private MapGeometryConfig(Dictionary<string, Rule[]> maps) => this.maps = maps;

    public static MapGeometryConfig ReadFile(string path)
    {
        if (!File.Exists(path)) return Empty;
        using var source = File.OpenRead(path);
        if (source.Length is <= 0 or > 2 * 1024 * 1024) throw new InvalidDataException("World configuration size");
        using var reader = new StreamReader(source, new UTF8Encoding(false, true), true);
        return Parse(reader.ReadToEnd());
    }

    public static MapGeometryConfig Parse(string text)
    {
        if (Encoding.UTF8.GetByteCount(text) > 2 * 1024 * 1024) throw new InvalidDataException("World configuration size");
        using var doc = JsonDocument.Parse(text.TrimStart('\uFEFF'), new() { MaxDepth = 16 });
        int nodes = 0;
        ValidateJson(doc.RootElement, ref nodes);
        var root = doc.RootElement;
        Keys(root, "schema", "layer", "rules");
        if (root.GetProperty("schema").GetInt32() != 1 || Text(root, "layer") != "world") throw new InvalidDataException("World schema/layer");
        var rows = root.GetProperty("rules");
        if (rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() > 2048) throw new InvalidDataException("World rule budget");
        var result = new Dictionary<string, List<Rule>>(StringComparer.Ordinal);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows.EnumerateArray())
        {
            Keys(row, "id", "enabled", "map", "kind", "tag", "occurrence", "first", "last", "left", "right", "from", "to");
            string id = Text(row, "id");
            if (id.Length == 0 || Encoding.UTF8.GetByteCount(id) > 128 || !ids.Add(id)) throw new InvalidDataException("World duplicate rule identity");
            if (!row.GetProperty("enabled").GetBoolean()) continue;
            string path = Normalize(Text(row, "map")), kind = Text(row, "kind"), tag = Text(row, "tag");
            int occurrence = row.GetProperty("occurrence").GetInt32();
            if (occurrence is < 0 or > 16384 || !row.GetProperty("first").GetBoolean() || !row.GetProperty("last").GetBoolean() ||
                Sequence(row.GetProperty("left")).Length != 0 || Sequence(row.GetProperty("right")).Length != 0)
                throw new InvalidDataException("World selector must guard entire semantic record");
            int stride = (kind, tag) switch
            {
                ("town_gate", "[town movable area]") => 6,
                ("walk_area", "[virtual movable area]") => 4,
                ("npc_position", "[npc]") => 5,
                _ => throw new InvalidDataException("World mechanism has no verified record adapter")
            };
            var from = Profiles(row.GetProperty("from"));
            var to = Profiles(row.GetProperty("to"));
            if (!to[0].SequenceEqual(to[1]) || !to[0].SequenceEqual(to[2])) throw new InvalidDataException("Ratio-dependent world geometry");
            foreach (var before in from)
            {
                if (before.Length == 0 || before.Length != to[0].Length || before.Length % stride != 0) throw new InvalidDataException("World record shape");
                for (int i = 0; i < before.Length; ++i)
                {
                    var a = before[i]; var b = to[0][i];
                    bool facing = kind == "npc_position" && i % stride == 1;
                    if (facing ? a.Kind != 6 || a != b : a.Kind != 0 || b.Kind != 0 || b.Number is < -1048576 or > 1048576)
                        throw new InvalidDataException("World coordinate kind/range or NPC facing");
                    if ((kind == "town_gate" && i % stride >= 4 || kind == "npc_position" && i % stride is 0 or 4) && a != b)
                        throw new InvalidDataException("World target or condition identity");
                }
                if (kind != "npc_position")
                    for (int i = 0; i < before.Length; i += stride)
                        if (before[i + 2].Number <= 0 || before[i + 3].Number <= 0 || to[0][i + 2].Number <= 0 || to[0][i + 3].Number <= 0)
                            throw new InvalidDataException("World rectangle extent");
            }
            if (!result.TryGetValue(path, out var list)) result.Add(path, list = []);
            if (list.Any(r => r.Tag == tag && r.Occurrence == occurrence)) throw new InvalidDataException("Overlapping world selectors");
            list.Add(new(id, tag, occurrence, from, to[0]));
        }
        return new(result.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.Ordinal));
    }

    public PvfToken[] Apply(string path, PvfToken[] input, Func<int, string> resolve)
    {
        if (!maps.TryGetValue(Normalize(path), out var rules)) return input;
        var result = input.ToArray();
        foreach (var rule in rules)
        {
            int begin = -1, end = input.Length, occurrence = 0;
            for (int i = 0; i < input.Length; ++i)
                if (input[i].Type == 3 && resolve(input[i].Value).Equals(rule.Tag, StringComparison.OrdinalIgnoreCase) && occurrence++ == rule.Occurrence)
                { begin = i + 1; break; }
            if (begin < 0) throw new InvalidDataException($"Unmatched world tag: {path} {rule.Id}");
            for (int i = begin; i < input.Length; ++i) if (input[i].Type == 3) { end = i; break; }
            bool Matches(Atom[] atoms) => atoms.Length == end - begin && atoms.Select((a, i) =>
                a.Kind == input[begin + i].Type && (a.Kind == 6 ? a.Text == resolve(input[begin + i].Value) : a.Number == input[begin + i].Value)).All(value => value);
            if (!rule.From.Append(rule.To).Any(Matches)) throw new InvalidDataException($"Unmatched world endpoint: {path} {rule.Id}");
            for (int i = 0; i < rule.To.Length; ++i)
                if (rule.To[i].Kind == 0) result[begin + i] = new(0, rule.To[i].Number);
        }
        return result;
    }

    public PvfToken[] ReadTokens(PvfArchive pvf, string path) => Apply(path, pvf.ReadTokens(path), pvf.ResolveString);

    public string ReadText(PvfArchive pvf, string path)
    {
        string text = pvf.ReadText(path);
        if (!maps.TryGetValue(Normalize(path), out var rules)) return text;
        // 先按无损token验证所有守卫；城镇旧文本解析只替换已核实的完整几何段。
        _ = ReadTokens(pvf, path);
        var writes = new List<(int At, int Length, string Text)>();
        foreach (var rule in rules)
        {
            var matches = Regex.Matches(text, @"(?m)^" + Regex.Escape(rule.Tag) + @"\r?$", RegexOptions.IgnoreCase);
            if (rule.Occurrence >= matches.Count) throw new InvalidDataException("World text/token tag mismatch");
            int begin = matches[rule.Occurrence].Index + matches[rule.Occurrence].Length;
            var next = Regex.Match(text[begin..], @"(?m)^\[");
            int end = next.Success ? begin + next.Index : text.Length;
            string body = "\n" + string.Join(" ", rule.To.Select(a => a.Kind == 6 ? "`" + a.Text + "`" : a.Number.ToString(CultureInfo.InvariantCulture))) + "\n";
            writes.Add((begin, end - begin, body));
        }
        foreach (var write in writes.OrderByDescending(w => w.At)) text = text.Remove(write.At, write.Length).Insert(write.At, write.Text);
        return text;
    }

    private static string Normalize(string path)
    {
        path = path.Replace('\\', '/').ToLowerInvariant();
        if (!path.StartsWith("map/", StringComparison.Ordinal) || Encoding.UTF8.GetByteCount(path) > 480 || path.Contains("..", StringComparison.Ordinal) || path.Contains(':') || path.Contains('\0'))
            throw new InvalidDataException("World resource path");
        return path;
    }
    private static string Text(JsonElement row, string name) => row.GetProperty(name).GetString() ?? throw new InvalidDataException("World string required");
    private static void Keys(JsonElement obj, params string[] names)
    {
        if (obj.ValueKind != JsonValueKind.Object || obj.EnumerateObject().Any(p => !names.Contains(p.Name, StringComparer.Ordinal))) throw new InvalidDataException("Unknown world field");
    }
    private static Atom[][] Profiles(JsonElement obj)
    {
        Keys(obj, "800", "1067", "960");
        return [Sequence(obj.GetProperty("800")), Sequence(obj.GetProperty("1067")), Sequence(obj.GetProperty("960"))];
    }
    private static Atom[] Sequence(JsonElement array)
    {
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > 16384) throw new InvalidDataException("World token budget");
        return array.EnumerateArray().Select(row =>
        {
            if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() != 2) throw new InvalidDataException("World token shape");
            int kind = row[0].GetInt32();
            return kind switch { 0 => new Atom(0, row[1].GetInt32(), null), 6 => new Atom(6, 0, row[1].GetString() ?? throw new InvalidDataException("World token string")), _ => throw new InvalidDataException("World token kind") };
        }).ToArray();
    }
    private static void ValidateJson(JsonElement value, ref int nodes)
    {
        if (++nodes > 65536) throw new InvalidDataException("World JSON node budget");
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate world JSON key");
                ValidateJson(property.Value, ref nodes);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var child in value.EnumerateArray()) ValidateJson(child, ref nodes);
        else if (value.ValueKind == JsonValueKind.String && (value.GetString()!.Contains('\0') || Encoding.UTF8.GetByteCount(value.GetString()!) > 16384)) throw new InvalidDataException("World JSON string budget");
        else if (value.ValueKind == JsonValueKind.Number && !value.TryGetInt64(out _)) throw new InvalidDataException("World JSON integer required");
    }
}
