using System.Text.RegularExpressions;
using System.Xml.Linq;
using PvfLib;
using static PvfLib.PvfScript;

namespace DfoServer.GameWorld.Towns;

internal sealed record Location(byte Town, byte Area, ushort X, ushort Y, byte Direction = 0);
internal sealed record Rectangle(int X, int Y, int Width, int Height)
{
    public bool Contains(int x, int y) => x >= X && y >= Y && x <= X + Width && y <= Y + Height;
    public bool Door => Width > 0 && Height > 0;
    public bool Marker => Width == 0 && Height == 0;
}

internal sealed record WalkRegion(Rectangle Bounds, int[] Quests, int[] Conditions);
internal sealed record Link(Rectangle Gate, byte Town, byte Area, int[] Quests, int[] Conditions);
internal sealed record TownNpc(uint Id, int X, int Y);
internal sealed record TownMap(string Path, WalkRegion[] Bounds, Link[] Links, bool IsRoom, TownNpc[] Npcs, string? Error = null)
{
    public WalkRegion[] DungeonGates { get; init; } = [];
}
internal sealed record TownPhase(int Index, int Quest);
internal sealed record RoomPortal(byte Town, byte Area, Rectangle Bounds, int Level, int Quest);
internal sealed record TownArea(byte Town, byte Area, int Level, int[] Quests, int[] AnyQuests, int[] ExceptQuests, Location? Gate, TownMap Default, TownMap[] Phases, TownPhase[] Shifts)
{
    public int? DungeonWorldMap { get; init; }
    public TownMap Map(IReadOnlySet<int> completed)
    {
        int phase = -1;
        foreach (var shift in Shifts)
            if (shift.Quest > 0 && completed.Contains(shift.Quest))
                phase = shift.Index;
        return phase >= 0 && phase < Phases.Length ? Phases[phase] : Default;
    }
}

internal sealed class TownCatalog
{
    static readonly IReadOnlySet<int> NoQuests = new HashSet<int>();
    public IReadOnlyDictionary<(byte, byte), TownArea> Areas { get; }
    public Location Room { get; }
    public Location InitialReturn { get; }
    public TownDock Dock { get; }
    public RoomPortal[] RoomPortals { get; }
    public IReadOnlyDictionary<uint, uint[]> NpcMoves { get; }
    public IReadOnlyDictionary<int, TownDungeonMap> DungeonMaps { get; }

    // 只收集完成后开放区域的条件；except quest和进行中任务不能当作解锁任务。
    public IEnumerable<int> AccessQuestIds => Areas.Values.SelectMany(area =>
        area.Quests.Concat(area.AnyQuests).Concat(area.Shifts.Select(shift => shift.Quest))
            .Concat(area.Phases.Prepend(area.Default).SelectMany(map =>
                map.Bounds.Concat(map.DungeonGates).SelectMany(region => region.Quests)
                    .Concat(map.Links.SelectMany(link => link.Quests)))))
        .Concat(RoomPortals.Select(portal => portal.Quest))
        .Concat(Dock.Stops.Select(stop => stop.Quest)).Append(Dock.OpenQuest)
        .Concat(Dock.Shifts.Select(shift => shift.Quest))
        .Concat(DungeonMaps.Values.SelectMany(map => map.Options.Where(option => !option.InProgress).Select(option => option.QuestId)))
        .Where(id => id > 0).Distinct();

    internal TownCatalog(Dictionary<(byte, byte), TownArea> areas, Location room, Location initialReturn, TownDock dock, RoomPortal[] portals, IReadOnlyDictionary<uint, uint[]> npcMoves,
        IReadOnlyDictionary<int,TownDungeonMap>? dungeonMaps = null)
    {
        Areas = areas;
        Room = room;
        InitialReturn = initialReturn;
        Dock = dock;
        RoomPortals = portals;
        NpcMoves = npcMoves;
        DungeonMaps = dungeonMaps ?? new Dictionary<int,TownDungeonMap>();
    }

