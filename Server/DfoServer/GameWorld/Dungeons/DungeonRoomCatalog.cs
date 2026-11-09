using PvfLib;

namespace DfoServer.GameWorld.Dungeons;

// 只保存匹配地图的资源行；整数和标记尚未全部映射到生成规则、战斗状态或 wire 字段。
internal sealed record DungeonRoomMonster(int MonsterId, IReadOnlyList<int> RawValues, string PlacementKind, string? RankKind, string Path)
{
    public int? Team { get; init; }
    public bool ScriptedRetirement { get; init; }
    // MOB禁止强化或命名演员声明只限制随机提升，不覆盖MAP显式品级。
    public bool ChampionRestricted { get; init; }
    public bool IsNamed { get; init; }
    // monster 内 [NPC] 的原整数；缺省与显式0/-1不同，不将它猜为NPC编号或生成资格。
    public int? NpcParameter { get; init; }
    // 冠军/命名品级的附加数量独立于八整数；当前只恢复空附加集合，不截断未知正数量。
    public IReadOnlyList<string> Modifiers { get; init; } = Array.Empty<string>();
    // 屏外显示控制演员保留原Boss行；仅接纳已确认的单标记固定声明。
    public bool IsDisplayHuntDummy => NpcParameter is null && PlacementKind == "[fixed]" && RankKind == "[boss]" &&
        Modifiers is ["[displayhuntdummy]"];
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
    string? Path, IReadOnlyList<DungeonRoomSpecialChild> Children)
{
    // 本版9618含未登记、无子规则的精确空行；保留原序号供后续真实父子关联。
    public bool IsEmptyPlaceholder => Path is null && ObjectId == 0 &&
        RawValues is [0, 1, -1, -1, 0] && Children.Count == 0;
}
internal sealed record DungeonRoomAiCharacter(int Id, IReadOnlyList<int> RawValues, string Faction, string RankKind, string Path, int Level);

internal enum TournamentEnemyKind { Monster, AiCharacter }
internal sealed record TournamentEnemy(int PartyCount, int Code, int Strength)
{
    public DungeonRoomAiCharacter? AiCharacter { get; init; }
}
internal sealed record TournamentSource(int PartyCount, IReadOnlyList<TournamentEnemy> Enemies, int OpponentX, int OpponentY, bool HasStartArea)
{
    public TournamentEnemyKind Kind { get; init; }
}
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
    public PvfMapAltarWaves? AltarWaves { get; init; }
    public IReadOnlyList<DfoServer.GameWorld.Characters.TutorialCinematicEvidence> TutorialCinematics { get; init; } = [];
    public DungeonCinematicLayerReturn? CinematicLayerReturn { get; init; }
    public DungeonScriptedLayerReturn? ScriptedLayerReturn { get; init; }
    public DungeonCinematicBossEvidence? BossDeathLayerChange { get; init; }
    public IReadOnlyList<DungeonLostLandWarp> LostLandWarps { get; init; } = [];
    public DungeonEvildomWarChallengeWarp? EvildomWarChallengeWarp { get; init; }
    public DungeonEvildomWarChallengeSpawn? EvildomWarChallengeSpawn { get; init; }
}

internal sealed class DungeonRoomCatalog
{
    private readonly PvfArchive archive;
    private DfoServer.GameWorld.Maps.MapGeometryConfig geometry = DfoServer.GameWorld.Maps.MapGeometryConfig.Empty;
    private readonly IReadOnlyDictionary<int, string> monsters;
    private readonly IReadOnlyDictionary<int, string> passiveObjects;
    private readonly IReadOnlyDictionary<int, string> aiCharacters;
    private readonly IReadOnlyDictionary<int, string> maps;
    private readonly Lazy<IReadOnlyDictionary<int, string>> cinematics;
    private readonly Lazy<DungeonHellPartyCatalog> hellParty;

    private DungeonRoomCatalog(PvfArchive archive, IReadOnlyDictionary<int, string> monsters, IReadOnlyDictionary<int, string> passiveObjects,
        IReadOnlyDictionary<int, string> aiCharacters, IReadOnlyDictionary<int, string> maps)
    {
        (this.archive, this.monsters, this.passiveObjects, this.aiCharacters, this.maps) = (archive, monsters, passiveObjects, aiCharacters, maps);
        cinematics = new(() => DungeonCatalog.ParseDirectory(archive.ReadTokens("cinematic/cinematic.lst"),archive.ResolveString,"cinematic"));
        hellParty = new(() => DungeonHellPartyCatalog.Load(archive));
    }

