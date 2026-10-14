using PvfLib;

namespace DfoServer.GameWorld.Dungeons;

// 只保存匹配地图的资源行；整数和标记尚未全部映射到生成规则、战斗状态或 wire 字段。
internal sealed record DungeonRoomMonster(int MonsterId, IReadOnlyList<int> RawValues, string PlacementKind, string RankKind, string Path)
{
    public int? Team { get; init; }
    public bool ScriptedRetirement { get; init; }
    // 冠军/命名品级的附加数量独立于八整数；当前只恢复空附加集合，不截断未知正数量。
    public IReadOnlyList<string> Modifiers { get; init; } = Array.Empty<string>();
    public int? RankParameterCount { get; init; }
    public DungeonRoomSpecialObject? SpecialParent { get; init; }
    public DungeonRoomSpecialChild? SpecialChild { get; init; }
}
internal sealed record DungeonRoomPassiveObject(int ObjectId, IReadOnlyList<int> RawValues, string? Path)
{
    public string? UnresolvedReason => Path is null ? "passive-object-unlisted" : null;
}
internal sealed record DungeonRoomSpecialChild(int SourceOrdinal, string Kind, IReadOnlyList<int> RawValues, string? Path);
internal sealed record DungeonRoomSpecialObject(int SourceOrdinal, int ObjectId, IReadOnlyList<int> RawValues,
    string? Path, IReadOnlyList<DungeonRoomSpecialChild> Children);
internal sealed record DungeonRoomAiCharacter(int Id, IReadOnlyList<int> RawValues, string Faction, string RankKind, string Path, int Level);
internal sealed record DungeonRoomNpc(int Id, string Direction, int X, int Y, int Z);
internal sealed record DungeonRoomDefinition(
    DungeonMap Map,
    IReadOnlyList<DungeonRoomMonster> Monsters,
    IReadOnlyList<DungeonRoomPassiveObject> PassiveObjects,
    IReadOnlyList<int> Buffs,
    IReadOnlyList<int>? MonsterTeams = null)
{
    // MAP自身的单值[type]，与迷宫位置的map/boss/layered声明分别保存；缺失或歧义不授予终点资格。
    public string? DeclaredType { get; init; }
    public IReadOnlyList<DungeonRoomSpecialObject> SpecialObjects { get; init; } = Array.Empty<DungeonRoomSpecialObject>();
    public IReadOnlyList<DungeonRoomAiCharacter> AiCharacters { get; init; } = Array.Empty<DungeonRoomAiCharacter>();
    public IReadOnlyList<int> EventMonsterPositions { get; init; } = Array.Empty<int>();
    public IReadOnlyList<DungeonRoomNpc> Npcs { get; init; } = [];
    public string? NpcMetadataError { get; init; }
    public IReadOnlyList<DfoServer.GameWorld.Characters.TutorialCinematicEvidence> TutorialCinematics { get; init; } = [];
}

internal sealed class DungeonRoomCatalog
{
    private readonly PvfArchive archive;
    private readonly IReadOnlyDictionary<int, string> monsters;
    private readonly IReadOnlyDictionary<int, string> passiveObjects;
    private readonly IReadOnlyDictionary<int, string> aiCharacters;

    private DungeonRoomCatalog(PvfArchive archive, IReadOnlyDictionary<int, string> monsters, IReadOnlyDictionary<int, string> passiveObjects,
        IReadOnlyDictionary<int, string> aiCharacters)
        => (this.archive, this.monsters, this.passiveObjects, this.aiCharacters) = (archive, monsters, passiveObjects, aiCharacters);