    static bool Allowed(int[] required, int[] conditions, IReadOnlySet<int> completed) => required.All(completed.Contains) && conditions.Length == 0;
    public bool IsRoom(Location p, IReadOnlySet<int>? completed = null) => Areas.TryGetValue((p.Town, p.Area), out var area) && area.Map(completed ?? NoQuests).IsRoom;
    public bool IsPosition(Location p, IReadOnlySet<int>? completed = null)
    {
        completed ??= NoQuests;
        if (!Areas.TryGetValue((p.Town, p.Area), out var area))
            return false;
        var map = area.Map(completed);
        // Native arrival can be inside the door but outside the walking rectangles.
        return map.Error is null && (map.Bounds.Any(r => Allowed(r.Quests, r.Conditions, completed) && r.Bounds.Contains(p.X, p.Y)) || map.Links.Any(l => l.Gate.Door && Allowed(l.Quests, l.Conditions, completed) && l.Gate.Contains(p.X, p.Y)));
    }

    public Location? SelectionReturn(Location position, IReadOnlySet<int>? completed = null)
    {
        completed ??= NoQuests;
        if (!Areas.TryGetValue((position.Town, position.Area), out var area))
            return position;
        var map = area.Map(completed);
        var gates = map.DungeonGates.Where(g => g.Bounds.Door && Allowed(g.Quests, g.Conditions, completed)).ToArray();
        // 按入口脚本解析边界外8像素的返回点，不沿用边缘站位。
        const int margin = 8;
        if (gates.Length == 0)
            return position;
        Location? result = null;
        long bestDistance = long.MaxValue;
        foreach (var gate in gates.Select(g => g.Bounds))
        {
            long centerX = (long)gate.X + gate.Width / 2, centerY = (long)gate.Y + gate.Height / 2;
            // 上下站位保持不变，只调整横向坐标。
            foreach (long x in new[] { (long)gate.X - margin, (long)gate.X + gate.Width + margin })
            {
                if (x is < 0 or > ushort.MaxValue || map.Error is not null ||
                    !map.Bounds.Any(r => Allowed(r.Quests, r.Conditions, completed) && r.Bounds.Contains((int)x, position.Y)) ||
                    gates.Any(g => g.Bounds.Contains((int)x, position.Y)))
                    continue;
                long dx = x - centerX, dy = position.Y - centerY, distance = dx * dx + dy * dy;
                if (distance >= bestDistance)
                    continue;
                bestDistance = distance;
                result = position with { X = (ushort)x };
            }
        }
        if (result is not null || map.Error is not null)
            return result;
        // 入口边缘的Y可能不在外侧可走矩形内；保留外侧X候选，寻找较近的合法落点。
        foreach (var gate in gates.Select(g => g.Bounds))
        {
            foreach (long x in new[] { (long)gate.X - margin, (long)gate.X + gate.Width + margin })
            {
                if (x is < 0 or > ushort.MaxValue)
                    continue;
                foreach (var region in map.Bounds.Where(r => Allowed(r.Quests, r.Conditions, completed)))
                {
                    long minimumY = Math.Max(0L, region.Bounds.Y), maximumY = Math.Min(ushort.MaxValue, (long)region.Bounds.Y + region.Bounds.Height);
                    if (minimumY > maximumY)
                        continue;
                    var candidates = new[] { Math.Clamp(position.Y, minimumY, maximumY) }.Concat(gates.SelectMany(g =>
                        new[] { (long)g.Bounds.Y - 1, (long)g.Bounds.Y + g.Bounds.Height + 1 }));
                    foreach (long y in candidates)
                    {
                        if (y < minimumY || y > maximumY || !region.Bounds.Contains((int)x, (int)y) ||
                            gates.Any(g => g.Bounds.Contains((int)x, (int)y)))
                            continue;
                        long dx = x - ((long)gate.X + gate.Width / 2), dy = y - position.Y, distance = dx * dx + dy * dy;
                        if (distance >= bestDistance)
                            continue;
                        bestDistance = distance;
                        result = position with { X = (ushort)x, Y = (ushort)y };
                    }
                }
            }
        }
        return result;
    }

