using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PvfLib;

namespace DfoServer.GameWorld.Maps;

// 同一份world文件供客户端及服务端读取；仅派生目录数据，不改写归档或角色状态。
internal sealed partial class MapGeometryConfig
{
    private sealed record Atom(byte Kind, int Number, string? Text);
    private sealed record Rule(string Id, string Tag, int Occurrence, Atom[][] From, Atom[] To, string Kind);
    private readonly Dictionary<string, Rule[]> maps;
    private readonly Dictionary<string, string> aliases;
    private readonly Dictionary<string, int> stringIds;
    private readonly Dictionary<int, string> strings;
    private IReadOnlyDictionary<string, DungeonCollisionPlan> collisionPlans = new Dictionary<string, DungeonCollisionPlan>(StringComparer.Ordinal);
    internal IReadOnlyDictionary<string, DungeonCollisionPlan> CollisionPlans => collisionPlans;
    public static MapGeometryConfig Empty { get; } = new([],[]);
    public IReadOnlyCollection<string> Paths => maps.Keys.Concat(collisionPlans.Keys).Distinct(StringComparer.Ordinal).ToArray();
    public IReadOnlyDictionary<string, string> Aliases => aliases;
    // 只为原入口的两种加载名建立同源能力，不放宽其他地图的缺失检查。
    internal string? EpicRoadEntrySource(string path)
    {
        const string canonical = "map/common/gate_seria.map", regional = "map/common/(r)gate_seria.map";
        if (path != canonical || !maps.TryGetValue(canonical, out var left) || !maps.TryGetValue(regional, out var right) ||
            left.Length != 1 || right.Length != 1 || left[0].Kind != "epic_road_npc_restore" || right[0].Kind != "epic_road_npc_restore" ||
            !left[0].To.SequenceEqual(right[0].To) || !Enumerable.Range(0, 3).All(i => left[0].From[i].SequenceEqual(right[0].From[i])))
            return null;
        return regional;
    }
    internal IEnumerable<(string Missing, string Empty)> ExtensionRepairs()
    {
        foreach (var row in maps)
            foreach (var rule in row.Value.Where(rule => rule.Kind is "dungeon_ext_tilefiles_repair" or "native_ext_tilefiles"))
            {
                foreach (int slot in rule.Kind == "dungeon_ext_tilefiles_repair" ? [RepairSlot(rule.From[0],rule.To)] : NativeRepairSlots(row.Key,rule,aliases))
                    yield return (ResolveReference(row.Key,rule.From[0][slot].Text!),ResolveReference(row.Key,rule.To[slot].Text!));
            }
    }
    internal static bool EmptyExtendedTile(IReadOnlyList<PvfToken> tokens, Func<int,string> resolve) =>
        tokens.Count >= 144 && (tokens.Count - 4) % 14 == 0 && tokens[0].Type == 3 && resolve(tokens[0].Value).Equals("[image]",StringComparison.OrdinalIgnoreCase) &&
        tokens[1].Type == 6 && resolve(tokens[1].Value).Length == 0 && tokens[2] == new PvfToken(0,0) &&
        tokens[3].Type == 3 && resolve(tokens[3].Value).Equals("[pass type]",StringComparison.OrdinalIgnoreCase) &&
        tokens.Skip(4).All(token => token.Type == 0 && token.Value == 2);
    private static int RepairSlot(Atom[] before, Atom[] after)
    {
        int changed = -1;
        for (int i = 0; i < before.Length; ++i)
            if (before[i] != after[i])
            {
                if (i % 2 != 1 || before[i].Kind != 6 || after[i].Kind != 6 || changed >= 0)
                    throw new InvalidDataException("World empty extension repair identity");
                changed = i;
            }
        return changed >= 0 ? changed : throw new InvalidDataException("World empty extension repair missing replacement");
    }
    public string SignatureKind(string path)
    {
        path = Normalize(path);
        if (path.EndsWith(".til",StringComparison.Ordinal)) return "DCT1";
        if (collisionPlans.ContainsKey(path)) return "DCG4";
        if (maps.TryGetValue(path,out var anchored) && anchored.Any(rule => MapGeometrySignature.AnchorTags.Contains(rule.Tag) || rule.Kind == "native_anchor_restore")) return "DCG3";
        return maps.TryGetValue(path,out var rules) && rules.Any(rule => rule.Kind == "native_geometry_restore" || rule.Tag is "[tile]" or "[tile files]" or "[tile map]" or "[pathgate pos]" or "[pathgate recognize range]" or "[dungeon movable area]") ? "DCG2" : "DCG1";
    }
    private static (int Stride, int X, int Strings) AnchorLayout(string kind) => kind switch
    {
        "dungeon_animation5" or "dungeon_tent_animation5" => (5,2,3),
        "dungeon_passive4" or "dungeon_tent_passive4" => (4,1,0),
        "dungeon_monster10_fixed_normal" => (10,3,(1 << 8) | (1 << 9)),
        "dungeon_ai8" => (8,1,(1 << 4) | (1 << 5)),
        "dungeon_ai10_npc_monster_boss" => (10,1,(1 << 4) | (1 << 5) | (1 << 7)),
        "dungeon_event_xyz3" => (3,0,0),
        "dungeon_special11_single_child" => (11,1,1 << 5),
        _ => default
    };
    private static void ValidateAnchor(string kind, Atom[] before, Atom[] after)
    {
        if (TentKind(kind)) { ValidateTentRecord(kind,before,after); return; }
        var layout = AnchorLayout(kind);
        if (before.Length == 0 || before.Length != after.Length || before.Length % layout.Stride != 0)
            throw new InvalidDataException("World anchor record shape");
        for (int start = 0; start < before.Length; start += layout.Stride)
        {
            for (int n = 0; n < layout.Stride; ++n)
            {
                var source = before[start + n]; var target = after[start + n];
                if (source.Kind != ((layout.Strings & (1 << n)) != 0 ? 6 : 0) || target.Kind != source.Kind)
                    throw new InvalidDataException("World anchor token shape");
                if (n == layout.X)
                {
                    if (source.Number is < -1048576 or > 1048352 || target.Number != source.Number + (source.Number >= 672 ? 224 : 0))
                        throw new InvalidDataException("World anchor insertion mapping");
                }
                else if (source != target) throw new InvalidDataException("World anchor identity changed");
            }
            if (kind == "dungeon_monster10_fixed_normal" && (before[start + 8].Text != "[fixed]" || before[start + 9].Text != "[normal]"))
                throw new InvalidDataException("World anchor monster tail");
            if (kind == "dungeon_special11_single_child" && (before[start + 4].Number != 1 || before[start + 5].Text is not ("[trap]" or "[monster]" or "[item]")))
                throw new InvalidDataException("World anchor special child");
            if (kind == "dungeon_ai10_npc_monster_boss" && (before[start + 4].Text != "[monster]" || before[start + 5].Text != "[NPC]" || before[start + 7].Text != "[boss]"))
                throw new InvalidDataException("World anchor NPC AI tail");
        }
    }