    public static DungeonRoomCatalog Load(PvfArchive pvf)
    {

        var monsters = DungeonCatalog.ParseDirectory(pvf.ReadTokens("monster/monster.lst"), pvf.ResolveString, "monster");
        var objects = ParsePassiveDirectory(pvf.ReadTokens("passiveobject/passiveobject.lst"), pvf.ResolveString);
        var actors = DungeonCatalog.ParseDirectory(pvf.ReadTokens("aicharacter/aicharacter.lst"),pvf.ResolveString,"aicharacter");
        return new(pvf, monsters, objects, actors);
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

    public DungeonRoomDefinition Get(DungeonMap map, bool requirePassiveDefinitions = true, bool allowUnresolvedSpecialMetadata = false)
    {
        string path = PvfArchive.NormalizePath(map.Path);
        if (map.Id < 0 || !path.StartsWith("map/", StringComparison.Ordinal) || !path.EndsWith(".map", StringComparison.Ordinal)
            || !archive.Contains(path))
            throw new InvalidDataException("房间地图路径或索引无效");
        return ParseResource(map with { Path = path }, archive.ReadTokens(path), archive.ResolveString, monsters, passiveObjects, archive.Contains,
            requirePassiveDefinitions,ResolveAiCharacter,allowUnresolvedSpecialMetadata);
    }

    DungeonRoomAiCharacter ResolveAiCharacter(PvfAiCharacterSpawn row)
    {
        int id = row.Values[0];
        if (!aiCharacters.TryGetValue(id,out var path) || !archive.Contains(path))
            throw new InvalidDataException("房间APC定义不存在: " + id);
        var minimum = PvfAiCharacterScript.MinimumInfo(archive.ReadTokens(path),archive.ResolveString);
        // 匹配AIC：名字后四项角色字段，再是等级；不以角色或副本等级替代APC自身声明。
        if (minimum.Length < 6 || minimum[0].Type != 6 || minimum.Skip(1).Take(5).Any(token => token.Type != 0) ||
            minimum[5].Value is < 1 or > 255) throw new InvalidDataException("房间APC最低信息或等级无效: " + id);
        return new(id,row.Values,row.Faction,row.Rank,path,minimum[5].Value);
    }

    public bool ReportsQuestDeath(DungeonRoomPassiveObject source)
    {
        if (!passiveObjects.TryGetValue(source.ObjectId,out var path) || path != source.Path || path.Length == 0 || !archive.Contains(path)) return false;
        var tokens = archive.ReadTokens(path);
        for (int at = 0; at + 1 < tokens.Length; at++)
            if (tokens[at].Type == 3 && archive.ResolveString(tokens[at].Value) == "[quest type]")
                return tokens[at + 1].Type == 6 && archive.ResolveString(tokens[at + 1].Value) == "[must be destroyed]";
        return false;
    }

    internal static DungeonRoomDefinition ParseResource(DungeonMap map, IReadOnlyList<PvfToken> tokens, Func<int, string> resolve,
        IReadOnlyDictionary<int, string> monsterPaths, IReadOnlyDictionary<int, string> objectPaths, Func<string, bool> exists,
        bool requirePassiveDefinitions = true, Func<PvfAiCharacterSpawn,DungeonRoomAiCharacter>? resolveAiCharacter = null,
        bool allowUnresolvedSpecialMetadata = false)
    {
        var sections = new Dictionary<string, IReadOnlyList<PvfToken>>(StringComparer.Ordinal);
        string? declaredType = null;
        bool typeSeen = false;
        for (int i = 0; i < tokens.Count;)
        {
            if (tokens[i].Type != 3)
            {
                i++;
                continue;
            }

            string tag = resolve(tokens[i++].Value).ToLowerInvariant();
            if (tag == "[type]")
            {
                declaredType = !typeSeen && i < tokens.Count && tokens[i].Type == 6 &&
                    (i + 1 == tokens.Count || tokens[i + 1].Type == 3) ? resolve(tokens[i].Value) : null;
                typeSeen = true;
                continue;
            }
            if (tag is not ("[monster]" or "[monster team]" or "[passive object]" or "[buff]" or "[ai character]" or "[special passive object]" or "[event monster position]"))
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
            for (int at = 0; at < monsterTokens.Count;)
            {
                if (monsterTokens.Count - at < 10)
                    throw new NotSupportedException("unsupported: 房间 monster 缺少八整数和完整字符串标记");
                var values = Integers(monsterTokens.Skip(at).Take(8));
                at += 8;
                var labels = new List<string>();
                while (at < monsterTokens.Count && monsterTokens[at].Type == 6)
                    labels.Add(resolve(monsterTokens[at++].Value));
                if (labels.Count < 2)
                    throw new NotSupportedException("unsupported: 房间 monster 标记不是字符串");
                if (labels.Any(label => label.Length == 0))
                    throw new NotSupportedException("unsupported: 房间 monster 标记为空");
                if (labels[^1] is "[champion]" or "[super champion]")
                {
                    // Full03 0425D1F9/0425D23E在品级后读附加效果数量；0不带效果，不能当下一怪物ID。
                    if (at >= monsterTokens.Count || monsterTokens[at] != new PvfToken(0,0))
                        throw new NotSupportedException("unsupported: 精英怪物的非空附加效果尚未解析");
                    at++;
                }
                string path = Resolve(values[0], monsterPaths, "monster");
                monsterRows.Add(new(values[0], values, labels[0], labels[^1], path)
                {
                    // 76155真实行含[fixed] [dummy] [boss]；保留中间标记，生成层逐项确认后启用。
                    Modifiers = Freeze(labels.Skip(1).Take(labels.Count - 2)),
                    RankParameterCount = labels[^1] is "[champion]" or "[super champion]" ? 0 : null
                });
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
                if (values[0] < 0) throw new InvalidDataException("被动物件ID不能为负数");
                // 装饰物由客户端地图装载；服务端只为任务权威事件验证脚本，不修补无关旧装饰引用。
                string? path = requirePassiveDefinitions ? Resolve(values[0], objectPaths, "passive object")
                    : objectPaths.GetValueOrDefault(values[0]);
                objectRows.Add(new(values[0], values, path));
            }
        }

        var buffs = sections.TryGetValue("[buff]", out var buffTokens) ? Integers(buffTokens) : Freeze(Array.Empty<int>());
        var specialObjects = new List<DungeonRoomSpecialObject>();
        if (sections.TryGetValue("[special passive object]", out var specialTokens))
        {
            foreach (var objectRow in MapSpecialObjects.Read(specialTokens, resolve))
            {
                var raw = objectRow.Values;
                // 仅训练保留已从LST移除的历史hellparty声明，不生成新演员或奖励。
                string? path = allowUnresolvedSpecialMetadata && objectRow.Children.All(child => child.Kind == "[hellparty]")
                    ? objectPaths.TryGetValue(raw[0], out var listed) && listed.Length != 0 && exists(listed) ? listed : null
                    : Resolve(raw[0], objectPaths, "special passive object");
                var children = new List<DungeonRoomSpecialChild>();
                for (int index = 0; index < objectRow.Children.Length; index++)
                {
                    string kind = objectRow.Children[index].Kind;
                    var values = objectRow.Children[index].Values;
                    string? childPath = kind == "[monster]" ? Resolve(values[0], monsterPaths, "special monster") : null;
                    // 未解释的机关/奖励原整数仍完整保留；解析不授予生成、掉落或任务资格。
                    children.Add(new(index, kind, values, childPath));
                }
                specialObjects.Add(new(specialObjects.Count, raw[0], raw, path, Freeze(children)));
            }
        }
        var teams = sections.TryGetValue("[monster team]", out var teamTokens) ? Integers(teamTokens) : Freeze(Array.Empty<int>());
        if (teams.Count > monsterRows.Count)
            throw new InvalidDataException("房间 monster team 索引超过怪物资源行");
        for (int index = 0; index < teams.Count; index++)
            monsterRows[index] = monsterRows[index] with { Team = teams[index] };
        var actors = new List<DungeonRoomAiCharacter>();
        if (sections.TryGetValue("[ai character]",out var actorTokens))
            foreach (var row in PvfAiCharacterScript.MapRows(actorTokens,resolve))
                actors.Add(resolveAiCharacter?.Invoke(row) ?? throw new InvalidDataException("房间APC定义解析依赖缺失"));
        var eventPositions = sections.TryGetValue("[event monster position]", out var eventTokens) ? Integers(eventTokens) : Freeze(Array.Empty<int>());
        if (eventPositions.Count % 3 != 0)
            throw new InvalidDataException("事件怪物位置不是完整的三整数坐标");
        IReadOnlyList<DungeonRoomNpc> npcs = [];
        string? npcError = null;
        try { npcs = Freeze(MapNpcs.Read(tokens, resolve).Select(row => new DungeonRoomNpc(row.Id,row.Direction,row.X,row.Y,row.Z))); }
        catch (InvalidDataException error) { npcError = error.Message; }
        // 未解释的 NPC 元数据不收窄原入场/战斗能力，但不能作为任务会面的真实来源。
        return new(map, Freeze(monsterRows), Freeze(objectRows), buffs, teams)
            { AiCharacters = Freeze(actors), SpecialObjects = Freeze(specialObjects), EventMonsterPositions = eventPositions,
                Npcs = npcs, NpcMetadataError = npcError, DeclaredType = declaredType };

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