    public Location? Entrance(Location from, Location target, IReadOnlySet<int>? completed = null)
    {
        completed ??= NoQuests;
        if (!Areas.TryGetValue((target.Town, target.Area), out var area))
            return null;
        var map = area.Map(completed);
        if (map.Error is not null)
            return null;
        // Dock selection uses portal area's exact landing point, not the reverse zero-size map marker.
        if (Dock.IsRoute(from, target))
            return Dock.Admission(from, target, int.MaxValue, completed) == "ready" && IsPosition(target, completed) ? target : null;
        var links = map.Links.Where(l => Allowed(l.Quests, l.Conditions, completed)).ToArray();
        var gates = links.Where(l => l.Town == from.Town && l.Area == from.Area).Select(l => l.Gate).ToArray();
        // Older regional rooms remain in town.lst; their streets now point to the shared room.
        if (gates.Length == 0 && IsRoom(from, completed))
            gates = links.Where(l => IsRoom(new(l.Town, l.Area, 0, 0), completed)).Select(l => l.Gate).ToArray();
        // Explicit arrival markers take priority. Multiple doors use the nearest fixed center,
        // never the previously saved standing coordinate.
        var markers = gates.Where(g => g.Marker).ToArray();
        var candidates = (markers.Length > 0 ? markers : gates.Where(g => g.Door))
            .Select(g => new { X = g.X + g.Width / 2, Y = g.Y + g.Height / 2 })
            .Where(p => p.X is >= 0 and <= ushort.MaxValue && p.Y is >= 0 and <= ushort.MaxValue)
            .OrderBy(p => (long)(p.X - target.X) * (p.X - target.X) + (long)(p.Y - target.Y) * (p.Y - target.Y))
            .ThenBy(p => p.X)
            .ThenBy(p => p.Y);
        foreach (var p in candidates)
        {
            var arrival = target with
            {
                X = (ushort)p.X,
                Y = (ushort)p.Y
            };
            if (IsPosition(arrival, completed))
                return arrival;
        }

        // A directed one-way edge may name a destination with only a declared spawn gate.
        return gates.Length == 0 && area.Gate is { } gate && IsPosition(gate, completed) ? gate with
        {
            Direction = target.Direction
        }

        : null;
    }

    public Location? DefaultReturn(Location room, IReadOnlySet<int>? completed = null)
    {
        completed ??= NoQuests;
        if (room.Town == Room.Town && room.Area == Room.Area)
            return InitialReturn;
        if (!IsRoom(room, completed))
            return null;
        foreach (var link in Areas[(room.Town, room.Area)].Map(completed).Links.Where(l => l.Gate.Door && Allowed(l.Quests, l.Conditions, completed)))
        {
            var target = new Location(link.Town, link.Area, 0, 0, room.Direction);
            if (!IsRoom(target, completed) && Entrance(room, target, completed) is { } point)
                return point;
        }

        return null;
    }