    public static DungeonRoomCatalog Load(PvfArchive pvf, DfoServer.GameWorld.Maps.MapGeometryConfig? geometry = null)
    {

        var monsters = DungeonCatalog.ParseDirectory(pvf.ReadTokens("monster/monster.lst"), pvf.ResolveString, "monster");
        var objects = ParsePassiveDirectory(pvf.ReadTokens("passiveobject/passiveobject.lst"), pvf.ResolveString);
        var actors = DungeonCatalog.ParseDirectory(pvf.ReadTokens("aicharacter/aicharacter.lst"),pvf.ResolveString,"aicharacter");
        var maps = DungeonCatalog.ParseDirectory(pvf.ReadTokens("map/map.lst"), pvf.ResolveString, "map");
        return new(pvf, monsters, objects, actors, maps) { geometry = geometry ?? DfoServer.GameWorld.Maps.MapGeometryConfig.Empty };
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

    public RevisedBeastEncounter RevisedBeastEncounter(byte difficulty) =>
        global::DfoServer.GameWorld.Dungeons.RevisedBeastEncounter.Read(archive, monsters, difficulty);

    public DungeonRoomDefinition Get(DungeonMap map, bool requirePassiveDefinitions = true, bool allowUnresolvedSpecialMetadata = false)
    {
        string path = PvfArchive.NormalizePath(map.Path);
        var source = archive.Contains(path) ? archive : null;
        if (map.Id < 0 || !path.StartsWith("map/", StringComparison.Ordinal) || !path.EndsWith(".map", StringComparison.Ordinal)
            || source is null)
            throw new InvalidDataException("房间地图路径或索引无效");
        var tokens = NativeResourcePrefix(geometry.ReadTokens(source, path), source.ResolveString);
        var roomMonsters = ReferenceEquals(source, archive) ? monsters : DungeonCatalog.ParseDirectory(source.ReadTokens("monster/monster.lst"), source.ResolveString, "monster");
        var roomObjects = ReferenceEquals(source, archive) ? passiveObjects : ParsePassiveDirectory(source.ReadTokens("passiveobject/passiveobject.lst"), source.ResolveString);
        var room = ParseResource(map with { Path = path }, tokens, source.ResolveString, roomMonsters, roomObjects, source.Contains,
            requirePassiveDefinitions,ResolveAiCharacter,allowUnresolvedSpecialMetadata);
        // 只读本房引用的MOB，同模板在本次操作内只解析一次；限制随资源行冻结，不另建全局缓存。
        var restrictions = new Dictionary<int, (bool Restricted, bool Named)>();
        room = room with { Monsters = Array.AsReadOnly(room.Monsters.Select(monster =>
        {
            if (!restrictions.TryGetValue(monster.MonsterId, out var flags))
            {
                var mob = archive.ReadTokens(monster.Path);
                flags = (ParseChampionRestriction(mob, archive.ResolveString), ParseNamedMonster(mob, archive.ResolveString));
                restrictions.Add(monster.MonsterId, flags);
            }
            return monster with { ChampionRestricted = flags.Restricted, IsNamed = flags.Named };
        }).ToArray()) };
        if (room.AltarWaves is { } altar)
            foreach (int ordinal in altar.Phases.Select(phase => phase.MonsterOrdinal).Distinct())
            {
                int id = altar.Monsters[ordinal].Values[0];
                if (!monsters.TryGetValue(id, out string? monsterPath) || monsterPath.Length == 0 || !archive.Contains(monsterPath))
                    throw new InvalidDataException("祭坛实际生成的怪物定义不存在：" + id);
            }
        // 基础图的ON START MAP也可声明向下换层；和末层返回共用同一严格ACT/CMT识别。
        return room with
        {
            CinematicLayerReturn = ReadCinematicLayerReturn(room,tokens),
            ScriptedLayerReturn = room.Map.Kind == "layered" ? ReadScriptedLayerReturn(room) : null,
            BossDeathLayerChange = room.Map.Kind == "boss" ? ReadBossDeathLayerChange(room) : null,
            EvildomWarChallengeWarp = room.Map.Id is 400000524 or 400000525
                ? DungeonEvildomWarChallengeWarpRules.Read(room, file => archive.Contains(file) ? archive.ReadTokens(file) : [], archive.ResolveString)
                : null,
            EvildomWarChallengeSpawn = room.Map.Id is 400000524 or 400000525
                ? DungeonEvildomWarChallengeSpawnRules.Read(room, file => archive.Contains(file) ? archive.ReadTokens(file) : [],
                    archive.ResolveString, id => monsters.GetValueOrDefault(id)) : null
        };
    }

    public DungeonRoomDefinition WithBossRushMonsters(DungeonRoomDefinition room, BossRushCatalog catalog, int offset)
    {
        if (offset < 0 || offset + room.Monsters.Count > catalog.Bosses.Length)
            throw new InvalidDataException("BossRush 房间怪物数量超过专表名单");
        // 按路线资源行顺序消费专表；仅替换模板，保留地图位置、等级和品级声明。
        var result = room.Monsters.Select((source, index) =>
        {
            int id = catalog.Bosses[offset + index];
            if (!monsters.TryGetValue(id, out string? path) || !archive.Contains(path))
                throw new InvalidDataException("BossRush 怪物定义不存在：" + id);
            var raw = source.RawValues.ToArray();
            raw[0] = id;
            return source with { MonsterId = id, RawValues = Array.AsReadOnly(raw), Path = path,
                ChampionRestricted = ParseChampionRestriction(archive.ReadTokens(path), archive.ResolveString),
                IsNamed = ParseNamedMonster(archive.ReadTokens(path), archive.ResolveString) };
        }).ToArray();
        return room with { Monsters = Array.AsReadOnly(result) };
    }

    public DungeonRoomDefinition WithLostLandWarps(DungeonRoomDefinition room)
    {
        var warps = new List<DungeonLostLandWarp>();
        for (int ordinal = 0; ordinal < room.PassiveObjects.Count; ordinal++)
        {
            var source = room.PassiveObjects[ordinal];
            if (source.RawValues.Count != 4 || source.Path is not { Length: > 0 } objectPath ||
                !archive.Contains(objectPath)) continue;
            try
            {
                string? action = DungeonCinematicLayerReturnRules.BasicAction(archive.ReadTokens(objectPath),archive.ResolveString);
                if (string.IsNullOrWhiteSpace(action)) continue;
                string path = PvfArchive.NormalizePath(objectPath[..(objectPath.LastIndexOf('/') + 1)] + action);
                if (!path.StartsWith("passiveobject/",StringComparison.Ordinal) || !path.EndsWith(".act",StringComparison.Ordinal) ||
                    !archive.Contains(path)) continue;
                if (DungeonLostLandWarpRules.UnconditionalMove(archive.ReadTokens(path),archive.ResolveString) is { } move)
                    warps.Add(new(ordinal,source.ObjectId,objectPath,path,move.Destination,move.PlayerX,move.PlayerY));
                else if (DungeonLostLandWhirlwind.Read(ordinal,source,passiveObjects,
                    file => archive.Contains(file) ? archive.ReadTokens(file) : [],archive.ResolveString) is { } whirlwind)
                    warps.Add(whirlwind);
                else if (DungeonLostLandRescue.Read(ordinal,room,passiveObjects,
                    file => archive.Contains(file) ? archive.ReadTokens(file) : [],archive.ResolveString) is { } rescue)
                    warps.Add(rescue);
            }
            // 可选导航声明不完整时仍保留原房间，但不开放该传送。
            catch (InvalidDataException) { }
        }
        return room with { LostLandWarps = Array.AsReadOnly(warps.ToArray()) };
    }

    internal static bool ParseNamedMonster(IReadOnlyList<PvfToken> tokens, Func<int, string> resolve)
    {
        bool found = false;
        for (int at = 0; at < tokens.Count; at++)
        {
            if (tokens[at].Type != 3 || resolve(tokens[at].Value) != "[is named monster]") continue;
            if (found || at + 1 >= tokens.Count || tokens[at + 1] is not { Type: 0, Value: 1 } ||
                at + 2 < tokens.Count && tokens[at + 2].Type != 3) return false;
            found = true;
        }
        return found;
    }

    internal static bool ParseChampionRestriction(IReadOnlyList<PvfToken> tokens, Func<int, string> resolve)
    {
        int seen = 0;
        for (int at = 0; at < tokens.Count; at++)
        {
            if (tokens[at].Type != 3) continue;
            int flag = resolve(tokens[at].Value) switch { "[no champion]" => 1, "[is named monster]" => 2, _ => 0 };
            if (flag == 0) continue;
            // 缺值、重复和未知形状不阻断原演员，只禁止本次随机强化。
            if ((seen & flag) != 0 || at + 1 >= tokens.Count || tokens[at + 1] is not { Type: 0, Value: 0 } ||
                at + 2 < tokens.Count && tokens[at + 2].Type != 3) return true;
            seen |= flag;
        }
        return false;
    }

    DungeonCinematicBossEvidence? ReadBossDeathLayerChange(DungeonRoomDefinition room)
    {
        try
        {
            var bosses = room.Monsters.Select((monster,index) => (monster,index)).Where(row => row.monster.RankKind == "[boss]").ToArray();
            if (bosses is not [var boss] || boss.monster.Modifiers.Count != 0 || boss.monster.PlacementKind != "[fixed]") return null;
            int? id = DungeonCinematicBossTerminalRules.DieCinematic(archive.ReadTokens(boss.monster.Path),archive.ResolveString);
            if (id is not { } movie || !cinematics.Value.TryGetValue(movie,out string? file) || !archive.Contains(file)) return null;
            var body = archive.ReadTokens(file);
            bool valid = DungeonCinematicBossTerminalRules.DeathChangesLayer(room.Map.Id,body,archive.ResolveString);
            return valid ? new(room.Map.Id,room.Map.Path,boss.index,boss.monster.MonsterId,boss.monster.Path,movie,file) : null;
        }
        // 可选声明不完整时不扩张终点范围；原地图及死亡事务仍按原规则处理。
        catch (InvalidDataException) { return null; }
    }

    DungeonScriptedLayerReturn? ReadScriptedLayerReturn(DungeonRoomDefinition room)
    {
        try { return Read(); }
        // 附加ACT仅授予呈现返回；格式不支持时保留已严格校验的原MAP与清怪路线。
        catch (InvalidDataException) { return null; }

        DungeonScriptedLayerReturn? Read()
        {
        DungeonScriptedLayerReturn? evidence = null;
        for (int ordinal = 0; ordinal < room.PassiveObjects.Count; ordinal++)
        {
            var source = room.PassiveObjects[ordinal];
            if (source.Path is not { Length: > 0 } objectPath || !archive.Contains(objectPath)) continue;
            var actions = DungeonScriptedLayerReturnRules.OtherActions(archive.ReadTokens(objectPath),archive.ResolveString);
            if (actions is null) continue;
            for (int index = 0; index < actions.Count; index++)
            {
                string path = PvfArchive.NormalizePath(objectPath[..(objectPath.LastIndexOf('/') + 1)] + actions[index]);
                if (!path.StartsWith("passiveobject/",StringComparison.Ordinal) || !path.EndsWith(".act",StringComparison.Ordinal) ||
                    !archive.Contains(path)) continue;
                var move = DungeonScriptedLayerReturnRules.ReturnMove(archive.ReadTokens(path),archive.ResolveString);
                if (move is not { } target || target.X != room.Map.Position.X || target.Y != room.Map.Position.Y) continue;
                if (evidence is not null) return null;
                evidence = new(room.Map.Id,room.Map.Path,ordinal,source.ObjectId,objectPath,index,path,
                    target.X,target.Y,target.PlayerX,target.PlayerY,target.SceneFlag);
            }
        }
        return evidence;
        }
    }

    // 实验性（未在95客户端实证；依据参考项目 BuildHellPartyWaves/BuildHellPartyMonsterInfos）：给深渊房间计划抽波次并展开成怪物。
    // 怪物/APC 定义缺失的条目跳过（参考项目会硬失败，这里不让入场整体失败）；普通怪等级=副本基准等级，APC 取 AICharacter 等级。
    public DungeonHellPartyCatalog HellParty => hellParty.Value;
    public IReadOnlyList<DungeonHellWaveMonster> ResolveHellWaves(DungeonRoomDefinition room, byte mode, int? basisLevel, Func<int, int> draw)
    {
        var catalog = hellParty.Value;
        var tables = room.SpecialObjects.SelectMany(parent => parent.Children).Where(child => child.Kind == "[hellparty]").Select(child => child.RawValues).ToArray();
        if (tables.Length == 0 || basisLevel is not (>= 1 and <= 255)) return [];
        char key = DungeonHellPartyCatalog.DifficultyKey(mode);
        var result = new List<DungeonHellWaveMonster>();
        foreach (var (order, group) in catalog.PickWaves(tables, mode, draw))
            foreach (var (code, entityType) in catalog.Entries(group, key))
            {
                byte type = 0;
                int level = basisLevel.Value;
                string resourcePath = "";
                if (entityType == 1)
                {
                    if (!aiCharacters.TryGetValue(code, out var apcPath) || !archive.Contains(apcPath)) continue;
                    type = 5;
                    resourcePath = apcPath;
                    var minimum = PvfAiCharacterScript.MinimumInfo(archive.ReadTokens(apcPath), archive.ResolveString);
                    if (minimum.Length >= 6 && minimum[5].Type == 0 && minimum[5].Value is >= 1 and <= 255) level = minimum[5].Value;
                }
                else if (!monsters.TryGetValue(code, out var path) || !archive.Contains(path)) continue;
                else resourcePath = path;
                result.Add(new(order, group, code, (byte)level, type, 0, 0) { Path = resourcePath });
            }
        return Array.AsReadOnly(result.ToArray());
    }

    DungeonCinematicLayerReturn? ReadCinematicLayerReturn(DungeonRoomDefinition room, IReadOnlyList<PvfToken> tokens)
    {
        try { return Read(); }
        // 可选演出元数据不支持时保留正常清怪路线；MAP及演员主资源仍在Get中严格校验。
        catch (InvalidDataException) { return null; }

        DungeonCinematicLayerReturn? Read()
        {
        var map = room.Map;
        string? action = DungeonCinematicLayerReturnRules.BasicAction(tokens,archive.ResolveString);
        if (string.IsNullOrEmpty(action)) return null;
        string path = PvfArchive.NormalizePath(action.StartsWith("map/",StringComparison.OrdinalIgnoreCase) ? action :
            map.Path[..(map.Path.LastIndexOf('/') + 1)] + action);
        if (!path.StartsWith("map/",StringComparison.Ordinal) || !archive.Contains(path)) return null;
        var actionTokens = archive.ReadTokens(path);
        int? id = DungeonCinematicLayerReturnRules.OneShotCinematic(actionTokens,archive.ResolveString);
        int? actorId = null;
        if (id is null && DungeonCinematicLayerReturnRules.ActorCinematic(actionTokens,archive.ResolveString) is { } actor &&
            room.AiCharacters.Count(row => row.Id == actor.ActorId) == 1)
        {
            id = actor.CinematicId;
            actorId = actor.ActorId;
        }
        if (id is not { } movie || !archive.Contains("cinematic/cinematic.lst") || !cinematics.Value.TryGetValue(movie,out string? file) ||
            !archive.Contains(file)) return null;
        var cinematicTokens = archive.ReadTokens(file);
        bool navigation = actorId is not null
            ? DungeonCinematicLayerReturnRules.IsActorMapChange(map.Id,cinematicTokens,archive.ResolveString)
            : DungeonCinematicLayerReturnRules.IsNonCombatMapChange(map.Id,cinematicTokens,archive.ResolveString);
        // 严格匹配后的10 token为无APC绑定的ON START MAP，不继承末层返回基础格资格。
        return navigation ? new(map.Id,map.Path,path,movie,file)
            { ActorId=actorId, IsOnStartMap=actionTokens.Length == 10 } : null;
        }
    }

    // 读取比赛图的对手表与对手侧起点；APC保留独立目录与真实模板，匹配027E911D生成分支。
    public TournamentSource ReadTournamentSource(DungeonMap map)
    {
        string path = PvfArchive.NormalizePath(map.Path);
        if (!path.StartsWith("map/tournament/",StringComparison.Ordinal) || !path.EndsWith(".map",StringComparison.Ordinal) || !archive.Contains(path))
            throw new InvalidDataException("比赛地图路径无效");
        return ParseTournamentSource(archive.ReadTokens(path),archive.ResolveString,monsters,archive.Contains,aiCharacters,
            id => ResolveAiCharacter(new([id,0,0,0,0,0],"[monster]","[normal]")));
    }

    internal static TournamentSource ParseTournamentSource(IReadOnlyList<PvfToken> tokens,Func<int,string> resolve,
        IReadOnlyDictionary<int,string> monsterPaths,Func<string,bool> exists,
        IReadOnlyDictionary<int,string>? aiCharacterPaths = null,Func<int,DungeonRoomAiCharacter?>? resolveAiCharacter = null)
    {
        var enemies = new List<TournamentEnemy>();
        TournamentEnemyKind enemyKind = TournamentEnemyKind.Monster;
        int? partyCount = null; int x = 760, y = 270; bool area = false;
        for (int at = 0; at < tokens.Count; at++)
        {
            if (tokens[at].Type != 3) continue;
            string tag = resolve(tokens[at].Value);
            var body = tokens.Skip(at + 1).TakeWhile(token => token.Type != 3).ToArray();
            if (tag == "[tournament enemies]")
            {
                if (partyCount is not null) throw new InvalidDataException("重复 tournament enemies");
                if (body.Length < 5 || body[0].Type != 0 || body[0].Value is < 1 or > 2 || body[1].Type != 6 || (body.Length - 2) % 3 != 0)
                    throw new InvalidDataException("tournament enemies 头部或记录格式无效");
                string kind = resolve(body[1].Value);
                enemyKind = kind switch
                {
                    "[monster]" => TournamentEnemyKind.Monster,
                    "[apc]" => TournamentEnemyKind.AiCharacter,
                    _ => throw new NotSupportedException("unsupported: tournament enemies kind " + kind)
                };
                var paths = enemyKind == TournamentEnemyKind.Monster ? monsterPaths : aiCharacterPaths
                    ?? throw new NotSupportedException("unsupported: tournament APC catalog missing");
                partyCount = body[0].Value;
                for (int i = 2; i < body.Length; i += 3)
                {
                    if (body[i].Type != 0 || body[i + 1].Type != 0 || body[i].Value <= 0 || body[i + 1].Value is < 0 or > ushort.MaxValue)
                        throw new InvalidDataException("tournament enemy 记录无效");
                    if (!paths.TryGetValue(body[i].Value,out var enemyPath) || enemyPath.Length == 0 || !exists(enemyPath))
                        throw new InvalidDataException("tournament enemy 记录无效");
                    DungeonRoomAiCharacter? apc = null;
                    if (enemyKind == TournamentEnemyKind.AiCharacter)
                    {
                        // 034D6210只接模板code；386的level不覆盖AIC自身最低信息等级。
                        if (body[i].Value > 80000 || resolveAiCharacter is null)
                            throw new NotSupportedException("unsupported: tournament APC template factory");
                        apc = resolveAiCharacter(body[i].Value);
                        if (apc is null || apc.Id != body[i].Value || apc.Path != enemyPath || apc.Level is < 1 or > 255 ||
                            apc.Faction != "[monster]" || apc.RankKind != "[normal]")
                            throw new InvalidDataException("比赛APC模板来源或等级无效");
                    }
                    enemies.Add(new(body[0].Value,body[i].Value,body[i + 1].Value) { AiCharacter = apc });
                }
            }
            else if (tag == "[tournament start area]" && body is [{ Type: 0, Value: var count },{ Type: 0, Value: var px },{ Type: 0, Value: var py },{ Type: 0, Value: 2 }]
                && count == (partyCount ?? count))
            {
                (x, y, area) = (px, py, true);
            }
        }
        if (partyCount is null || enemies.Select(enemy => enemy.Code).Distinct().Count() != enemies.Count)
            throw new InvalidDataException("tournament enemies 缺失或重复");
        return new(partyCount.Value,Array.AsReadOnly(enemies.ToArray()),x,y,area) { Kind = enemyKind };
    }

    // 为比赛对手构造 monster 房间行（[fixed]，相对基准等级偏移 0）；路径取 monster.lst，缺失则拒绝。
    public DungeonRoomMonster TournamentMonster(int code,int x,int y,bool boss)
    {
        if (!monsters.TryGetValue(code,out var path) || path.Length == 0 || !archive.Contains(path))
            throw new InvalidDataException("比赛对手怪物资源不存在: " + code);
        return new(code,[code,1,0,x,y,0,0,0],"[fixed]",boss ? "[boss]" : "[normal]",path);
    }

    DungeonRoomAiCharacter? ResolveAiCharacter(PvfAiCharacterSpawn row)
    {
        int id = row.Values[0];
        // 匹配客户端 02A0B720：目录不命中返回 al=0，上层不计创建成功，不阻断整图装载。
        // 已列入且脚本存在的 APC 仍校验最低信息；不把缺项改写成相邻编号。
        if (!aiCharacters.TryGetValue(id,out var path) || !archive.Contains(path))
            return null;
        var minimum = PvfAiCharacterScript.MinimumInfo(archive.ReadTokens(path),archive.ResolveString);
        // 匹配AIC：名字后四项角色字段，再是等级；不以角色或副本等级替代APC自身声明。
        if (minimum.Length < 6 || minimum[0].Type != 6 || minimum.Skip(1).Take(5).Any(token => token.Type != 0) ||
            minimum[5].Value is < 1 or > 255) throw new InvalidDataException("房间APC最低信息或等级无效: " + id);
        return new(id,row.Values,row.Faction,row.Rank,path,minimum[5].Value);
    }

    // 仅核对共享monster.lst及正文存在性，不创建演员或证明地图生成资格。
    internal bool HasMonsterDefinition(int id) => monsters.TryGetValue(id, out var path) && path.Length != 0 && archive.Contains(path);

    public bool ReportsQuestDeath(DungeonRoomPassiveObject source) =>
        Tagged(source, "[quest type]", "[must be destroyed]");

    // 时间广场巨大的泰拉石（85176）声明 [reward type] [quest item]，任务脚本用 enemy type 3 发材料。
    // 它没有 [quest type] [must be destroyed]，销毁仍要进入任务掉落。
    public bool ReportsQuestItemReward(DungeonRoomPassiveObject source) =>
        Tagged(source, "[reward type]", "[quest item]");

    bool Tagged(DungeonRoomPassiveObject source, string tag, string value)
    {
        if (!passiveObjects.TryGetValue(source.ObjectId,out var path) || path != source.Path || path.Length == 0 || !archive.Contains(path)) return false;
        var tokens = archive.ReadTokens(path);
        for (int at = 0; at + 1 < tokens.Length; at++)
            if (tokens[at].Type == 3 && archive.ResolveString(tokens[at].Value) == tag)
                return tokens[at + 1].Type == 6 && archive.ResolveString(tokens[at + 1].Value) == value;
        return false;
    }

    internal static DungeonRoomDefinition ParseResource(DungeonMap map, IReadOnlyList<PvfToken> tokens, Func<int, string> resolve,
        IReadOnlyDictionary<int, string> monsterPaths, IReadOnlyDictionary<int, string> objectPaths, Func<string, bool> exists,
        bool requirePassiveDefinitions = true, Func<PvfAiCharacterSpawn,DungeonRoomAiCharacter?>? resolveAiCharacter = null,
        bool allowUnresolvedSpecialMetadata = false)
    {
        tokens = NativeResourcePrefix(tokens, resolve);
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
                if (MissingRankTail(monsterTokens, at, monsterTokens.Count, resolve))
                {
                    var raw = Integers(monsterTokens.Skip(at).Take(8));
                    monsterRows.Add(new(raw[0],raw,"[fixed]",null,Resolve(raw[0],monsterPaths,"monster")));
                    break;
                }
                if (monsterTokens.Count - at < 10)
                    throw new NotSupportedException("unsupported: 房间 monster 缺少八整数和完整字符串标记");
                var values = Integers(monsterTokens.Skip(at).Take(8));
                at += 8;
                var labels = new List<string>();
                int? npcParameter = null;
                while (at < monsterTokens.Count && monsterTokens[at].Type == 6)
                {
                    string label = resolve(monsterTokens[at++].Value);
                    if (label == "[NPC]")
                    {
                        // Full03 0425D145/152在placement之后消费一个原整数，随后才读dummy及品级。
                        if (labels.Count != 1)
                            throw new NotSupportedException("unsupported: 房间 monster 的 NPC 标记重复或顺序无效");
                        if (at >= monsterTokens.Count || monsterTokens[at].Type != 0)
                            throw new NotSupportedException("unsupported: 房间 monster 的 NPC 参数不是完整整数");
                        npcParameter = monsterTokens[at++].Value;
                    }
                    labels.Add(label);
                }
                if (labels.Count < 2)
                    throw new NotSupportedException("unsupported: 房间 monster 标记不是字符串");
                if (labels.Any(label => label.Length == 0))
                    throw new NotSupportedException("unsupported: 房间 monster 标记为空");
                if (npcParameter is not null)
                {
                    int rankAt = 2;
                    if (rankAt < labels.Count && labels[rankAt] == "[dummy]")
                        rankAt++;
                    if (rankAt < labels.Count && labels[rankAt] == "[displayhuntdummy]")
                        rankAt++;
                    if (rankAt != labels.Count - 1 || labels[rankAt] is not ("[normal]" or "[champion]" or "[super champion]" or "[boss]"))
                        throw new NotSupportedException("unsupported: 房间 monster 的 NPC 后续修饰顺序或品级尚未解析");
                }
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
                    // 保留NPC/dummy原标记；结构恢复不授予普通生成、死亡、奖励或会面资格。
                    Modifiers = Freeze(labels.Skip(1).Take(labels.Count - 2)),
                    NpcParameter = npcParameter,
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
                // 仅兼容已核对的空行；ID 0已登记时仍须严格解析真实对象，不能吞掉缺失脚本。
                bool emptyPlaceholder = raw is [0, 1, -1, -1, 0] && !objectPaths.ContainsKey(0);
                // 仅训练保留已从LST移除的历史hellparty声明，不生成新演员或奖励。
                string? path = emptyPlaceholder ? null :
                    allowUnresolvedSpecialMetadata && objectRow.Children.All(child => child.Kind == "[hellparty]")
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
        {
            if (resolveAiCharacter is null)
                throw new InvalidDataException("房间APC定义解析依赖缺失");
            foreach (var row in PvfAiCharacterScript.MapRows(actorTokens,resolve))
                if (resolveAiCharacter(row) is { } actor)
                    actors.Add(actor);
        }
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
                Npcs = npcs, NpcMetadataError = npcError, DeclaredType = declaredType,
                AltarWaves = MapAltarWaves.Read(tokens, resolve) };

        string Resolve(int id, IReadOnlyDictionary<int, string> paths, string kind)
        {
            if (id < 0 || !paths.TryGetValue(id, out string? path) || path.Length == 0 || !exists(path))
                throw new InvalidDataException("房间资源引用不存在: " + kind + " " + id);
            return path;
        }
    }

