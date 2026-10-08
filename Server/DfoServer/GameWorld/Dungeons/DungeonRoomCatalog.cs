using PvfLib;

namespace DfoServer.GameWorld.Dungeons;

// 只保存匹配地图的资源行；整数和标记尚未全部映射到生成规则、战斗状态或 wire 字段。
internal sealed record DungeonRoomMonster(int MonsterId, IReadOnlyList<int> RawValues, string PlacementKind, string RankKind, string Path);
internal sealed record DungeonRoomPassiveObject(int ObjectId, IReadOnlyList<int> RawValues, string Path);
internal sealed record DungeonRoomDefinition(
    DungeonMap Map,
    IReadOnlyList<DungeonRoomMonster> Monsters,
    IReadOnlyList<DungeonRoomPassiveObject> PassiveObjects,
    IReadOnlyList<int> Buffs,
    IReadOnlyList<int>? MonsterTeams = null);

internal sealed class DungeonRoomCatalog
{
    private readonly PvfArchive archive;
    private readonly IReadOnlyDictionary<int, string> monsters;
    private readonly IReadOnlyDictionary<int, string> passiveObjects;

    private DungeonRoomCatalog(PvfArchive archive, IReadOnlyDictionary<int, string> monsters, IReadOnlyDictionary<int, string> passiveObjects)
        => (this.archive, this.monsters, this.passiveObjects) = (archive, monsters, passiveObjects);

    public static DungeonRoomCatalog Load(PvfArchive pvf)
    {

        var monsters = DungeonCatalog.ParseDirectory(pvf.ReadTokens("monster/monster.lst"), pvf.ResolveString, "monster");
        var objects = ParsePassiveDirectory(pvf.ReadTokens("passiveobject/passiveobject.lst"), pvf.ResolveString);
        return new(pvf, monsters, objects);
    }

    internal static IReadOnlyDictionary<int, string> ParsePassiveDirectory(IReadOnlyList<PvfToken> tokens, Func<int, string> resolve)
    {
        if (tokens.Count == 0 || tokens.Count % 2 != 0)
            throw new InvalidDataException("被动对象目录记录不完整");
        var paths = new Dictionary<int, string>();
        for (int i = 0; i < tokens.Count; i += 2)
        {
            if (tokens[i].Type != 0 || tokens[i].Value < 0 || tokens[i + 1].Type != 6)
                throw new InvalidDataException("被动对象目录 ID 或路径类型错误");
            string raw = resolve(tokens[i + 1].Value);
            // 匹配目录保留二十个空路径占位；只有地图实际引用它时才拒绝，不阻断无关地图。
            string path = raw.Length == 0 ? "" : PvfArchive.NormalizePath("passiveobject/" + raw);
            if (path.Length != 0 && (!path.StartsWith("passiveobject/", StringComparison.Ordinal) || path.Length == "passiveobject/".Length))
                throw new InvalidDataException("被动对象路径越过资源根");
            if (!paths.TryAdd(tokens[i].Value, path))
                throw new InvalidDataException("重复被动对象 ID");
        }
        return paths.AsReadOnly();
    }

    public DungeonRoomDefinition Get(DungeonMap map)
    {
        string path = PvfArchive.NormalizePath(map.Path);
        if (map.Id < 0 || !path.StartsWith("map/", StringComparison.Ordinal) || !path.EndsWith(".map", StringComparison.Ordinal)
            || !archive.Contains(path))
            throw new InvalidDataException("房间地图路径或索引无效");
        return ParseResource(map with { Path = path }, archive.ReadTokens(path), archive.ResolveString, monsters, passiveObjects, archive.Contains);
    }