    // The shared room remembers a district, including while visiting an NPC-only town without a room.
    // Resolve its street gate afresh; neither the login spawn nor the exit uses logout coordinates.
    public Location ResolveRoomReturn(Location position, Location? remembered, int level, IReadOnlySet<int> completed)
    {
        var district = IsRoom(position, completed) ? remembered ?? DefaultReturn(position, completed) : position;
        if (district is not null && Find(district) is { } found)
            return found;
        if (remembered is not null && Find(remembered) is { } saved)
            return saved;
        // Recover old saves in NPC-only towns which predate outdoor district persistence.
        if (Areas.TryGetValue((position.Town, position.Area), out var source))
            foreach (var npc in source.Map(completed).Npcs)
                if (NpcMoves.TryGetValue(npc.Id, out var destinations))
                    foreach (var area in Areas.Values.OrderBy(a => a.Town).ThenBy(a => a.Area))
                        if (area.Map(completed).Npcs.Any(n => destinations.Contains(n.Id)) && Find(new(area.Town, area.Area, 0, 0)) is { } back)
                            return back;
        return InitialReturn;
        Location? Find(Location p)
        {
            foreach (var area in Areas.Values.Where(a => a.Town == p.Town).OrderBy(a => a.Area == p.Area ? 0 : 1).ThenBy(a => a.Area))
            {
                var map = area.Map(completed);
                if (map.IsRoom || map.Error is not null || !map.Links.Any(l => l.Gate.Door && Allowed(l.Quests, l.Conditions, completed) && IsRoom(new(l.Town, l.Area, 0, 0), completed)))
                    continue;
                var point = Entrance(Room, new(area.Town, area.Area, 0, 0), completed);
                if (point is not null && Admission(Room, point, level, completed, point) == "ready")
                    return point;
            }

            return null;
        }
    }

    public string Admission(Location current, Location target, int level, IReadOnlySet<int>? completed = null, Location? roomReturn = null, bool roomPortal = false, uint targetNpc = 0)
    {
        completed ??= NoQuests;
        if (current.Town == target.Town && current.Area == target.Area)
            return "same-area";
        string destination = DestinationAdmission(target, level, completed);
        if (destination != "ready") return destination;
        var area = Areas[(target.Town, target.Area)];
        if (targetNpc != 0)
        {
            if (!Areas.TryGetValue((current.Town, current.Area), out var npcSource) || !npcSource.Map(completed).Npcs.Any(n => NpcMoves.TryGetValue(n.Id, out var to) && to.Contains(targetNpc)))
                return "npc-route-not-in-pvf";
            if (!area.Map(completed).Npcs.Any(n => n.Id == targetNpc))
                return "npc-target-not-in-area";
            return "ready";
        }
        if (roomPortal)
        {
            if (!IsRoom(current, completed))
                return "portal-source-not-room";
            var portal = RoomPortals.SingleOrDefault(p => p.Town == target.Town && p.Area == target.Area);
            if (portal is null)
                return "portal-target-not-in-pvf";
            if (level < portal.Level)
                return "portal-level-required";
            if (portal.Quest > 0 && !completed.Contains(portal.Quest))
                return "portal-quest-required";
            return "ready";
        }

        if (IsRoom(current, completed))
        {
            var saved = roomReturn ?? DefaultReturn(current, completed);
            return saved is not null && target.Town == saved.Town && target.Area == saved.Area ? "ready" : "room-return-mismatch";
        }

        if (!Areas.TryGetValue((current.Town, current.Area), out var source))
            return "source-not-in-pvf";
        if (Dock.IsRoute(current, target))
            return Dock.Admission(current, target, level, completed);
        var links = source.Map(completed).Links.Where(l => l.Gate.Door && l.Town == target.Town && l.Area == target.Area).ToArray();
        if (links.Length == 0)
            return "route-not-in-pvf";
        if (!links.Any(l => l.Quests.All(completed.Contains)))
            return "route-quest-required";
        return links.Any(l => Allowed(l.Quests, l.Conditions, completed)) ? "ready" : "route-state-required";
    }

    // 专用传送复用区域权限；普通旅行继续在Admission里验证来源及有向路线。
    public string DestinationAdmission(Location target, int level, IReadOnlySet<int>? completed = null)
    {
        completed ??= NoQuests;
        if (!Areas.TryGetValue((target.Town, target.Area), out var area)) return "target-not-in-pvf";
        if (area.Map(completed).Error is { } error) return error;
        if (area.Level > level) return "target-level-required";
        if (!area.Quests.All(completed.Contains) || area.AnyQuests.Length > 0 && !area.AnyQuests.Any(completed.Contains))
            return "target-quest-required";
        return area.ExceptQuests.Any(completed.Contains) ? "target-excluded-by-quest" : "ready";
    }

