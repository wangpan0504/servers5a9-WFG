using System.Collections.Concurrent;
using PvfLib;

namespace DfoServer.GameWorld.Dungeons;

internal readonly record struct DungeonPoint(int X, int Y);
internal sealed record DungeonMap(string Kind, DungeonPoint Position, int Id, string Path)
{
    public int StoryStageIndex { get; init; } = -1;
    public int LayerIndex { get; init; } = -1;
    public bool IsConditional { get; init; }
    public IReadOnlyList<int> CandidateIds { get; init; } = Array.Empty<int>();
}
internal sealed record DungeonRawSection(string Tag, IReadOnlyList<PvfToken> Tokens);
// 实验性（未在95客户端实证）：深渊派对房间 = maze 内 [hell dungeon] 1 + [seal door map index]（深渊房间地图）+ [seal door pos]（该房间在迷宫内的坐标）。
// 依据参考项目 dungeon_hell_room.go（对应 A21 FindHellMapRoom/TryBuildSealDoorHellPartyRoom）。
internal sealed record DungeonHellPartyRoom(int MapId, DungeonPoint Room)
{
    // 实验性：[hell party required item count]（95 PVF 里每个深渊 dgn 显式声明的入场票数，黑暗都市=30）；缺省 0 = 声明缺失，按等级公式回退。
    public int RequiredItemCount { get; init; }
    // 实验性：[seal door appear rate]（普通入场时密门出现概率声明；仅保留数据，参考项目 90 与已知协议都没有对应的密门运行时，未实现随机出现）。
    public int AppearRate { get; init; }
    // 实验性：传奇深渊 [seal door legend map index]（黑暗都市=313169）与 [hell legend party required item count]（=2）；0=无传奇深渊。
    public int LegendMapId { get; init; }
    public int LegendRequiredCount { get; init; }
}
internal sealed record DungeonMaze(
    int SourceOrdinal,
    int Width,
    int Height,
    string Greed,
    IReadOnlyList<DungeonPoint> Starts,
    IReadOnlyList<DungeonPoint> Bosses,
    IReadOnlyList<DungeonMap>? MapSpecification,
    IReadOnlyList<PvfToken> QuestConnectionTokens)
{
    // 实验性：只有普通（非任务连接）maze 带 [hell dungeon] 时才非空；任务 maze 带 [escape hell]，不属于深渊派对。
    public DungeonHellPartyRoom? HellParty { get; init; }
    // 实验性：深渊房间地图的 PVF 路径（[seal door map index] 对应的 map 文件），用于把 HellParty.Room 处的普通房间换成深渊房间。
    public string? HellPartyPath { get; init; }
    public string? HellLegendPath { get; init; }
    public IReadOnlyList<DungeonMap> LayeredMaps { get; init; } = Array.Empty<DungeonMap>();
    // 保留覆盖前来源供任务地图池排除；不参与有效路线、候选抽取或演员准备。
    public IReadOnlyList<DungeonMap> OverriddenMaps { get; init; } = Array.Empty<DungeonMap>();
    public IReadOnlyList<DungeonMap> StoryStages { get; init; } = Array.Empty<DungeonMap>();
    public DungeonBossEntrance? BossEntrance { get; init; }
    public IReadOnlyList<DungeonWarpMap> WarpMaps { get; init; } = Array.Empty<DungeonWarpMap>();
    public IReadOnlyList<DungeonMap> ConditionalMaps { get; init; } = Array.Empty<DungeonMap>();
    public IReadOnlyList<DungeonClearCondition> ClearConditions { get; init; } = [];
    public string? ResourceError { get; init; }
    public bool IsTrainingRoom { get; init; }
    public bool IsClassExperience { get; init; }
    // 实验性（#259）：比赛副本的 1x1 单图迷宫，地图取自 PVF 地图反向 [dungeon] 声明。
    public bool IsTournament { get; init; }
    // 仅识别已确认的三个整数；原始 token 继续保留给资源诊断。
    public bool ConnectsQuest(uint questId, byte difficulty = 0, int state = 0) => QuestConnectionTokens.Count == 3 &&
        QuestConnectionTokens.All(token => token.Type == 0) &&
        QuestConnectionTokens[0].Value == state && QuestConnectionTokens[1].Value == questId &&
        (QuestConnectionTokens[2].Value == -1 || QuestConnectionTokens[2].Value == difficulty);
    public int? ConnectionQuestId => QuestConnectionTokens.Count == 3 && QuestConnectionTokens.All(token => token.Type == 0) &&
        QuestConnectionTokens[0].Value is 0 or 1 && QuestConnectionTokens[1].Value is > 0 and < 30000 && QuestConnectionTokens[2].Value is >= -1 and <= 4
            ? QuestConnectionTokens[1].Value : null;
    public bool HasActiveQuestConnection(int questId) => ConnectionQuestId == questId && QuestConnectionTokens[0].Value == 0;

    // 布局实质相同判定：同一任务连接的 maze 可能在资源里重复出现（如 SunderLandDark 的转职任务
    // 7104/7105 各重复一次），字段完全一致时视为同一路线，避免入场被歧义拒绝。
    public bool SameLayout(DungeonMaze other) =>
        Width == other.Width && Height == other.Height && Greed == other.Greed && ResourceError == other.ResourceError &&
        Starts.SequenceEqual(other.Starts) && Bosses.SequenceEqual(other.Bosses) &&
        SameMaps(MapSpecification, other.MapSpecification) && SameMaps(LayeredMaps, other.LayeredMaps) &&
        SameMaps(StoryStages, other.StoryStages) && SameMaps(ConditionalMaps, other.ConditionalMaps) &&
        WarpMaps.SequenceEqual(other.WarpMaps) && ClearConditions.SequenceEqual(other.ClearConditions) &&
        SameEntrance(BossEntrance, other.BossEntrance);

    static bool SameEntrance(DungeonBossEntrance? left, DungeonBossEntrance? right) =>
        left is null || right is null ? left is null && right is null :
        left.TimeMode == right.TimeMode && left.TimeValue == right.TimeValue && left.Targets.SequenceEqual(right.Targets);

    static bool SameMaps(IReadOnlyList<DungeonMap>? left, IReadOnlyList<DungeonMap>? right)
    {
        if (left is null || right is null) return left is null && right is null;
        if (left.Count != right.Count) return false;
        for (int i = 0; i < left.Count; i++)
            if (!SameMap(left[i], right[i])) return false;
        return true;
    }

    static bool SameMap(DungeonMap left, DungeonMap right) =>
        left.Kind == right.Kind && left.Position == right.Position && left.Id == right.Id && left.Path == right.Path &&
        left.StoryStageIndex == right.StoryStageIndex && left.LayerIndex == right.LayerIndex && left.IsConditional == right.IsConditional &&
        left.CandidateIds.SequenceEqual(right.CandidateIds);

    // 只描述资源结构，不表示准入通过或可以向客户端发布初始化通知。
    public string MapStructure => MapSpecification is null ? "unsupported" : "explicit";
    public string? UnsupportedReason => ResourceError ?? (MapSpecification is null ? "缺少显式 map specification，地图生成合同未实现" : null);
    // 资源查询及inspect JSON提供整数索引；未知格式不会阻断其他maze或生成已批准通路。
    public DungeonGreedGrid GreedGrid => DungeonGreedGrid.Read(Width, Height, Greed);
    public DungeonStaticTopology StaticTopology => DungeonStaticTopology.Read(this);
}