    internal static DungeonRoomDefinition ParseResource(DungeonMap map, IReadOnlyList<PvfToken> tokens, Func<int, string> resolve,
        IReadOnlyDictionary<int, string> monsterPaths, IReadOnlyDictionary<int, string> objectPaths, Func<string, bool> exists)
    {
        var sections = new Dictionary<string, IReadOnlyList<PvfToken>>(StringComparer.Ordinal);
        for (int i = 0; i < tokens.Count;)
        {
            if (tokens[i].Type != 3)
            {
                i++;
                continue;
            }

            string tag = resolve(tokens[i++].Value).ToLowerInvariant();
            if (tag is not ("[monster]" or "[monster team]" or "[passive object]" or "[buff]"))
                continue;
            if (sections.ContainsKey(tag))
                throw new NotSupportedException("unsupported: 房间重复资源块 " + tag);
            int start = i;
            while (i < tokens.Count && tokens[i].Type != 3)
                i++;
            string closing = "[/" + tag[1..];
            if (i >= tokens.Count || resolve(tokens[i].Value).ToLowerInvariant() != closing)
                throw new NotSupportedException("unsupported: 房间资源块未闭合或有嵌套条件 " + tag);
            sections.Add(tag, Freeze(tokens.Skip(start).Take(i - start)));
            i++;
        }

        var monsterRows = new List<DungeonRoomMonster>();
        if (sections.TryGetValue("[monster]", out var monsterTokens))
        {
            if (monsterTokens.Count % 10 != 0)
                throw new NotSupportedException("unsupported: 房间 monster 不是八整数和两字符串的完整行");
            for (int at = 0; at < monsterTokens.Count; at += 10)
            {
                var values = Integers(monsterTokens.Skip(at).Take(8));
                if (monsterTokens[at + 8].Type != 6 || monsterTokens[at + 9].Type != 6)
                    throw new NotSupportedException("unsupported: 房间 monster 标记不是字符串");
                string placement = resolve(monsterTokens[at + 8].Value);
                string rank = resolve(monsterTokens[at + 9].Value);
                if (placement.Length == 0 || rank.Length == 0)
                    throw new NotSupportedException("unsupported: 房间 monster 标记为空");
                string path = Resolve(values[0], monsterPaths, "monster");
                monsterRows.Add(new(values[0], values, placement, rank, path));
            }
        }

        var objectRows = new List<DungeonRoomPassiveObject>();
        if (sections.TryGetValue("[passive object]", out var objectTokens))
        {
            if (objectTokens.Count % 4 != 0)
                throw new NotSupportedException("unsupported: 房间 passive object 不是四整数的完整行");
            for (int at = 0; at < objectTokens.Count; at += 4)
            {
                var values = Integers(objectTokens.Skip(at).Take(4));
                string path = Resolve(values[0], objectPaths, "passive object");
                objectRows.Add(new(values[0], values, path));
            }
        }

        var buffs = sections.TryGetValue("[buff]", out var buffTokens) ? Integers(buffTokens) : Freeze(Array.Empty<int>());
        var teams = sections.TryGetValue("[monster team]", out var teamTokens) ? Integers(teamTokens) : Freeze(Array.Empty<int>());
        if (teams.Count > monsterRows.Count)
            throw new InvalidDataException("房间 monster team 索引超过怪物资源行");
        return new(map, Freeze(monsterRows), Freeze(objectRows), buffs, teams);

        string Resolve(int id, IReadOnlyDictionary<int, string> paths, string kind)
        {
            if (id < 0 || !paths.TryGetValue(id, out string? path) || path.Length == 0 || !exists(path))
                throw new InvalidDataException("房间资源引用不存在: " + kind + " " + id);
            return path;
        }
    }

    private static IReadOnlyList<int> Integers(IEnumerable<PvfToken> source)
    {
        var tokens = source.ToArray();
        if (tokens.Any(token => token.Type != 0))
            throw new NotSupportedException("unsupported: 房间数值包含未解析的 token");
        // 除目录 ID 外不猜测整数的业务范围；原值完整保留供后续证据核对。
        return Freeze(tokens.Select(token => token.Value));
    }

    private static IReadOnlyList<T> Freeze<T>(IEnumerable<T> values) => Array.AsReadOnly(values.ToArray());
}