    public bool CanTravel(Location current, Location target, int level, IReadOnlySet<int>? completed = null, Location? roomReturn = null) => Admission(current, target, level, completed, roomReturn) == "ready";
    public static TownCatalog Load(PvfArchive pvf)
    {
        var areas = new Dictionary<(byte, byte), TownArea>();
        var maps = new Dictionary<string, TownMap>();
        var texts = new Dictionary<string, MapText>();
        foreach (var entry in List(pvf.ReadText("town/town.lst")))
        {
            byte town = checked((byte)entry.Key);
            string text = pvf.ReadText("town/" + entry.Value);
            var shifts = Regex.Matches(text, @"\[phase shift\](.*?)\[/phase shift\]", RegexOptions.Singleline).Select(m =>
            {
                var quest = Regex.Match(Section(m.Groups[1].Value, "condition"), @"`\[completed quest\]`\s*(\d+)");
                return new TownPhase((int)Number(m.Groups[1].Value, "phase Index", -1), quest.Success ? int.Parse(quest.Groups[1].Value) : 0);
            }).ToArray();
            foreach (Match m in Regex.Matches(text, @"\[area\]\s*(\d+)\s+`([^`]+)`(.*?)\[/area\]", RegexOptions.Singleline))
            {
                byte id = byte.Parse(m.Groups[1].Value);
                string body = m.Groups[3].Value, permission = Block(body, "permission");
                var gate = Regex.Match(body, @"`\[gate\]`\s+(\d+)\s+(\d+)");
                var phases = Regex.Matches(Block(body, "phase"), "`([^`]+)`").Select(p => LoadMap("map/" + p.Groups[1].Value)).ToArray();
                areas.Add((town, id), new(
                    Town: town,
                    Area: id,
                    Level: (int)Number(permission, "need level", 1),
                    Quests: Integers(Block(permission, "need quest")),
                    AnyQuests: Integers(Block(permission, "need quest or")),
                    ExceptQuests: Integers(Block(permission, "except quest")),
                    Gate: gate.Success ? new(town, id, ushort.Parse(gate.Groups[1].Value), ushort.Parse(gate.Groups[2].Value)) : null,
                    Default: LoadMap("map/" + m.Groups[2].Value),
                    Phases: phases,
                    Shifts: shifts) { DungeonWorldMap = Regex.Match(body,@"`\[dungeon gate\]`\s+(\d+)") is { Success: true } dungeonGate
                        ? int.Parse(dungeonGate.Groups[1].Value) : null });
            }
        }

        var room = areas[(38, 1)].Gate ?? throw new InvalidDataException("Client room gate missing");
        var origin = areas[(38, 0)].Default.Links.Single(l => l.Town == room.Town && l.Area == room.Area && l.Gate.Marker);
        var npcMoves = new Dictionary<uint, uint[]>();
        var npcList = List(pvf.ReadText("npc/npc.lst"));
        foreach (uint id in maps.Values.SelectMany(m => m.Npcs).Select(n => n.Id).Distinct())
        {
            if (!npcList.TryGetValue(checked((int)id), out var npcPath))
                continue;
            string text = pvf.ReadText("npc/" + npcPath);
            var targets = Regex.Matches(text, @"\[role\](.*?)\[/role\]", RegexOptions.Singleline | RegexOptions.IgnoreCase)
                .SelectMany(m => Regex.Matches(m.Groups[1].Value, @"`\[move town\]`\s+(\d+)")
                    .Select(t => uint.Parse(t.Groups[1].Value)))
                .Distinct()
                .ToArray();
            if (targets.Length > 0)
                npcMoves.Add(id, targets);
        }

        var worldPaths = List(pvf.ReadText("worldmap/worldmap.lst"));
        var dungeonMaps = new Dictionary<int,TownDungeonMap>();
        foreach (int id in areas.Values.Select(area => area.DungeonWorldMap).OfType<int>().Distinct())
        {
            string path = worldPaths.TryGetValue(id,out var relative) ? PvfArchive.NormalizePath("worldmap/" + relative) : "";
            try { dungeonMaps.Add(id,TownDungeonMap.Read(id,path,pvf.ReadTokens(path),pvf.ResolveString)); }
            catch (Exception error) when (error is FileNotFoundException or InvalidDataException or NotSupportedException)
            { dungeonMaps.Add(id,new(id,path,[],error.Message)); }
        }
        var catalog = new TownCatalog(areas, room, new(38, 0, checked((ushort)origin.Gate.X), checked((ushort)origin.Gate.Y)), TownDock.Load(pvf), LoadRoomPortals(pvf), npcMoves,dungeonMaps);
        if (!catalog.IsPosition(room) || !catalog.IsPosition(catalog.InitialReturn))
            throw new InvalidDataException("PVF initial position outside native movable regions");
        return catalog;
        TownMap LoadMap(string path)
        {
            path = PvfArchive.NormalizePath(path);
            if (maps.TryGetValue(path, out var cached))
                return cached;
            TownMap map;
            try
            {
                var data = ReadMap(pvf, path, [], texts);
                var own = data.Text;
                var bounds = Rows(own, "virtual movable area", 4).Select(r => new WalkRegion(Rect(r.Values), r.Quests, r.Conditions)).ToArray();
                var movable = Rows(own, "town movable area", 6);
                var links = movable.Where(r => r.Values[4] is >= 0 and <= 255 && r.Values[5] is >= 0 and <= 255).Select(r => new Link(Rect(r.Values), (byte)r.Values[4], (byte)r.Values[5], r.Quests, r.Conditions)).ToList();
                foreach (Match animation in Regex.Matches(Block(own, "town movable area with animation"), @"\[movable area\](.*?)\[/movable area\]", RegexOptions.Singleline))
                {
                    string block = animation.Groups[1].Value;
                    int[] destination = Integers(Section(block, "move town"));
                    if (destination.Length == 0)
                        continue;
                    int[] region = Integers(Section(block, "area"));
                    if (destination.Length != 2 || region.Length != 4)
                        throw new InvalidDataException("Animated town portal shape: " + path);
                    if (destination.All(v => v is >= 0 and <= 255))
                        links.Add(new(Rect(region), (byte)destination[0], (byte)destination[1], [], []));
                }

                var npcs = new List<TownNpc>();
                foreach (Match block in Regex.Matches(own, @"\[NPC\](.*?)\[/NPC\]", RegexOptions.Singleline | RegexOptions.IgnoreCase))
                {
                    string value = Regex.Replace(block.Groups[1].Value, @"`\[(?:left|right)\]`", "0", RegexOptions.IgnoreCase);
                    foreach (var row in Rows("[NPC]" + value + "[/NPC]", "NPC", 5))
                        if (row.Quests.Length == 0 && row.Conditions.Length == 0)
                            npcs.Add(new(checked((uint)row.Values[0]), row.Values[2], row.Values[3]));
                }

                map = new(path, bounds, links.ToArray(), data.IsRoom, npcs.Distinct().ToArray())
                {
                    DungeonGates = movable.Where(r => r.Values[4] == -1 && r.Values[5] == -1)
                        .Select(r => new WalkRegion(Rect(r.Values), r.Quests, r.Conditions)).ToArray()
                };
            }
            catch (FileNotFoundException ex)
            {
                map = new(path, [], [], false, [], "map-resource-missing: " + ex.FileName);
            }

            maps.Add(path, map);
            return map;
        }
    }