internal sealed record DungeonDefinition(int Id, string Path, int? MinimumLevel, int? BasisLevel, IReadOnlyList<DungeonMaze> Mazes, IReadOnlyList<DungeonRawSection> RawSections)
{
    public bool IsTutorial => RawSections.Where(row => row.Tag == "tutorial dungeon").ToArray() is
        [{ Tokens: [{ Type: 0, Value: 1 }] }];
    // [tournament dungeon] 1：魂·黄龙/青龙大会等比赛副本（实验性，依据来自 Go 项目反汇编）。
    public bool IsTournament => RawSections.Where(row => row.Tag == "tournament dungeon").ToArray() is [{ Tokens: [{ Type: 0, Value: 1 }] }];
    public bool DisableClearReward => RawSections.Any(row => row.Tag == "disable clear reward" && row.Tokens.Count == 0);
    // 强袭任务副本均带 [disable black diamond card reward]（无值标记）：通关翻牌不提供黑钻额外卡。
    public bool DisableBlackDiamondCardReward => RawSections.Any(row => row.Tag == "disable black diamond card reward" && row.Tokens.Count == 0);
    public bool IsClassExperience => Id == 7142 &&
        Path.Equals("dungeon/cataclysm/changeclass/7142_changeclass.dgn", StringComparison.OrdinalIgnoreCase) &&
        Marker("disable drop item") && Marker("disable clear reward") && Marker("no fatigue") &&
        Marker("no experience monster and result") && Marker("no giveup panalty") &&
        RawSections.Where(row => row.Tag == "enter without fatigue").ToArray() is [{ Tokens: [{ Type: 0, Value: 1 }] }];
    public bool RoomMoveKeepState { get; init; }
    public bool IndividualMapMovement { get; init; }