    // 原生读者保留已追加的缺rank末行，随后停止MAP分派；所有附属读者共用同一有效前缀。
    private static IReadOnlyList<PvfToken> NativeResourcePrefix(IReadOnlyList<PvfToken> tokens, Func<int,string> resolve)
    {
        for (int at = 0; at < tokens.Count; at++)
        {
            if (tokens[at].Type != 3 || !resolve(tokens[at].Value).Equals("[monster]",StringComparison.OrdinalIgnoreCase)) continue;
            int end = at + 1;
            while (end < tokens.Count && tokens[end].Type != 3) end++;
            if (end < tokens.Count && resolve(tokens[end].Value).Equals("[/monster]",StringComparison.OrdinalIgnoreCase) &&
                end - 9 >= at + 1 && MissingRankTail(tokens,end - 9,end,resolve))
            {
                var prefix = end + 1 == tokens.Count ? tokens : Freeze(tokens.Take(end + 1));
                // 以原输入的闭合声明识别外层块，不能因截断删掉外层闭合后把条件演员提升为顶层。
                var blocks = tokens.Where(token => token.Type == 3).Select(token => resolve(token.Value))
                    .Where(tag => tag.StartsWith("[/",StringComparison.Ordinal) && tag.EndsWith(']'))
                    .Select(tag => tag[2..^1].Trim().ToLowerInvariant()).Distinct(StringComparer.Ordinal)
                    .ToDictionary(name => name,name => name,StringComparer.Ordinal);
                blocks.TryAdd("condition","condition");
                try
                {
                    if (!new PvfTokenDocument(prefix,resolve,blocks,preserveExtraEndTags:true).Sections.Any(section => section.Name == "monster"))
                        throw new NotSupportedException("unsupported: 缺rank怪物不是顶层声明");
                }
                catch (InvalidDataException error)
                {
                    throw new NotSupportedException("unsupported: 缺rank怪物的外层声明未确认",error);
                }
                return prefix;
            }
        }
        return tokens;
    }

    private static bool MissingRankTail(IReadOnlyList<PvfToken> tokens, int start, int end, Func<int,string> resolve)
    {
        if (end - start != 9 || tokens[end - 1].Type != 6 || resolve(tokens[end - 1].Value) != "[fixed]") return false;
        for (int at = start; at < end - 1; at++)
            if (tokens[at].Type != 0) return false;
        return true;
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