    static Rectangle Rect(int[] v)
    {
        if (v[2] < 0 || v[3] < 0)
            throw new InvalidDataException("Negative PVF rectangle size");
        return new(v[0], v[1], v[2], v[3]);
    }

    static RoomPortal[] LoadRoomPortals(PvfArchive pvf)
    {
        var document = XDocument.Parse(pvf.ReadText("ui/warpgate/warpgate.xui").TrimEnd('\0', '\r', '\n'));
        var portals = document.Descendants("CNButton").Where(b => b.Attribute("townIndex") is not null).Select(b =>
        {
            int N(string key)
            {
                string value = (string?)b.Attribute(key) ?? "";
                if (!value.StartsWith("NUM:") || !int.TryParse(value[4..], out int n) || n < 0)
                    throw new InvalidDataException("Room portal attribute: " + key);
                return n;
            }

            return new RoomPortal(checked((byte)N("townIndex")), checked((byte)N("areaIndex")), new(N("townXPos"), N("townYPos"), N("townAreaWidth"), N("townAreaHeight")), N("requireLevel"), N("clearQuest"));
        }).ToArray();
        if (portals.Length == 0 || portals.Any(p => !p.Bounds.Door) || portals.Select(p => (p.Town, p.Area)).Distinct().Count() != portals.Length)
            throw new InvalidDataException("Invalid room portal destinations");
        return portals;
    }