    bool Marker(string name)
    {
        var rows = RawSections.Where(row => row.Tag == name).ToArray();
        return rows.Length > 0 && rows.All(row => row.Tokens.Count == 0);
    }

    public bool TryGetDesignatedDifficulty(out byte difficulty)
    {
        difficulty = 0;
        var sections = RawSections.Where(section => section.Tag == "designate dungeon difficulty").ToArray();
        if (sections.Length != 1 || sections[0].Tokens.Count != 1 || sections[0].Tokens[0] is not { Type: 0, Value: >= 0 and <= 4 } value)
            return false;
        difficulty = checked((byte)value.Value);
        return true;
    }

    public bool AllowsDifficulty(byte difficulty, bool story, int? connectionQuestId = null)
    {
        if (difficulty > 4) return false;
        if (story)
        {
            var markers = RawSections.Where(section => section.Tag is "story mode" or "/story mode").ToArray();
            if (markers.Length != 0)
            {
                if (markers.Length > 2) return AllowsQuestStoryDifficulty(difficulty,connectionQuestId);
                // 故事块有自己的难度范围；块外普通路线的指定难度和难度表不能覆盖它。
                if (markers is not [{ Tag: "story mode", Tokens.Count: 0 },{ Tag: "/story mode", Tokens.Count: 0 }])
                    return false;
                int start = RawSections.ToList().FindIndex(section => section.Tag == "story mode");
                var size = RawSections.Skip(start + 1).TakeWhile(section => section.Tag != "/story mode")
                    .Where(section => section.Tag == "difficulty size").ToArray();
                return size.Length == 1 && size[0].Tokens is [{ Type: 0, Value: >= 1 and <= 5 }] && difficulty < size[0].Tokens[0].Value;
            }
        }
        var designated = RawSections.Where(section => section.Tag == "designate dungeon difficulty").ToArray();
        if (designated.Length > 1 || designated.Length == 1 && (designated[0].Tokens is not [{ Type: 0, Value: >= -1 and <= 4 }] ||
            designated[0].Tokens[0].Value >= 0 && designated[0].Tokens[0].Value != difficulty)) return false;
        var levels = RawSections.Where(section => section.Tag == "difficulty level").ToArray();
        // 实验性（#259）：比赛副本声明唯一指定难度且无难度表时，放行该指定难度；其他副本仍只放行难度 0。
        if (levels.Length == 0) return IsTournament && designated.Length == 1 || difficulty == 0;
        // 部分副本重复声明同一难度表；内容相同不构成冲突，未知或不一致声明仍拒绝。
        return levels[0].Tokens.Count == 5 && levels[0].Tokens.All(token => token.Type == 0 && token.Value is 0 or 1) &&
            levels.All(section => section.Tokens.SequenceEqual(levels[0].Tokens)) &&
            levels[0].Tokens[difficulty].Value != 0;
    }

    bool AllowsQuestStoryDifficulty(byte difficulty, int? connectionQuestId)
    {
        if (connectionQuestId is not > 0) return false;
        // 多块仅支持明确的整数任务归属；逐块校验，不以顺序或最大难度合并故事范围。
        var owners = new HashSet<int>();
        List<DungeonRawSection>? block = null;
        int? selectedSize = null;
        foreach (var section in RawSections)
        {
            if (section.Tag == "story mode")
            {
                if (block is not null || section.Tokens.Count != 0) return false;
                block = [];
            }
            else if (section.Tag == "/story mode")
            {
                if (block is null || section.Tokens.Count != 0) return false;
                var sizes = block.Where(row => row.Tag == "difficulty size").ToArray();
                var lists = block.Where(row => row.Tag == "quest list").ToArray();
                var ends = block.Where(row => row.Tag == "/quest list").ToArray();
                if (sizes is not [{ Tokens: [{ Type: 0, Value: >= 1 and <= 5 }] }] ||
                    lists.Length != 1 || lists[0].Tokens.Count == 0 ||
                    lists[0].Tokens.Any(token => token.Type != 0 || token.Value <= 0) ||
                    ends is not [{ Tokens.Count: 0 }] || block.IndexOf(ends[0]) != block.IndexOf(lists[0]) + 1) return false;
                foreach (var token in lists[0].Tokens)
                {
                    if (!owners.Add(token.Value)) return false;
                    if (token.Value == connectionQuestId) selectedSize = sizes[0].Tokens[0].Value;
                }
                block = null;
            }
            else block?.Add(section);
        }
        return block is null && selectedSize.HasValue && difficulty < selectedSize.Value;
    }
}
internal sealed class DungeonCatalog
{
    private readonly PvfArchive archive;
    private readonly IReadOnlyDictionary<int, string> dungeons, maps;
    private readonly ConcurrentDictionary<int, Lazy<DungeonDefinition>> definitions = new();
    private readonly Lazy<DungeonMapPool> mapPool;
    private DungeonCatalog(PvfArchive archive, IReadOnlyDictionary<int, string> dungeons, IReadOnlyDictionary<int, string> maps)
    {
        (this.archive, this.dungeons, this.maps) = (archive, dungeons, maps);
        mapPool = new(() => DungeonMapPool.Load(archive,maps),LazyThreadSafetyMode.ExecutionAndPublication);
    }
    public int DungeonCount => dungeons.Count;