    private static void ValidateEpicRoadNpc(Atom[] before, Atom[] after)
    {
        Atom[] original = [new(0,400001102,null),new(6,0,"[right]"),new(0,535,null),new(0,165,null),new(0,0,null)];
        if (before.Length == 0 || before.Length % 5 != 0 || after.Length != before.Length + 5 ||
            !before.SequenceEqual(after.Take(before.Length)) || !original.SequenceEqual(after.Skip(before.Length)))
            throw new InvalidDataException("World epic NPC restore must preserve prefix and original placement");
        for (int at = 0; at < before.Length; at += 5)
        {
            if (before[at].Kind != 0 || before[at].Number < 0 || before[at].Number == 400001102 ||
                before[at + 1].Kind != 6 || before[at + 1].Text is not ("[left]" or "[right]"))
                throw new InvalidDataException("World epic NPC restore source identity");
            for (int n = 2; n < 5; n++)
                if (before[at + n].Kind != 0 || before[at + n].Number is < -1048576 or > 1048576)
                    throw new InvalidDataException("World epic NPC restore source coordinates");
        }
    }
    private static void ValidateAnimationAppend(Atom[] before, Atom[] after, bool town = false)
    {
        if (before.Length == 0 || before.Length % 5 != 0 || after.Length != before.Length + (town ? 10 : 5) ||
            !before.SequenceEqual(after.Take(before.Length)))
            throw new InvalidDataException("World animation append prefix/shape");
        for (int at = 0; at < after.Length; at += 5)
            for (int n = 0; n < 5; ++n)
                if (after[at + n].Kind != (n < 2 ? 6 : 0) ||
                    n >= 2 && after[at + n].Number is < -1048576 or > 1048576)
                    throw new InvalidDataException("World animation append record kind/range");
        for (int added = before.Length; added < after.Length; added += 5)
        {
            bool source = false;
            for (int at = 0; at < before.Length; at += 5)
            {
                int donorX = before[at + 2].Number;
                int targetX = town ? donorX switch { 771 => 995, 664 => 888, _ => int.MinValue } : donorX == 784 ? 1008 : int.MinValue;
                if (targetX != int.MinValue && after[added + 2].Number == targetX && Enumerable.Range(0,5).All(n =>
                    n == 2 || before[at + n] == after[added + n])) source = true;
            }
            if (!source) throw new InvalidDataException("World animation append source identity");
            for (int at = 0; at < added; at += 5)
                if (after.AsSpan(at,5).SequenceEqual(after.AsSpan(added,5)))
                    throw new InvalidDataException("World animation append duplicate");
        }
    }
    private MapGeometryConfig(Dictionary<string, Rule[]> maps, Dictionary<string, string> aliases)
    {
        this.maps = maps; this.aliases = aliases;
        stringIds = new(StringComparer.Ordinal); strings = [];
        foreach (string value in maps.Values.SelectMany(rows => rows).SelectMany(rule => rule.To)
            .Where(atom => atom.Kind == 6).Select(atom => atom.Text!).Distinct(StringComparer.Ordinal))
        {
            int id = -1 - strings.Count;
            strings.Add(id,value); stringIds.Add(value,id);
        }
    }