    sealed record MapText(string Text, bool IsRoom);
    static MapText ReadMap(PvfArchive pvf, string path, HashSet<string> chain, Dictionary<string, MapText> cache)
    {
        if (cache.TryGetValue(path, out var cached))
            return cached;
        if (!chain.Add(path))
            throw new InvalidDataException("Recursive map import: " + path);
        string own = pvf.ReadText(path), parent = "";
        bool room = path is "map/common/gate_seria.map" or "map/common/(r)gate_seria.map";
        foreach (Match m in Regex.Matches(Section(own, "import script"), "`([^`]+)`"))
        {
            string imported = PvfArchive.NormalizePath(path[..(path.LastIndexOf('/') + 1)] + m.Groups[1].Value);
            if (!pvf.Contains(imported))
                imported = PvfArchive.NormalizePath("map/" + m.Groups[1].Value);
            if (!pvf.Contains(imported))
            {
                int slash = imported.LastIndexOf('/');
                imported = imported[..(slash + 1)] + "(r)" + imported[(slash + 1)..];
            }

            var data = ReadMap(pvf, imported, chain, cache);
            parent += data.Text + "\n";
            room |= data.IsRoom;
        }

        chain.Remove(path);
        foreach (string tag in new[]
        {
            "virtual movable area",
            "town movable area",
            "town movable area with animation"
        })
            if (Regex.IsMatch(own, @"\[" + tag + @"\]"))
                parent = Regex.Replace(parent, @"\[" + tag + @"\].*?\[/" + tag + @"\]", "", RegexOptions.Singleline);
        var result = new MapText(parent + own, room);
        cache.Add(path, result);
        return result;
    }

    internal sealed record Row(int[] Values, int[] Quests, int[] Conditions);
    internal static Row[] Rows(string text, string tag, int width)
    {
        string block = Block(text, tag);
        var rows = new List<Row>();
        int offset = 0;
        foreach (Match condition in Regex.Matches(block, @"\[(quest|check) condition\](.*?)\[/\1 condition\]", RegexOptions.Singleline))
        {
            Add(block[offset..condition.Index]);
            if (rows.Count == 0)
                throw new InvalidDataException("Condition without geometry");
            int[] values = Integers(condition.Groups[2].Value);
            if (condition.Groups[1].Value == "quest")
                rows[^1] = rows[^1] with
                {
                    Quests = rows[^1].Quests.Concat(values).ToArray()
                };
            else
                rows[^1] = rows[^1] with
                {
                    Conditions = rows[^1].Conditions.Concat(values).ToArray()
                };
            offset = condition.Index + condition.Length;
        }

        Add(block[offset..]);
        return rows.ToArray();
        void Add(string segment)
        {
            if (Regex.Replace(segment, @"-?\d+|\s+", "").Length != 0)
                throw new InvalidDataException("Unknown map geometry: " + tag);
            int[] values = Integers(segment);
            if (values.Length % width != 0)
                throw new InvalidDataException("Map row width");
            for (int i = 0; i < values.Length; i += width)
                rows.Add(new(values[i..(i + width)], [], []));
        }
    }
}