    public static DungeonCatalog Load(PvfArchive pvf)
    {

        // 这里只加载两个目录；未知副本的脚本不会因加载目录而被强制解析。
        var dungeons = ParseDirectory(pvf.ReadTokens("dungeon/dungeon.lst"), pvf.ResolveString, "dungeon");
        var maps = ParseDirectory(pvf.ReadTokens("map/map.lst"), pvf.ResolveString, "map");
        return new(pvf, dungeons, maps);
    }

    public DungeonDefinition Get(int id)
    {
        if (id < 0 || !dungeons.TryGetValue(id, out var path))
            throw new InvalidDataException("副本索引不存在: " + id);
        // Lazy 在完整解析成功后才发布 Definition；失败缓存为异常，不发布局部 maze。
        return definitions.GetOrAdd(id, _ => new(() => ParseResource(id, path, archive.ReadTokens(path), archive.ResolveString, maps, archive.Contains), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    public DungeonMaze ResolveOrdinaryMaze(DungeonDefinition definition, DungeonMaze maze, Func<int,int>? draw = null)
    {
        var choose = draw ?? System.Security.Cryptography.RandomNumberGenerator.GetInt32;
        maze = ResolveMapCandidates(maze,maps,archive.Contains,choose);
        // 显式候选只在入场规划抽取；剧情缺图仍不能套普通地图池。
        if (maze.ResourceError is not null || maze.QuestConnectionTokens.Count != 0 || maze.StaticTopology.Supported) return maze;
        return mapPool.Value.Resolve(definition,maze,choose);
    }

    internal static DungeonMaze ResolveMapCandidates(DungeonMaze maze, IReadOnlyDictionary<int,string> paths,
        Func<string,bool> exists, Func<int,int> draw)
    {
        if (maze.ResourceError is not null) return maze;
        var maps = maze.MapSpecification?.Select(map =>
        {
            // 阶段首项固定；普通基础行已按坐标排除全部阶段地图。
            var ids = map.CandidateIds.Count == 0 ? new[] { map.Id } : map.CandidateIds.Distinct().ToArray();
            int index = map.StoryStageIndex >= 0 || ids.Length == 1 ? 0 : draw(ids.Length);
            if (index < 0 || index >= ids.Length) throw new InvalidDataException("显式地图随机索引越界");
            return Resolve(map,ids[index],map.StoryStageIndex < 0 && ids.Length > 1);
        }).ToArray();
        var layers = new List<DungeonMap>();
        var layerIndexes = new Dictionary<DungeonPoint,int>();
        foreach (var map in maze.LayeredMaps)
        {
            var ids = map.LayerIndex >= 0 || map.CandidateIds.Count == 0 ? new[] { map.Id } : map.CandidateIds.ToArray();
            foreach (int id in ids)
            {
                int layerIndex = layerIndexes.GetValueOrDefault(map.Position);
                layerIndexes[map.Position] = layerIndex + 1;
                var layer = Resolve(map,id,ids.Length > 1) with { LayerIndex = layerIndex };
                layers.Add(layer);
            }
        }
        if ((maps is null || maps.SequenceEqual(maze.MapSpecification!)) && layers.SequenceEqual(maze.LayeredMaps))
        {
            if (maze.BossEntrance is null || maze.WarpMaps.Count == 0) return maze;
            // 候选未变化仍复验实际Boss入口声明；直接调用或with后的迷宫不能绕过来源唯一性。
            var conditional = ConditionalMaps(maze);
            return maze.ConditionalMaps.SequenceEqual(conditional) ? maze : maze with { ConditionalMaps = conditional };
        }
        var resolved = maze with { MapSpecification = maps is null ? null : Freeze(maps), LayeredMaps = Freeze(layers),
            StoryStages = maps is null ? maze.StoryStages : Freeze(maps.Where(map => map.StoryStageIndex >= 0).OrderBy(map => map.StoryStageIndex)) };
        return resolved with { ConditionalMaps = ConditionalMaps(resolved) };

        DungeonMap Resolve(DungeonMap map,int id,bool freeze)
        {
            if (id < 0 || !paths.TryGetValue(id,out var path) || !exists(path))
                throw new InvalidDataException("选定地图不存在: " + id);
            return map with { Id = id,Path = path,CandidateIds = freeze ? Freeze(new[] { id }) : map.CandidateIds };
        }
    }

    static IReadOnlyList<DungeonMap> ConditionalMaps(DungeonMaze maze)
    {
        if (maze.BossEntrance is null || maze.MapSpecification is null) return [];
        var maps = new List<DungeonMap>();
        foreach (var warp in maze.WarpMaps)
        {
            // Boss入口只支持每个逻辑房间唯一替换，多目标声明不能任取其一。
            if (maps.Any(map => map.Position == warp.Source))
                throw new InvalidDataException("Boss入口条件传送的同一来源存在多个目标");
            var source = maze.MapSpecification.Where(map => map.Position == warp.Source && map.StoryStageIndex < 0).ToArray();
            var destination = maze.MapSpecification.Where(map => map.Position == warp.Destination && map.StoryStageIndex < 0).ToArray();
            if (source.Length != 1 || destination.Length != 1)
                throw new NotSupportedException("条件传送需要唯一的来源和目标地图");
            maps.Add(destination[0] with { Position = warp.Source,Kind = source[0].Kind,IsConditional = true });
        }
        return Freeze(maps);
    }

    internal static IReadOnlyDictionary<int, string> ParseDirectory(IReadOnlyList<PvfToken> tokens, Func<int, string> resolve, string root)
    {
        if (tokens.Count == 0 || tokens.Count % 2 != 0)
            throw new InvalidDataException("PVF 目录记录不完整: " + root);
        var result = new Dictionary<int, string>();
        for (int i = 0; i < tokens.Count; i += 2)
        {
            if (tokens[i].Type != 0 || tokens[i].Value < 0 || tokens[i + 1].Type != 6)
                throw new InvalidDataException("PVF 目录 ID/路径类型无效: " + root);
            string path = PvfArchive.NormalizePath(root + "/" + resolve(tokens[i + 1].Value));
            if (!path.StartsWith(root + "/", StringComparison.Ordinal) || path.Length == root.Length + 1)
                throw new InvalidDataException("PVF 目录路径越过资源根: " + root);
            if (!result.TryAdd(tokens[i].Value, path))
                throw new InvalidDataException("重复 PVF 目录 ID: " + tokens[i].Value);
        }

        return result.AsReadOnly();
    }

    internal static DungeonDefinition ParseResource(int id, string path, IReadOnlyList<PvfToken> tokens, Func<int, string> resolve, IReadOnlyDictionary<int, string> mapPaths, Func<string, bool> exists)
    {
        if (id < 0)
            throw new InvalidDataException("副本索引不能为负");
        var sections = Sections(tokens, resolve);
        int? minimum = Scalar("minimum required level"), basis = Scalar("basis level");
        var mazes = new List<DungeonMaze>();
        Dictionary<string, DungeonRawSection>? current = null;
        var mazeSections = new List<DungeonRawSection>();
        bool mazeClosed = false;
        for (int i = 0; i < sections.Count; i++)
        {
            var section = sections[i];
            if (section.Tag == "beast dungeon")
            {
                if (section.Tokens.Count != 0)
                    throw new InvalidDataException("beast dungeon 标记带有未知参数");
                // 魔兽模式另有尺寸、地图种类和无普通Boss的布局，不归前一条普通迷宫。
                // 原始区块仍完整保留；这里只结束普通路线，不启用魔兽玩法。
                Finish();
                continue;
            }
            if (section.Tag == "maze info")
            {
                if (section.Tokens.Count != 0)
                    throw new InvalidDataException("maze info 标记带有未知参数");
                Finish();
                current = new(StringComparer.Ordinal);
                mazeSections.Clear();
                mazeClosed = false;
                continue;
            }

            if (section.Tag == "/maze info")
            {
                // 尾随入口和传送规则仍归本路线，布局字段只在maze块内读取。
                mazeClosed = true;
                continue;
            }

            if (current is null) continue;
            mazeSections.Add(section);
            if (mazeClosed || section.Tag.StartsWith('/'))
                continue;
            if (section.Tag is not ("size" or "greed" or "map specification" or "start map" or "boss map" or "quest connection"))
                continue;
            if (!current.TryAdd(section.Tag, section))
                throw new InvalidDataException("重复 maze 字段: " + section.Tag);
            if (section.Tag is "map specification" or "start map" or "boss map")
            {
                if (i + 1 >= sections.Count || sections[i + 1].Tag != "/" + section.Tag || sections[i + 1].Tokens.Count != 0)
                    throw new InvalidDataException("maze 块未正常结束: " + section.Tag);
            }
        }

        Finish();
        if (mazes.Count == 0)
            throw new NotSupportedException("unsupported: 未发现 maze info 资源结构");
        return new(id, path, minimum, basis, Freeze(mazes), Freeze(sections))
        {
            RoomMoveKeepState = Flag("room move keep state"),
            IndividualMapMovement = Flag("individual map movement")
        };

        bool Flag(string tag) => sections.LastOrDefault(section => section.Tag == tag)?.Tokens is [{ Type: 0, Value: 1 }];

        int? Scalar(string tag)
        {
            var matches = sections.Where(s => s.Tag == tag).ToArray();
            if (matches.Length == 0)
                return null;
            if (matches.Length != 1 || matches[0].Tokens.Count != 1 || matches[0].Tokens[0].Type != 0 || matches[0].Tokens[0].Value < 0)
                throw new InvalidDataException("副本等级字段无效或重复: " + tag);
            return matches[0].Tokens[0].Value;
        }

        void Finish()
        {
            if (current is null)
                return;
            try { mazes.Add(ParseMaze(mazes.Count, current, mazeSections, resolve, mapPaths, exists)); }
            catch (Exception error) when (error is InvalidDataException or NotSupportedException)
            {
                // 每条路线独立装载；未使用的旧路线不能阻断同副本的有效剧情路线。
                var connection = current.TryGetValue("quest connection", out var section) ? section.Tokens : Array.Empty<PvfToken>();
                mazes.Add(new(mazes.Count,0,0,"",[],[],null,connection) { ResourceError = error.Message });
            }
            current = null;
        }
    }

    private static DungeonMaze ParseMaze(int ordinal, IReadOnlyDictionary<string, DungeonRawSection> sections, IReadOnlyList<DungeonRawSection> mazeSections,
        Func<int, string> resolve, IReadOnlyDictionary<int, string> mapPaths, Func<string, bool> exists)
    {
        var size = Integers(Required("size"));
        if (size.Length != 2 || size[0] <= 0 || size[1] <= 0)
            throw new InvalidDataException("maze size 必须是两个正整数");
        int width = size[0], height = size[1];
        var greedTokens = Required("greed");
        if (greedTokens.Count != 1 || greedTokens[0].Type != 6)
            throw new NotSupportedException("unsupported: greed 不是单个字符串");
        string greed = resolve(greedTokens[0].Value);
        // 保留原文；类型化GreedGrid独立返回不支持原因，未知编码不使整个副本定义失败。
        var starts = Points(Required("start map"));
        var bosses = Points(Required("boss map"),allowRepeated: true);
        IReadOnlyList<DungeonMap>? specification = null;
        var layered = new List<DungeonMap>();
        if (sections.TryGetValue("map specification", out var mapSection))
        {
            var values = mapSection.Tokens;
            if (values.Count == 0)
                throw new InvalidDataException("map specification 记录不完整");
            var items = new List<DungeonMap>();
            for (int i = 0; i < values.Count;)
            {
                if (values[i].Type != 6 || i + 3 >= values.Count)
                    throw new NotSupportedException("unsupported: map specification 的类型标记未实现");
                string kind = resolve(values[i].Value);
                if (kind is not ("map" or "boss" or "layered"))
                    throw new NotSupportedException("unsupported: map specification 类型 " + kind);
                int[] numbers = Integers(values.Skip(i + 1).Take(3).ToArray());
                var position = new DungeonPoint(numbers[0], numbers[1]);
                ValidatePoint(position);
                i += 4;
                var ids = new List<int> { numbers[2] };
                while (i < values.Count && values[i].Type == 0)
                {
                    if (values[i].Value < 0) throw new InvalidDataException("地图候选编号无效");
                    ids.Add(values[i++].Value);
                }
                if (numbers[2] < 0) throw new InvalidDataException("地图候选编号无效");
                string mapPath = mapPaths.GetValueOrDefault(numbers[2]) ?? "";
                if (ids.Count == 1 && (mapPath.Length == 0 || !exists(mapPath)))
                    throw new InvalidDataException("map specification 引用的地图不存在: " + numbers[2]);
                var map = new DungeonMap(kind,position,numbers[2],mapPath) { CandidateIds = ids.AsReadOnly() };
                if (kind == "layered") layered.Add(map); else items.Add(map);
            }

            // 普通迷宫可只声明部分房间，其余由地图池生成；完整性在所选路线规划时检查。
            specification = Freeze(items);
        }

        var quest = sections.TryGetValue("quest connection", out var connection) ? connection.Tokens : Freeze(Array.Empty<PvfToken>());
        if (connection is not null && quest.Count == 0)
            throw new InvalidDataException("quest connection 不能为空");
        var stages = new List<DungeonMap>();
        if (bosses.Count >= 2 && bosses.Distinct().Count() != bosses.Count && quest.Count != 0 && specification is not null)
        {
            var byPosition = specification.GroupBy(map => map.Position).ToDictionary(group => group.Key,group => new Queue<DungeonMap>(group));
            var stageMaps = specification.ToList();
            foreach (var point in bosses)
            {
                if (!byPosition.TryGetValue(point,out var candidates) || candidates.Count == 0)
                    throw new InvalidDataException("剧情阶段缺少对应的有序地图声明");
                var original = candidates.Dequeue();
                var stage = original with { StoryStageIndex = stages.Count };
                if (stages.Count == bosses.Count - 1) stage = stage with { Kind = "boss" };
                stageMaps[stageMaps.IndexOf(original)] = stage;
                stages.Add(stage);
            }
            if (stages.Zip(stages.Skip(1)).Any(pair =>
                Math.Abs(pair.First.Position.X-pair.Second.Position.X)+Math.Abs(pair.First.Position.Y-pair.Second.Position.Y) != 1))
                throw new InvalidDataException("剧情阶段连续坐标不合法");
            var bases = new List<DungeonMap>();
            foreach (var group in stageMaps.GroupBy(map => map.Position))
            {
                var roomStages = group.Where(map => map.StoryStageIndex >= 0).ToArray();
                if (roomStages.Length == 0) { bases.AddRange(group); continue; }
                var stageIds = roomStages.Select(map => map.Id).ToHashSet();
                var candidates = group.SelectMany(map => map.CandidateIds.Count == 0 ? new[] { map.Id } : map.CandidateIds)
                    .Where(id => !stageIds.Contains(id)).Distinct().ToArray();
                // 没有额外候选时也建立普通基础身份，入场不能直接消费阶段0。
                if (candidates.Length == 0) candidates = [roomStages[0].Id];
                var source = group.FirstOrDefault(map => map.StoryStageIndex < 0) ?? roomStages[0];
                bases.Add(source with { Id = candidates[0],Path = mapPaths.GetValueOrDefault(candidates[0]) ?? "",
                    StoryStageIndex = -1,CandidateIds = Freeze(candidates) });
            }
            specification = Freeze(bases.Concat(stages));
        }
        var overridden = new List<DungeonMap>();
        if (stages.Count == 0 && specification is not null)
        {
            var effective = new List<DungeonMap>();
            foreach (var group in specification.GroupBy(map => map.Position))
            {
                // Full03 0409C98C按坐标取同一格，0409C9B9先清普通map候选再追加本行。
                // 仅解释同种普通map；既有Boss剧情阶段、混合种类校验与叠层身份保持独立。
                if (group.All(map => map.Kind == "map"))
                {
                    effective.Add(group.Last());
                    overridden.AddRange(group.SkipLast(1));
                }
                else effective.AddRange(group);
            }
            specification = Freeze(effective);
        }
        if (specification is not null && specification.GroupBy(map => map.Position).Any(group => group.Count() > 1 &&
            (stages.Count == 0 || group.Count(map => map.StoryStageIndex < 0) > 1))) throw new InvalidDataException("重复 map specification 坐标");
        var entrance = DungeonMazeConditions.Entrance(mazeSections,resolve);
        var warps = DungeonMazeConditions.Warps(mazeSections);
        if (entrance is not null)
        {
            foreach (var warp in warps)
            {
                ValidatePoint(warp.Source); ValidatePoint(warp.Destination);
            }
        }
        var maze = new DungeonMaze(ordinal,width,height,greed,starts,bosses,specification,quest)
            { LayeredMaps = Freeze(layered),OverriddenMaps = Freeze(overridden),StoryStages = Freeze(stages),BossEntrance = entrance,WarpMaps = warps,
                ClearConditions = DungeonMazeConditions.Clear(mazeSections,resolve), HellParty = HellParty(), HellPartyPath = HellParty() is { } hellRoom ? mapPaths[hellRoom.MapId] : null,
            HellLegendPath = HellParty() is { LegendMapId: > 0 } legendRoom ? mapPaths[legendRoom.LegendMapId] : null };
        return maze with { ConditionalMaps = ConditionalMaps(maze) };
        // 实验性：深渊房间解析失败（缺字段/坐标越界/该坐标没有普通房间/深渊地图文件不存在）一律视为该 maze 无深渊派对，不抛错（不影响现有入场）。
        DungeonHellPartyRoom? HellParty()
        {
            var flags = mazeSections.Where(s => s.Tag == "hell dungeon").ToArray();
            var maps = mazeSections.Where(s => s.Tag == "seal door map index").ToArray();
            var positions = mazeSections.Where(s => s.Tag == "seal door pos").ToArray();
            if (quest.Count != 0 || specification is null || flags.Length != 1 || maps.Length != 1 || positions.Length != 1 ||
                flags[0].Tokens is not [{ Type: 0, Value: 1 }] || maps[0].Tokens is not [{ Type: 0, Value: > 0 } mapToken] ||
                positions[0].Tokens is not [{ Type: 0 } x, { Type: 0 } y]) return null;
            var room = new DungeonPoint(x.Value, y.Value);
            if (room.X < 0 || room.Y < 0 || room.X >= width || room.Y >= height ||
                !specification.Any(map => map.Position == room && map.Kind == "map") ||
                !mapPaths.TryGetValue(mapToken.Value, out var hellPath) || !exists(hellPath)) return null;
            int Single(string tag) => mazeSections.Where(s => s.Tag == tag).ToArray() is [{ Tokens: [{ Type: 0, Value: >= 0 } value] }] ? value.Value : 0;
            return new(mapToken.Value, room) { RequiredItemCount = Single("hell party required item count"), AppearRate = Single("seal door appear rate"),
                LegendMapId = mapPaths.TryGetValue(Single("seal door legend map index"), out var legendPath) && exists(legendPath) ? Single("seal door legend map index") : 0,
                LegendRequiredCount = Single("hell legend party required item count") };
        }
        IReadOnlyList<PvfToken> Required(string tag) => sections.TryGetValue(tag, out var value) ? value.Tokens : throw new InvalidDataException("maze 缺少字段: " + tag);
        IReadOnlyList<DungeonPoint> Points(IReadOnlyList<PvfToken> values, bool allowRepeated = false)
        {
            int[] numbers = Integers(values);
            if (numbers.Length == 0 || numbers.Length % 2 != 0)
                throw new InvalidDataException("start/boss map 坐标不完整");
            var result = new List<DungeonPoint>();
            var seen = new HashSet<DungeonPoint>();
            for (int i = 0; i < numbers.Length; i += 2)
            {
                var point = new DungeonPoint(numbers[i], numbers[i + 1]);
                ValidatePoint(point);
                if (!seen.Add(point) && !allowRepeated)
                    throw new InvalidDataException("重复 start/boss map 坐标");
                result.Add(point);
            }

            return Freeze(result);
        }

        void ValidatePoint(DungeonPoint point)
        {
            if (point.X < 0 || point.Y < 0 || point.X >= width || point.Y >= height)
                throw new InvalidDataException("maze 坐标超出 size");
        }
    }

    private static int[] Integers(IReadOnlyList<PvfToken> tokens)
    {
        if (tokens.Any(t => t.Type != 0))
            throw new NotSupportedException("unsupported: maze 数值包含未实现的 token 类型");
        return tokens.Select(t => t.Value).ToArray();
    }

    private static IReadOnlyList<DungeonRawSection> Sections(IReadOnlyList<PvfToken> tokens, Func<int, string> resolve)
    {
        var sections = new List<DungeonRawSection>();
        for (int i = 0; i < tokens.Count;)
        {
            if (tokens[i].Type != 3)
                throw new InvalidDataException("副本脚本存在无标签 token");
            string raw = resolve(tokens[i++].Value);
            if (!raw.StartsWith('[') || !raw.EndsWith(']') || raw.Length < 3)
                throw new InvalidDataException("副本脚本标签无效");
            string tag = raw[1..^1].Trim().ToLowerInvariant();
            int start = i;
            while (i < tokens.Count && tokens[i].Type != 3)
                i++;
            sections.Add(new(tag, Freeze(tokens.Skip(start).Take(i - start))));
        }

        return Freeze(sections);
    }

    private static IReadOnlyList<T> Freeze<T>(IEnumerable<T> values) => Array.AsReadOnly(values.ToArray());
}