    // 配置解析时冻结的虚拟字符串只用于新TIL引用，不修改PVF字符串池。
    public string ResolveString(int id, Func<int, string> originalResolver) => id >= 0 ? originalResolver(id) :
        strings.TryGetValue(id,out string? value) ? value : throw new InvalidDataException("Unknown world virtual string");

    public static MapGeometryConfig ReadFile(string path)
    {
        if (!File.Exists(path)) return Empty;
        using var source = File.OpenRead(path);
        if (source.Length is <= 0 or > 32 * 1024 * 1024) throw new InvalidDataException("World configuration size");
        using var reader = new StreamReader(source, new UTF8Encoding(false, true), true);
        return Parse(reader.ReadToEnd());
    }

    public static MapGeometryConfig Parse(string text)
    {
        if (Encoding.UTF8.GetByteCount(text) > 32 * 1024 * 1024) throw new InvalidDataException("World configuration size");
        using var doc = JsonDocument.Parse(text.TrimStart('\uFEFF'), new() { MaxDepth = 16 });
        int nodes = 0;
        ValidateJson(doc.RootElement, ref nodes);
        var root = doc.RootElement;
        Keys(root, "schema", "layer", "rules", "aliases", "collision_plans");
        if (root.GetProperty("schema").GetInt32() != 1 || Text(root, "layer") != "world") throw new InvalidDataException("World schema/layer");
        var rows = root.GetProperty("rules");
        if (rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() > 32768) throw new InvalidDataException("World rule budget");
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
        if (root.TryGetProperty("aliases",out var aliasRows))
        {
            if (aliasRows.ValueKind != JsonValueKind.Object || aliasRows.EnumerateObject().Count() > 8192)
                throw new InvalidDataException("World alias budget/shape");
            foreach (var alias in aliasRows.EnumerateObject())
            {
                if (alias.Value.ValueKind != JsonValueKind.String) throw new InvalidDataException("World alias source");
                string target = Normalize(alias.Name), source = Normalize(alias.Value.GetString() ?? throw new InvalidDataException("World alias source"));
                if (!target.EndsWith(".til",StringComparison.Ordinal) || !target.Contains("/displaycompat/",StringComparison.Ordinal) ||
                    !source.EndsWith(".til",StringComparison.Ordinal) || source.Contains("/displaycompat/",StringComparison.Ordinal) ||
                    !aliases.TryAdd(target,source)) throw new InvalidDataException("World alias namespace/source");
            }
        }
        var result = new Dictionary<string, List<Rule>>(StringComparer.Ordinal);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows.EnumerateArray())
        {
            if (Text(row,"kind") == "native_geometry_restore")
                Keys(row, "id", "enabled", "map", "kind", "tag", "occurrence", "first", "last", "left", "right", "from", "to", "original_section_present", "section_closed");
            else Keys(row, "id", "enabled", "map", "kind", "tag", "occurrence", "first", "last", "left", "right", "from", "to");
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
                ("epic_road_npc_restore", "[npc]") => 5,
                ("gate_points8", "[pathgate pos]") => 8,
                ("gate_recognize8", "[pathgate recognize range]") => 8,
                ("tile_pass14", "[pass type]") => 14,
                ("dungeon_tiles", "[tile]") => 1,
                ("dungeon_tiles3", "[tile]") => 1,
                ("dungeon_tiles1", "[tile]") => 1,
                ("dungeon_ext_tilefiles", "[tile files]") => 2,
                ("dungeon_ext_tilefiles_repair", "[tile files]") => 2,
                ("dungeon_ext_tilemap", "[tile map]") => 1,
                ("dungeon_ext_tilemap3", "[tile map]") => 1,
                ("dungeon_animation5", "[animation]") => 5,
                ("dungeon_tent_animation5", "[animation]") => 5,
                ("dungeon_animation5_append" or "town_animation5_append", "[animation]") => 5,
                ("dungeon_passive4", "[passive object]") => 4,
                ("dungeon_tent_passive4", "[passive object]") => 4,
                ("dungeon_monster10_fixed_normal", "[monster]") => 10,
                ("dungeon_ai8", "[ai character]") => 8,
                ("dungeon_ai10_npc_monster_boss", "[ai character]") => 10,
                ("dungeon_event_xyz3", "[event monster position]") => 3,
                ("dungeon_special11_single_child", "[special passive object]") => 11,
                ("dungeon_movable_xywh3", "[dungeon movable area]") => 4,
                ("dungeon_virtual_xywh1", "[virtual movable area]") => 4,
                ("dungeon_virtual_xywh4_append", "[virtual movable area]") => 4,
                ("native_tiles", "[tile]") => 1,
                ("native_ext_tilefiles" or "native_ext_tilefiles_clone", "[tile files]") => 2,
                ("native_ext_tilemap", "[tile map]") => 1,
                ("native_geometry_restore", "[force map width]" or "[virtual movable area]") => 1,
                ("native_anchor_restore", "[animation]" or "[passive object]" or "[monster]" or "[ai character]" or "[event monster position]" or "[special passive object]" or "[dungeon movable area]") => 1,
                _ => throw new InvalidDataException("World mechanism has no verified record adapter")
            };
            var from = Profiles(row.GetProperty("from"));
            var to = Profiles(row.GetProperty("to"));
            if (kind == "epic_road_npc_restore" && (occurrence != 0 ||
                path is not ("map/common/gate_seria.map" or "map/common/(r)gate_seria.map")))
                throw new InvalidDataException("World epic NPC restore map/occurrence");
            if (!to[0].SequenceEqual(to[1]) || !to[0].SequenceEqual(to[2])) throw new InvalidDataException("Ratio-dependent world geometry");
            bool gate = kind is "gate_points8" or "gate_recognize8", tile = kind == "tile_pass14";
            bool anchor = AnchorLayout(kind).Stride != 0, metadata = kind is "dungeon_movable_xywh3" or "dungeon_virtual_xywh1" or "dungeon_virtual_xywh4_append";
            bool dungeon = kind is "dungeon_tiles" or "dungeon_tiles3" or "dungeon_tiles1" or "dungeon_ext_tilefiles" or "dungeon_ext_tilefiles_repair" or "dungeon_ext_tilemap" or "dungeon_ext_tilemap3" or "dungeon_animation5_append" or "town_animation5_append";
            bool native = IsNativeRestore(kind);
            if (kind == "native_geometry_restore")
            {
                if (from.Any(values => !values.SequenceEqual(from[0])))
                    throw new InvalidDataException("World native geometry requires common physical source");
                if (row.TryGetProperty("original_section_present",out var present) && present.GetBoolean() != (to[0].Length != 0) ||
                    row.TryGetProperty("section_closed",out var closed) && closed.GetBoolean() != (tag == "[virtual movable area]"))
                    throw new InvalidDataException("World native geometry section contract");
            }
            if ((metadata || anchor || gate || tile || dungeon || native) && occurrence != 0)
                throw new InvalidDataException("World dungeon/grid adapter requires the unique first record");
            if ((metadata || anchor || gate || dungeon || native) && !path.EndsWith(".map",StringComparison.Ordinal) ||
                tile && (!path.EndsWith(".til",StringComparison.Ordinal) || !aliases.ContainsKey(path)))
                throw new InvalidDataException("World mechanism resource kind/alias");
            foreach (var before in from)
            {
                if (native) { ValidateNativeRecord(path,kind,tag,before,to[0]); continue; }
                if (kind == "epic_road_npc_restore") { ValidateEpicRoadNpc(before,to[0]); continue; }
                if (kind is "dungeon_animation5_append" or "town_animation5_append") { ValidateAnimationAppend(before,to[0],kind == "town_animation5_append"); continue; }
                if (anchor) { ValidateAnchor(kind,before,to[0]); continue; }
                if (metadata)
                {
                    bool append = kind == "dungeon_virtual_xywh4_append";
                    int count = append ? 16 : kind == "dungeon_virtual_xywh1" ? 4 : 12;
                    if (before.Length != count || to[0].Length != count || before.Concat(to[0]).Any(atom => atom.Kind != 0 || atom.Number is < -1048576 or > 1048576))
                        throw new InvalidDataException("World dungeon metadata rectangles shape/range");
                    for (int at = 0; at < count; at += 4)
                    {
                        int x = before[at].Number, width = before[at + 2].Number;
                        if (width <= 0 || before[at + 3].Number <= 0 || to[0][at].Number != x + (!append && x >= 672 ? 224 : 0) ||
                            to[0][at + 1] != before[at + 1] || to[0][at + 3] != before[at + 3] ||
                            to[0][at + 2].Number != width + (append ? 448 : x < 672 && (long)x + width > 672 ? 224 : 0))
                            throw new InvalidDataException("World dungeon metadata insertion mapping");
                    }
                    continue;
                }
                if (kind is "dungeon_tiles" or "dungeon_tiles3" or "dungeon_tiles1")
                {
                    int columns = kind == "dungeon_tiles1" ? 1 : kind == "dungeon_tiles3" ? 3 : 4;
                    if (before.Length != columns || to[0].Length != 5 || before.Concat(to[0]).Any(atom => atom.Kind != 6) ||
                        !before.Take(columns - 1).SequenceEqual(to[0].Take(columns - 1)) ||
                        to[0].Skip(columns - 1).Any(atom => !aliases.ContainsKey(ResolveReference(path,atom.Text!))))
                        throw new InvalidDataException("World dungeon tile recipe");
                    if (columns is 1 or 3 && to[0].Skip(columns - 1).Any(atom => aliases[ResolveReference(path,atom.Text!)] != ResolveReference(path,before[columns - 1].Text!)))
                        throw new InvalidDataException("World narrow tile aliases must preserve terminal source");
                    continue;
                }
                if (kind is "dungeon_ext_tilefiles" or "dungeon_ext_tilefiles_repair")
                {
                    bool repair = kind == "dungeon_ext_tilefiles_repair";
                    if (before.Length == 0 || before.Length % 2 != 0 || to[0].Length <= before.Length || to[0].Length % 2 != 0 ||
                        (!repair && !before.SequenceEqual(to[0].Take(before.Length))) || repair && !before.SequenceEqual(from[0])) throw new InvalidDataException("World extended tile file recipe");
                    if (repair) _ = RepairSlot(before,to[0]);
                    _ = TileFileIds(before); _ = TileFileIds(to[0]);
                    for (int i = before.Length; i < to[0].Length; i += 2)
                        if (!aliases.ContainsKey(ResolveReference(path,to[0][i + 1].Text!)))
                            throw new InvalidDataException("World extended tile file alias");
                    continue;
                }
                if (kind is "dungeon_ext_tilemap" or "dungeon_ext_tilemap3")
                {
                    int columns = kind == "dungeon_ext_tilemap3" ? 3 : 4;
                    if (before.Length == 0 || before.Length % columns != 0 || to[0].Length != before.Length / columns * 5 ||
                        before.Concat(to[0]).Any(atom => atom.Kind != 0 || atom.Number < -1))
                        throw new InvalidDataException("World extended tile matrix shape");
                    for (int rowIndex = 0; rowIndex < before.Length / columns; ++rowIndex)
                        if (!before.Skip(rowIndex * columns).Take(columns - 1).SequenceEqual(to[0].Skip(rowIndex * 5).Take(columns - 1)))
                            throw new InvalidDataException("World extended tile matrix prefix");
                    continue;
                }
                if (before.Length == 0 || before.Length != to[0].Length || before.Length % stride != 0) throw new InvalidDataException("World record shape");
                if (gate && before.Length != 8) throw new InvalidDataException("World gate record shape");
                for (int i = 0; i < before.Length; ++i)
                {
                    var a = before[i]; var b = to[0][i];
                    bool facing = kind == "npc_position" && i % stride == 1;
                    if (facing ? a.Kind != 6 || a != b : a.Kind != 0 || b.Kind != 0 || b.Number is < -1048576 or > 1048576)
                        throw new InvalidDataException("World coordinate kind/range or NPC facing");
                    if ((kind == "town_gate" && i % stride >= 4 || kind == "npc_position" && i % stride is 0 or 4) && a != b)
                        throw new InvalidDataException("World target or condition identity");
                    if (gate && (a.Number is < -1048576 or > 1048576) ||
                        tile && (a.Number is < 0 or > 255 || b.Number is < 0 or > 255))
                        throw new InvalidDataException("World gate/grid value range");
                }
                if (kind is "town_gate" or "walk_area")
                    for (int i = 0; i < before.Length; i += stride)
                        if (before[i + 2].Number <= 0 || before[i + 3].Number <= 0 || to[0][i + 2].Number <= 0 || to[0][i + 3].Number <= 0)
                            throw new InvalidDataException("World rectangle extent");
            }
            if (!result.TryGetValue(path, out var list)) result.Add(path, list = []);
            if (list.Any(r => r.Tag == tag && r.Occurrence == occurrence)) throw new InvalidDataException("Overlapping world selectors");
            list.Add(new(id, tag, occurrence, from, to[0], kind));
        }
        if (aliases.Keys.Any(path => !result.TryGetValue(path,out var rules) || !rules.Any(rule => rule.Tag == "[pass type]" && rule.Occurrence == 0)))
            throw new InvalidDataException("World alias requires guarded pass grid");
        foreach (var row in result)
        {
            ValidateTentPlan(row.Value);
            ValidateExtendedRecipe(row.Key,row.Value,aliases);
            ValidateVisibleExtensionClone(row.Key,row.Value,aliases,result);
            if (row.Value.Any(rule => rule.Kind is "dungeon_animation5_append" or "dungeon_virtual_xywh1") && row.Value.Count(rule => rule.Kind == "dungeon_tiles") != 1)
                throw new InvalidDataException("World animation append requires the same map four-to-five tile root");
            if (row.Value.Any(rule => rule.Kind == "town_animation5_append") &&
                (row.Key != "map/town/metrocenter/metro_time_waiting.map" || row.Value.Count(rule => rule.Kind == "town_gate") != 1 || row.Value.Count(rule => rule.Kind == "walk_area") != 1))
                throw new InvalidDataException("World town animation requires metro town geometry");
            if (row.Value.Any(rule => rule.Kind == "dungeon_virtual_xywh4_append") && row.Value.Count(rule => rule.Kind == "dungeon_tiles3") != 1)
                throw new InvalidDataException("World four virtual rectangles require the same map three-to-five tile root");
        }
        foreach (var row in result)
            foreach (var repair in row.Value.Where(rule => rule.Kind is "dungeon_ext_tilefiles_repair" or "native_ext_tilefiles"))
            {
                foreach (int slot in repair.Kind == "dungeon_ext_tilefiles_repair" ? [RepairSlot(repair.From[0],repair.To)] : NativeRepairSlots(row.Key,repair,aliases))
                {
                    string target = ResolveReference(row.Key,repair.To[slot].Text!);
                    if (!aliases.ContainsKey(target) || !result.TryGetValue(target,out var guarded) || guarded.Count != 1)
                        throw new InvalidDataException("World repaired extension alias missing");
                    var pass = guarded[0];
                    if (pass.Kind != "tile_pass14" || pass.To.Length < 140 || pass.From.Any(before => !before.SequenceEqual(pass.To) || before.Any(atom => atom.Kind != 0 || atom.Number != 2)))
                        throw new InvalidDataException("World repaired extension must preserve all-blocked source");
                }
            }
        var config = new MapGeometryConfig(result.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.Ordinal),aliases);
        if (root.TryGetProperty("collision_plans", out var collisionRows)) config.collisionPlans = DungeonCollisionPlan.Parse(collisionRows, Normalize);
        return config;
    }

    public PvfToken[] Apply(string path, PvfToken[] input, Func<int, string> resolve)
    {
        if (!maps.TryGetValue(Normalize(path), out var rules)) return input;
        string Resolve(int id) => ResolveString(id,resolve);
        var writes = new List<(int At, int Length, PvfToken[] Tokens)>();
        foreach (var rule in rules)
        {
            if (rule.Kind == "native_geometry_restore")
            {
                string close = "[/" + rule.Tag[1..];
                bool closed = rule.Tag == "[virtual movable area]";
                int opens = 0, closes = 0, start = -1;
                for (int i = 0; i < input.Length; ++i)
                    if (input[i].Type == 3)
                    {
                        string tag = Resolve(input[i].Value);
                        if (tag.Equals(rule.Tag,StringComparison.OrdinalIgnoreCase)) { ++opens; start = i; }
                        if (tag.Equals(close,StringComparison.OrdinalIgnoreCase)) ++closes;
                    }
                // 空目标表示整个新增段不存在，孤立闭标签不能当作已恢复成功。
                if (opens == 0)
                {
                    if (rule.To.Length == 0 && closes == 0) continue;
                    throw new InvalidDataException($"World native geometry tag missing: {path} {rule.Id}");
                }
                if (opens != 1 || closes != (closed ? 1 : 0))
                    throw new InvalidDataException("World native geometry section duplicated or unbalanced");
                int stop = start + 1;
                while (stop < input.Length && input[stop].Type != 3) ++stop;
                if (closed && (stop == input.Length || !Resolve(input[stop].Value).Equals(close,StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException("World native geometry closure mismatch");
                bool Match(Atom[] values) => values.Length == stop - start - 1 &&
                    values.Select((atom,index) => input[start + 1 + index].Type == atom.Kind && input[start + 1 + index].Value == atom.Number).All(value => value);
                if (rule.To.Length != 0 && Match(rule.To)) continue;
                if (!Match(rule.From[0])) throw new InvalidDataException($"Unmatched native geometry endpoint: {path} {rule.Id}");
                var nativeReplacement = rule.To.Select(atom => new PvfToken(0,atom.Number)).ToArray();
                writes.Add(rule.To.Length == 0 ? (start,stop - start + (closed ? 1 : 0),nativeReplacement) : (start + 1,stop - start - 1,nativeReplacement));
                continue;
            }
            if ((rule.Tag is "[pathgate pos]" or "[pathgate recognize range]" or "[pass type]" or "[tile]" or "[tile files]" or "[tile map]") &&
                input.Count(token => token.Type == 3 && Resolve(token.Value).Equals(rule.Tag,StringComparison.OrdinalIgnoreCase)) != 1)
                throw new InvalidDataException("World dungeon/grid tag missing or duplicated");
            int begin = -1, end = input.Length, occurrence = 0;
            for (int i = 0; i < input.Length; ++i)
                if (input[i].Type == 3 && Resolve(input[i].Value).Equals(rule.Tag, StringComparison.OrdinalIgnoreCase) && occurrence++ == rule.Occurrence)
                { begin = i + 1; break; }
            if (begin < 0) throw new InvalidDataException($"Unmatched world tag: {path} {rule.Id}");
            for (int i = begin; i < input.Length; ++i) if (input[i].Type == 3) { end = i; break; }
            if (MapGeometrySignature.AnchorTags.Contains(rule.Tag) || rule.Kind is "dungeon_movable_xywh3" or "dungeon_virtual_xywh1" or "dungeon_virtual_xywh4_append" or "epic_road_npc_restore" || rule.Kind == "native_anchor_restore" && rule.Tag == "[dungeon movable area]")
            {
                string close = "[/" + rule.Tag[1..];
                if (end == input.Length || Resolve(input[end].Value).ToLowerInvariant() != close ||
                    input.Count(token => token.Type == 3 && Resolve(token.Value).Equals(rule.Tag,StringComparison.OrdinalIgnoreCase)) != 1 ||
                    input.Count(token => token.Type == 3 && Resolve(token.Value).Equals(close,StringComparison.OrdinalIgnoreCase)) != 1)
                    throw new InvalidDataException("World anchor closure missing or duplicated");
            }
            if (rule.Tag is "[tile files]" or "[tile map]")
            {
                bool extended = false;
                for (int i = 0; i < begin - 1; ++i)
                    if (input[i].Type == 3)
                    {
                        string tag = Resolve(input[i].Value);
                        if (tag.Equals("[extended tile]",StringComparison.OrdinalIgnoreCase))
                        { if (extended) throw new InvalidDataException("Nested extended tile block"); extended = true; }
                        else if (tag.Equals("[/extended tile]",StringComparison.OrdinalIgnoreCase)) extended = false;
                    }
                bool closed = false;
                for (int i = end; i < input.Length; ++i)
                    if (input[i].Type == 3)
                    {
                        string tag = Resolve(input[i].Value);
                        if (tag.Equals("[extended tile]",StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("Nested extended tile block");
                        if (tag.Equals("[/extended tile]",StringComparison.OrdinalIgnoreCase)) { closed = true; break; }
                    }
                if (!extended || !closed)
                    throw new InvalidDataException("World extended tile container missing closure");
            }
            bool Matches(Atom[] atoms) => atoms.Length == end - begin && atoms.Select((a, i) =>
                a.Kind == input[begin + i].Type && (a.Kind == 6 ? a.Text == Resolve(input[begin + i].Value) : a.Number == input[begin + i].Value)).All(value => value);
            if (!rule.From.Append(rule.To).Any(Matches)) throw new InvalidDataException($"Unmatched world endpoint: {path} {rule.Id}");
            var replacement = new PvfToken[rule.To.Length];
            for (int i = 0; i < rule.To.Length; ++i)
            {
                var atom = rule.To[i];
                replacement[i] = atom.Kind == 0 ? new(0,atom.Number) :
                    i < end - begin && input[begin + i].Type == 6 && Resolve(input[begin + i].Value) == atom.Text ? input[begin + i] :
                    new(6,stringIds[atom.Text!]);
            }
            writes.Add((begin,end - begin,replacement));
        }
        var result = input.ToList();
        foreach (var write in writes.OrderByDescending(write => write.At))
        {
            result.RemoveRange(write.At,write.Length); result.InsertRange(write.At,write.Tokens);
        }
        return result.ToArray();
    }

    private string SourcePath(PvfArchive pvf, string path)
    {
        path = Normalize(path);
        if (!aliases.TryGetValue(path,out string? source)) return path;
        if (pvf.Contains(path) || !pvf.Contains(source)) throw new InvalidDataException("World alias shadows source or source missing");
        return source;
    }

    public PvfToken[] ReadTokens(PvfArchive pvf, string path) => Apply(path, pvf.ReadTokens(SourcePath(pvf,path)), pvf.ResolveString);

    public string ReadText(PvfArchive pvf, string path)
    {
        string text = pvf.ReadText(SourcePath(pvf,path));
        if (!maps.ContainsKey(Normalize(path))) return text;
        return ApplyText(path,text,pvf.ReadTokens(SourcePath(pvf,path)),pvf.ResolveString);
    }

    internal string ApplyText(string path, string text, PvfToken[] input, Func<int,string> resolve)
    {
        if (!maps.TryGetValue(Normalize(path), out var rules)) return text;
        // 先按无损token验证所有守卫；城镇旧文本解析只替换已核实的完整几何段。
        _ = Apply(path,input,resolve);
        var writes = new List<(int At, int Length, string Text)>();
        foreach (var rule in rules)
        {
            var matches = Regex.Matches(text, @"(?m)^" + Regex.Escape(rule.Tag) + @"\r?$", RegexOptions.IgnoreCase);
            if (rule.Kind == "native_geometry_restore" && rule.To.Length == 0)
            {
                if (matches.Count == 0) continue;
                if (matches.Count != 1) throw new InvalidDataException("World native text section duplicated");
                int start = matches[0].Index, bodyStart = start + matches[0].Length;
                var following = Regex.Match(text[bodyStart..], @"(?m)^\[");
                int stop = following.Success ? bodyStart + following.Index : text.Length;
                if (rule.Tag == "[virtual movable area]")
                {
                    var closing = Regex.Match(text[stop..], @"^" + Regex.Escape("[/" + rule.Tag[1..]) + @"\r?(?:\n|$)",RegexOptions.IgnoreCase);
                    if (!closing.Success) throw new InvalidDataException("World native text closure mismatch");
                    stop += closing.Length;
                }
                writes.Add((start,stop - start,""));
                continue;
            }
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
        if (!path.StartsWith("map/", StringComparison.Ordinal) || Encoding.UTF8.GetByteCount(path) > 480 || path.Split('/').Contains("..", StringComparer.Ordinal) || path.Contains(':') || path.Contains('\0'))
            throw new InvalidDataException("World resource path");
        return path;
    }

    private static string ResolveReference(string path, string reference)
    {
        if (reference.IndexOfAny([':', '\0']) >= 0 || reference.StartsWith('/') || reference.StartsWith('\\'))
            throw new InvalidDataException("World tile reference");
        reference = reference.Replace('\\','/').ToLowerInvariant();
        // 原生加载器直接拼接当前MAP目录；不能把map/前缀误当成绝对资源引用。
        if (reference.StartsWith("map/",StringComparison.Ordinal)) throw new InvalidDataException("World tile reference must be map-relative");
        var parts = path[..(path.LastIndexOf('/') + 1)].Split('/',StringSplitOptions.RemoveEmptyEntries).ToList();
        foreach (string part in reference.Split('/',StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            if (part == "..") { if (parts.Count <= 1) throw new InvalidDataException("World tile reference escapes map root"); parts.RemoveAt(parts.Count - 1); }
            else parts.Add(part);
        }
        return Normalize(string.Join('/',parts));
    }

    private static HashSet<int> TileFileIds(Atom[] atoms)
    {
        var ids = new HashSet<int>();
        for (int i = 0; i < atoms.Length; i += 2)
            if (atoms[i].Kind != 0 || atoms[i].Number < 0 || atoms[i + 1].Kind != 6 || !ids.Add(atoms[i].Number))
                throw new InvalidDataException("World extended tile file identity");
        return ids;
    }

    private static void ValidateExtendedRecipe(string path, List<Rule> rules, Dictionary<string,string> aliases)
    {
        if (rules.Any(rule => rule.Kind is "native_ext_tilefiles" or "native_ext_tilefiles_clone" or "native_ext_tilemap"))
        {
            ValidateNativeExtendedRecipe(rules);
            return;
        }
        var fileRules = rules.Where(rule => rule.Tag == "[tile files]").ToArray();
        var mapRules = rules.Where(rule => rule.Tag == "[tile map]").ToArray();
        if (fileRules.Length == 0 && mapRules.Length == 0) return;
        if (fileRules.Length != 1 || mapRules.Length != 1 || fileRules[0].Occurrence != 0 || mapRules[0].Occurrence != 0 ||
            rules.Count(rule => rule.Tag == "[tile]" && rule.Occurrence == 0) != 1)
            throw new InvalidDataException("World extended tile recipe must bind the complete dungeon tile plan");
        var files = fileRules[0]; var matrix = mapRules[0];
        bool three = matrix.Kind == "dungeon_ext_tilemap3";
        if (rules.Single(rule => rule.Tag == "[tile]").Kind != (three ? "dungeon_tiles3" : "dungeon_tiles"))
            throw new InvalidDataException("World extended tile matrix must match base column adapter");
        int columns = three ? 3 : 4;
        bool Valid(Atom[] values, HashSet<int> ids) => values.All(atom => atom.Number == -1 || ids.Contains(atom.Number));
        if (!Valid(matrix.To,TileFileIds(files.To))) throw new InvalidDataException("World extended tile target id missing");
        for (int profile = 0; profile < 3; ++profile)
        {
            var originalIds = TileFileIds(files.From[profile]);
            if (!Valid(matrix.From[profile],originalIds)) throw new InvalidDataException("World extended tile source id missing");
            for (int row = 0; row < matrix.From[profile].Length / columns; ++row)
            {
                int before = matrix.From[profile][row * columns + columns - 1].Number;
                for (int column = columns - 1; column < 5; ++column)
                {
                    int target = matrix.To[row * 5 + column].Number;
                    if (before == -1 ? target != -1 : target < 0 || originalIds.Contains(target))
                        throw new InvalidDataException("World extended tile split must bind new aliases or preserve empty cell");
                    if (three && before != -1)
                    {
                        int sourceSlot = Array.FindIndex(files.From[profile],atom => atom.Kind == 0 && atom.Number == before);
                        int targetSlot = Array.FindIndex(files.To,atom => atom.Kind == 0 && atom.Number == target);
                        string alias = ResolveReference(path,files.To[targetSlot + 1].Text!);
                        if (!aliases.TryGetValue(alias,out string? source) || source != ResolveReference(path,files.From[profile][sourceSlot + 1].Text!))
                            throw new InvalidDataException("World three-column extension aliases must preserve terminal source");
                    }
                }
            }
        }
    }
    private static string Text(JsonElement row, string name) => row.GetProperty(name).GetString() ?? throw new InvalidDataException("World string required");
    private static void Keys(JsonElement obj, params string[] names)
    {
        if (obj.ValueKind != JsonValueKind.Object || obj.EnumerateObject().Any(p => !names.Contains(p.Name, StringComparer.Ordinal))) throw new InvalidDataException("Unknown world field");
    }
    private static Atom[][] Profiles(JsonElement obj)
    {
        if (obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty("all", out var common))
        {
            Keys(obj, "all");
            var values = Sequence(common);
            return [values, values, values];
        }
        Keys(obj, "800", "1067", "960");
        if (!obj.TryGetProperty("800", out var standard) || !obj.TryGetProperty("1067", out var wide) || !obj.TryGetProperty("960", out var compact))
            throw new InvalidDataException("World profile endpoints require all or three explicit profiles");
        return [Sequence(standard), Sequence(wide), Sequence(compact)];
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
        if (++nodes > 32 * 1024 * 1024) throw new InvalidDataException("World JSON node budget");
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
