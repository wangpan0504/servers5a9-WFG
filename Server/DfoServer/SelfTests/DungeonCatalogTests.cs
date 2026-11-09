using static DfoServer.Testing.TestCheck;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Dungeons;
using System.Collections;
using System.Text.Json;
using PvfLib;

namespace DfoServer;

internal static class DungeonCatalogTests
{
    static void VerifyResultBoardSuppression()
    {
        var start = new DungeonRawSection("dungeon clear result", []);
        var end = new DungeonRawSection("/dungeon clear result", []);
        var board = new DungeonRawSection("result board", [new(0, 0)]);
        var reward = new DungeonRawSection("reward card", [new(0, 0)]);
        var dungeon = new DungeonDefinition(3, "dungeon/test.dgn", 1, 1, [], [start, reward, board, end]);
        That(dungeon.HasNoResultBoard, "完整结果块内显式result board=0保留无结果页事实");
        foreach (var sections in new IReadOnlyList<DungeonRawSection>[]
        {
            [], [board], [start, board], [board, start, end], [end, board, start],
            [start, board, board, end], [start, board, end, start, end],
            [start with { Tokens = [new(0, 0)] }, board, end],
            [start, board, end with { Tokens = [new(0, 0)] }],
            [start, board with { Tokens = [new(0, 1)] }, end],
            [start, board with { Tokens = [new(2, 0)] }, end],
            [start, board with { Tokens = [new(0, 0), new(0, 0)] }, end],
            [start, new("unknown result option", []), board, end]
        })
            That(!(dungeon with { RawSections = sections }).HasNoResultBoard,
                "缺省、块外、重复、未闭合或未知结果声明不推导禁用结果页");
        var first = new DungeonMap("map", new(0, 0), 1, "map/start.map");
        var last = new DungeonMap("boss", new(1, 0), 2, "map/boss.map");
        var maze = new DungeonMaze(0, 2, 1, "BB EE", [first.Position], [last.Position], [first, last], []);
        var role = new Character(1, 0, "ResultBoard", 0, new(1, 0, 1, 1), null) { Level = 95 };
        foreach (var (id, weight, disable, suppressed) in new[]
        {
            (400000097, 0, true, true), (400000098, 0, true, true), (3, 0, true, false),
            (400000097, 1, true, false), (400000097, 0, false, false)
        })
        {
            var source = dungeon with { Id = id, Mazes = [maze], RawSections =
                [start, reward, board, end, new("experience increasing point", [new(0, weight)]),
                    ..disable ? new[] { new DungeonRawSection("disable clear reward", []) } : []] };
            That(DungeonEntryPlan.TryCreate(source, role, (uint)id, 0, 0, out var entry, out var reason) &&
                entry!.SuppressOrdinaryResultBoard == suppressed,
                "仅两张星河图的无结果页/禁奖励/零权重完整合同抑制普通结算，不扩大到其它模式：" + reason);
        }
        var originalPath = EpicRoadResourcePaths.Dungeons[400000097];
        var flags = new[] { new DungeonRawSection("experience increasing point", [new(0, 0)]), new("disable clear reward", []) };
        foreach (var (path, declaration, expected) in new[]
        {
            (originalPath, Array.Empty<DungeonRawSection>(), true),
            ("dungeon/another.dgn", Array.Empty<DungeonRawSection>(), false),
            (originalPath, new[] { start, board with { Tokens = [new(0, 1)] }, end }, false),
            (originalPath, new[] { start, board }, false),
            (originalPath, new[] { board }, false),
            (originalPath, new[] { start, board, board, end }, false)
        })
        {
            byte difficulty = path == originalPath ? (byte)2 : (byte)0;
            var source = dungeon with { Id = 400000097, Path = path, Mazes = [maze],
                RawSections = [..declaration, ..flags, new("difficulty", [new(0, difficulty)])] };
            That(DungeonEntryPlan.TryCreate(source, role, 400000097, 0, difficulty, out var entry, out _) &&
                entry!.SuppressOrdinaryResultBoard == expected,
                "具名原图只恢复完全缺失的原结果块，不覆盖显式或畸形声明");
        }
    }
    static void VerifyChampionRates()
    {
        var row = new DungeonRawSection("champion", [new(0, 4), new(0, 33), new(0, 50), new(0, 70), new(0, 80)]);
        var ordinary = new DungeonDefinition(3, "dungeon/act1/mirkwood.dgn", 1, 1, [], [row]);
        That(DungeonChampionRates.For(ordinary) == new DungeonChampionRates(33, 50, 70, 80),
            "普通图直接使用PVF四档百分比，不再只放行断头谷");
        foreach (var sections in new IReadOnlyList<DungeonRawSection>[]
        {
            [], [row, row], [row with { Tokens = [new(0, 4), new(0, 33)] }],
            [row with { Tokens = [new(0, 4), new(0, 33), new(0, 50), new(0, 70), new(0, 101)] }],
            [row with { Tokens = [new(0, 4), new(0, 33), new(0, 50), new(0, -1), new(0, 80)] }],
            [row with { Tokens = [new(0, 4), new(7, 33), new(0, 50), new(0, 70), new(0, 80)] }],
            [row with { Tokens = [new(0, 0), new(0, 0), new(0, 0), new(0, 0), new(0, 0)] }],
            [row, new("raid dungeon", [new(0, 1)])], [row, new("raid normal dungeon", [])],
            [row, new("tutorial dungeon", [new(0, 1)])], [row, new("blood dungeon", [new(0, 1)])],
            [row, new("special dungeon", [])], [row, new("tournament dungeon", [new(0, 1)])]
        })
            That(!DungeonChampionRates.For(ordinary with { RawSections = sections }).IsEnabled,
                "缺失、重复、畸形、全零或特殊玩法声明不借普通精英策略生成演员");
        That(!DungeonChampionRates.For(ordinary with { DungeonType = "assaultmode dungeon" }).IsEnabled &&
            !DungeonChampionRates.For(ordinary with { Path = "dungeon/towers/test.dgn" }).IsEnabled,
            "非普通类型与塔类不改变既有演员合同");
    }

    static void VerifyRetiredSectionIsolation()
    {
        var fixture = new Fixture();
        // 复现真实缺登记的59453；普通迷宫使用独立且完整的地图，不能靠换图放行魔兽。
        var tokens = fixture.Maze().Concat((PvfToken[])
        [
            ..fixture.Tag("Beast dungeon"),
            ..fixture.Tag("BEAST LEVEL",fixture.Int(90),fixture.Int(120)),
            ..fixture.Tag("size",fixture.Int(5),fixture.Int(3)),
            ..fixture.Tag("greed",fixture.String("JJNNNNNNMMLLPPPPPPOODDHHHHHHGG")),
            ..fixture.Tag("map specification",fixture.String("map"),fixture.Int(0),fixture.Int(0),fixture.Int(59453)),
            ..fixture.Tag("/map specification"),
            ..fixture.Tag("start map",fixture.Int(0),fixture.Int(0)),
            ..fixture.Tag("/start map"),
            ..fixture.Tag("boss map",fixture.Int(-1),fixture.Int(-1)),
            ..fixture.Tag("/boss map")
        ]).ToArray();
        DungeonDefinition? definition = null;
        Exception? parseError = null;
        try
        {
            definition = DungeonCatalog.ParseResource(310,"dungeon/optional-beast.dgn",tokens,fixture.Resolve,
                new Dictionary<int,string> { [1] = "map/ordinary.map" },_ => true);
        }
        catch (Exception error) when (error is InvalidDataException or NotSupportedException)
        {
            parseError = error;
        }
        That(parseError is null && definition is not null,
            "可选魔兽缺59453登记时仍须读取普通副本定义：" + parseError?.Message);
        var ready = definition! with
        {
            MinimumLevel = 90, BasisLevel = 90,
            RawSections = [..definition!.RawSections,new("difficulty level",[new(0,1),new(0,1),new(0,1),new(0,1),new(0,1)])]
        };
        var character = new Character(1,0,"OptionalBeast",1,new(1,0,1,1,0),null) { Level = 90, CompletedQuests = [5927] };
        That(ready.Mazes is [{ ResourceError: null, MapSpecification: [{ Kind: "boss", Id: 1 }] }] &&
            ready.RawSections.Count(section => section.Tag == "beast dungeon") == 1 &&
            DungeonEntryPlan.TryCreate(ready,character,310,0,0,out _,out _),
            "退役扩展块缺登记不阻断原普通路线入场");

    }

    public static void RunCore()
    {
        VerifySupplementalDirectories();
        VerifyDisjointDropRarity();
        VerifyRetiredSectionIsolation();
        DungeonEntryPlanTests.RunCore();
        int checks = Count;
        VerifyChampionRates();
        VerifyResultBoardSuppression();
        var size = new DungeonRawSection("difficulty size",[new(0,1)]);
        var start = new DungeonRawSection("story mode",[]);
        var end = new DungeonRawSection("/story mode",[]);
        var designated = new DungeonRawSection("designate dungeon difficulty",[new(0,2)]);
        var levels = new DungeonRawSection("difficulty level",[new(0,1),new(0,1),new(0,1),new(0,1),new(0,1)]);
        var mixed = Definition(start,size,end,designated,levels);
        That(mixed.AllowsDifficulty(0,true) && !mixed.AllowsDifficulty(1,true) && !mixed.AllowsDifficulty(2,true) &&
            mixed.AllowsDifficulty(2,false) && !mixed.AllowsDifficulty(0,false),
            "故事零难度与普通指定二难度各自校验，不能互相覆盖");
        var storyThree = Definition(start,size with { Tokens = [new(0,3)] },end,designated,
            levels with { Tokens = [new(0,0),new(0,0),new(0,1),new(0,0),new(0,0)] });
        That(Enumerable.Range(0,5).All(value => storyThree.AllowsDifficulty((byte)value,true) == (value < 3)) &&
            storyThree.AllowsDifficulty(2,false) && !storyThree.AllowsDifficulty(byte.MaxValue,true),
            "故事范围保留全部声明难度，普通难度表和协议上限仍有效");
        var withoutStory = Definition(designated,levels);
        var firstQuests = new DungeonRawSection("quest list",[new(0,6206),new(0,6214)]);
        var secondQuests = new DungeonRawSection("quest list",[new(0,6223)]);
        var questEnd = new DungeonRawSection("/quest list",[]);
        var multiple = Definition(start,size,firstQuests,questEnd,end,
            start,size with { Tokens = [new(0,3)] },secondQuests,questEnd,end,designated,levels);
        That(Enumerable.Range(0,5).All(value => multiple.AllowsDifficulty((byte)value,true,6206) == (value == 0)) &&
            Enumerable.Range(0,5).All(value => multiple.AllowsDifficulty((byte)value,true,6214) == (value == 0)) &&
            Enumerable.Range(0,5).All(value => multiple.AllowsDifficulty((byte)value,true,6223) == (value < 3)),
            "互斥任务故事块分别使用本块难度，不能泄漏另一块范围");
        That(!multiple.AllowsDifficulty(0,true) && !multiple.AllowsDifficulty(0,true,9999) &&
            multiple.AllowsDifficulty(2,false,6206) && !multiple.AllowsDifficulty(0,false,6206),
            "多故事块必须有唯一连接归属，普通路线仍使用普通难度");
        foreach (var invalid in new[]
        {
            Definition(start,firstQuests,size,questEnd,end,start,size,secondQuests,questEnd,end),
            Definition(start,size,firstQuests,new("unknown scope",[]),questEnd,end,start,size,secondQuests,questEnd,end),
            Definition(start,size,firstQuests,questEnd,end,start,secondQuests,size,questEnd,end),
            Definition(start,size,firstQuests,questEnd,end,start,size,secondQuests,new("unknown scope",[]),questEnd,end),
            Definition(start,size,firstQuests,questEnd,end,start,size,firstQuests,questEnd,end),
            Definition(start,size,firstQuests,questEnd,end,start,size,secondQuests with { Tokens = [new(7,6223)] },questEnd,end),
            Definition(start,size,firstQuests,questEnd,end,start,size,secondQuests with { Tokens = [] },questEnd,end),
            Definition(start,size,firstQuests,questEnd,end,start,size,secondQuests with { Tokens = [new(0,0)] },questEnd,end),
            Definition(start,size,firstQuests,questEnd,end,start,size,size,secondQuests,questEnd,end),
            Definition(start,size,firstQuests,questEnd,end,start,size with { Tokens = [new(0,6)] },secondQuests,questEnd,end),
            Definition(start,size,firstQuests,questEnd,end,start,size,secondQuests,secondQuests,questEnd,end),
            Definition(start,size,firstQuests,questEnd,end,start,size,secondQuests,end),
            Definition(start,size,firstQuests,questEnd,end,start,size,questEnd,secondQuests,end),
            Definition(start,size,firstQuests,questEnd,end,start with { Tokens = [new(0,1)] },size,secondQuests,questEnd,end),
            Definition(start,size,firstQuests,questEnd,start,size,secondQuests,questEnd,end,end),
            Definition(start,size,firstQuests,questEnd,end,start,size,secondQuests,questEnd)
        })
            That(!invalid.AllowsDifficulty(0,true,6206),"任一故事块归属、边界或难度不明确时拒绝，不能只验证已选块");
        That(Definition(start,size,secondQuests with { Tokens = [new(7,1)] },questEnd,end).AllowsDifficulty(0,true,6206),
            "单故事块保留既有不解释任务引用token的兼容合同");
        var boundedStory = Definition(start,size,firstQuests,questEnd,end,levels);
        That(Enumerable.Range(0,5).All(value => boundedStory.AllowsDifficulty((byte)value,true,6206) == (value == 0)) &&
            Enumerable.Range(0,5).All(value => boundedStory.AllowsDifficulty((byte)value,true,7930)),
            "单故事块的明确名册只约束所属任务，其他任务沿普通难度表");
        That(!boundedStory.AllowsDifficulty(1,true) && !boundedStory.AllowsDifficulty(5,true,7930) &&
            !Definition(start,size,firstQuests,questEnd,end,designated,levels).AllowsDifficulty(1,true,7930) &&
            Definition(start,size,firstQuests,questEnd,end,designated,levels).AllowsDifficulty(2,true,7930),
            "缺少任务身份不猜归属，非故事任务仍复核普通指定难度及协议上限");
        foreach (var malformed in new[]
        {
            Definition(start,size,firstQuests,end,levels),
            Definition(start,size,firstQuests,firstQuests,questEnd,end,levels),
            Definition(start,size,firstQuests, new("unknown",[]),questEnd,end,levels),
            Definition(start,size,firstQuests with { Tokens = [new(0,6206),new(0,6206)] },questEnd,end,levels),
            Definition(start,size,firstQuests with { Tokens = [new(7,6206)] },questEnd,end,levels)
        })
            That(!malformed.AllowsDifficulty(1,true,7930),"损坏或未知名册不能作为放行普通高难度的证据");
        That(Enumerable.Range(0,5).All(value => withoutStory.AllowsDifficulty((byte)value,true) == withoutStory.AllowsDifficulty((byte)value,false)),
            "未声明故事块的任务连接仍沿既有普通难度合同");
        foreach (var invalid in new[] { Definition(start,size),Definition(end),Definition(end,start,size,end),Definition(start,size,end,start,size,end),
            Definition(start,size,size,end),Definition(start,end),Definition(start with { Tokens = [new(0,1)] },size,end),
            Definition(start,size with { Tokens = [new(0,0)] },end),Definition(start,size with { Tokens = [new(0,6)] },end),
            Definition(start,size with { Tokens = [new(7,1)] },end) })
            That(!invalid.AllowsDifficulty(0,true),"不完整、重复或未知故事难度不能放行");
        That(!Definition(designated,designated,levels).AllowsDifficulty(2,false) &&
            !Definition(designated,levels,levels with { Tokens = [new(0,0),new(0,0),new(0,0),new(0,0),new(0,0)] }).AllowsDifficulty(2,false) &&
            Definition(designated).AllowsDifficulty(2,false),
            "普通重复指定与冲突难度表仍拒绝，唯一指定难度无需另写difficulty");
        foreach (int value in Enumerable.Range(0,5))
        {
            var fixedOnly = Definition(designated with { Tokens = [new(0,value)] });
            That(Enumerable.Range(0,5).All(request => fixedOnly.AllowsDifficulty((byte)request,false) == (request == value)),
                "仅声明指定难度时只接受指定值");
            foreach (var invalidPlain in new[] { new DungeonRawSection("difficulty",[new(0,(value+1)%5)]),
                new DungeonRawSection("difficulty",[new(7,value)]),new DungeonRawSection("difficulty",[]) })
                That(!Definition(designated with { Tokens = [new(0,value)] },invalidPlain).AllowsDifficulty((byte)value,false),
                    "同时出现的未知或冲突difficulty仍拒绝");
        }
        var riftDifficulty = Definition(new DungeonRawSection("difficulty",[new(0,3)]), designated with { Tokens = [new(0,3)] });
        That(Enumerable.Range(0,5).All(value => riftDifficulty.AllowsDifficulty((byte)value,false) == (value == 3)),
            "difficulty 与 designate dungeon difficulty 同为 3 且没有难度表时只放行难度 3");
        var repeatedLock = Definition(new DungeonRawSection("difficulty",[new(0,2)]), designated, designated);
        That(Enumerable.Range(0,5).All(value => repeatedLock.AllowsDifficulty((byte)value,false) == (value == 2)),
            "相同指定难度重复书写且没有难度表时只放行该难度");
        var conflictingLock = Definition(new DungeonRawSection("difficulty",[new(0,2)]), designated, designated with { Tokens = [new(0,1)] });
        That(Enumerable.Range(0,5).All(value => !conflictingLock.AllowsDifficulty((byte)value,false)),
            "互相冲突的重复指定难度仍然全部拒绝");
        That(Enumerable.Range(0,5).All(value => !Definition(designated, designated, levels).AllowsDifficulty((byte)value,false)),
            "重复指定难度旁边还有难度表时不把它们折成一条");
        var fixture = new Fixture();
        var beastTokens = new List<PvfToken>();
        for (int variant = 0; variant < 3; variant++)
        {
            beastTokens.AddRange(fixture.Tag("Beast dungeon"));
            beastTokens.AddRange(fixture.Tag("BEAST LEVEL",fixture.Int(90),fixture.Int(120)));
            beastTokens.AddRange(fixture.Tag("size",fixture.Int(5),fixture.Int(3)));
            beastTokens.AddRange(fixture.Tag("greed",fixture.String("JJNNNNNNMMLLPPPPPPOODDHHHHHHGG")));
            beastTokens.AddRange(fixture.Tag("map specification",fixture.String("Beast"),fixture.Int(variant),fixture.Int(0),fixture.Int(59453 + variant)));
            beastTokens.AddRange(fixture.Tag("/map specification"));
            beastTokens.AddRange(fixture.Tag("start map",fixture.Int(variant),fixture.Int(0)));
            beastTokens.AddRange(fixture.Tag("/start map"));
            beastTokens.AddRange(fixture.Tag("boss map",fixture.Int(-1),fixture.Int(-1)));
            beastTokens.AddRange(fixture.Tag("/boss map"));
        }
        var modes = fixture.Parse(fixture.Maze().Concat(fixture.Maze())
            .Concat(beastTokens).Concat(fixture.Maze()).ToArray());
        That(modes.Mazes.Count == 3 && modes.Mazes.Select(maze => maze.SourceOrdinal).SequenceEqual([0,1,2]) &&
            modes.Mazes.All(maze => maze.ResourceError is null && maze.Width == 1 && maze.Height == 1 &&
                maze.Bosses.SequenceEqual([new DungeonPoint(0,0)]) && maze.MapSpecification is [{ Kind: "boss",Id: 1 }]),
            "独立魔兽模式不污染前一条普通迷宫，后续普通maze仍按源序解析");
        That(modes.RawSections.Count(section => section.Tag == "beast dungeon") == 3 &&
            modes.RawSections.Where(section => section.Tag == "size").Select(section => section.Tokens[0].Value).SequenceEqual([1,1,5,5,5,1]) &&
            modes.RawSections.Count(section => section.Tag == "boss map" && section.Tokens.SequenceEqual([fixture.Int(-1),fixture.Int(-1)])) == 3 &&
            modes.RawSections.Where(section => section.Tag == "map specification" && fixture.Resolve(section.Tokens[0].Value) == "Beast")
                .Select(section => section.Tokens[3].Value).SequenceEqual([59453,59454,59455]),
            "魔兽重复布局、原地图类型与负Boss坐标完整保留在RawSections，不改写成普通路线");
        Reject<InvalidDataException>(() => fixture.Parse(fixture.Maze()
            .Concat(fixture.Tag("size",fixture.Int(1),fixture.Int(1))).ToArray()),"同一普通迷宫重复size仍拒绝");
        var scalar = fixture.Maze();
        int scalarStart = Array.FindIndex(scalar, token => token.Type == 3 && fixture.Resolve(token.Value) == "[start map]");
        var scalarDeclaration = fixture.Parse(scalar.Take(scalarStart).Concat(fixture.Tag("beast dungeon",fixture.Int(1)))
            .Concat(scalar.Skip(scalarStart)).ToArray());
        That(scalarDeclaration.Mazes is [{ ResourceError: null, Starts.Count: 1, Bosses.Count: 1 }],
            "旧标量标签不再启用玩法，也不能截断其后普通迷宫起点和Boss字段");
        Reject<InvalidDataException>(() => fixture.Parse(fixture.Maze()
            .Concat(fixture.Tag("beast dungeon",fixture.Int(1),fixture.Int(2))).ToArray()),
            "退役布局边界不接受双整数参数，不能借退役忽略格式错误");
        Reject<InvalidDataException>(() => fixture.Parse(fixture.Maze()
            .Concat(fixture.Tag("beast dungeon",fixture.String("invalid"))).ToArray()),
            "退役布局边界不接受字符串参数，普通路线仍严格校验");
        // 三条普通候选仅验证解析隔离，不能绕过规划器既有的歧义拒绝。
        var ambiguousDefinition = modes with { Id = 310, MinimumLevel = 1, BasisLevel = 1,
            RawSections = [..modes.RawSections,new("difficulty level",[new(0,1),new(0,1),new(0,1),new(0,1),new(0,1)])] };
        var character = new Character(1,0,"RetiredEntry",1,new(1,0,1,1,0),null) { Level = 90, CompletedQuests = [5927] };
        That(!DungeonEntryPlan.TryCreate(ambiguousDefinition,character,310,0,4,out _,out var ambiguousReason) &&
            ambiguousReason == "route-maze-ambiguous", "退役区块不改变多条普通候选的歧义拒绝");
        // 单条普通路线携带同样的旧扩展区块，普通首档与第五档仍可规划。
        var ordinarySource = fixture.Parse(fixture.Maze().Concat(beastTokens).ToArray());
        That(ordinarySource.Mazes.Count == 1 && ordinarySource.RawSections.Count(section => section.Tag == "beast dungeon") == 3,
            "普通入场夹具保留一条普通路线及全部退役原始布局");
        var ordinaryDefinition = ordinarySource with { Id = 310, MinimumLevel = 1, BasisLevel = 1,
            RawSections = [..ordinarySource.RawSections,new("difficulty level",[new(0,1),new(0,1),new(0,1),new(0,1),new(0,1)])] };
        foreach (byte difficulty in new byte[] { 0,4 })
            That(DungeonEntryPlan.TryCreate(ordinaryDefinition,character,310,0,difficulty,out _,out _),
                "原310普通难度不受退役扩展区块影响：" + difficulty);
        foreach (uint id in Enumerable.Range(5335,5).Select(id => (uint)id))
            That(!DungeonEntryPlan.TryCreate(ordinaryDefinition with { Id = (int)id },character,id,0,2,out _,out var reason) &&
                reason == "dungeon-mode-unsupported", "旧专属入口即使资源存在也拒绝：" + id);
        Reject<InvalidDataException>(() => fixture.Parse(fixture.Maze().Concat(fixture.Tag("unknown dungeon"))
            .Concat(fixture.Tag("size",fixture.Int(5),fixture.Int(3))).ToArray()),"未知标记不能用作放宽重复布局的边界");
        VerifyOrdinaryMapOverrides();
        VerifyMapBlocks();
        VerifyGiveupPenaltyFlags();
        Console.WriteLine($"PASS {Count-checks} 无资源副本难度、作用域及退图豁免断言");

        static DungeonDefinition Definition(params DungeonRawSection[] sections) => new(17,"dungeon/difficulty-fixture.dgn",1,1,[],sections);
    }

    static void VerifySupplementalDirectories()
    {
        var fixture = new Fixture();
        foreach (var (root, extension) in new[] { ("dungeon", "dgn"), ("map", "map") })
        {
            PvfToken[] tokens = [fixture.Int(3), fixture.String($"ordinary/existing.{extension}"),
                fixture.Int(4), fixture.String($"ordinary/unrelated.{extension}")];
            string existing = $"{root}/ordinary/existing.{extension}";
            var baseline = DungeonCatalog.ParseDirectory(tokens, fixture.Resolve, root);
            var empty = DungeonCatalog.ParseDirectory(tokens, fixture.Resolve, root, new Dictionary<int, string>());
            That(empty.Count == baseline.Count && baseline.All(row => empty.GetValueOrDefault(row.Key) == row.Value),
                $"{root}空补充表保留原目录全部登记");

            var supplemental = new Dictionary<int, string>
            {
                [3] = existing.ToUpperInvariant().Replace('/', '\\'),
                [5] = $"{root.ToUpperInvariant()}\\event\\sub\\..\\extra.{extension}"
            };
            var merged = DungeonCatalog.ParseDirectory(tokens, fixture.Resolve, root, supplemental);
            That(merged.Count == 3 && merged[3] == baseline[3] && merged[4] == baseline[4] &&
                merged.GetValueOrDefault(5) == $"{root}/event/extra.{extension}" && !merged.ContainsKey(6),
                $"{root}同路径原登记优先，补充路径沿既有规则归一化且不改变无关项或制造未知登记");
            That(baseline.Count == 2 && !baseline.ContainsKey(5) && supplemental[5].Contains('\\'),
                $"{root}装配不修改原目录快照或调用方补充表");

            Reject<InvalidDataException>(() => DungeonCatalog.ParseDirectory(tokens, fixture.Resolve, root,
                new Dictionary<int, string> { [3] = $"{root}/event/conflict.{extension}" }),
                $"{root}同ID不同路径拒绝装配，不能覆盖原登记");
            foreach (var (id, path) in new[]
            {
                (-1, $"{root}/event/extra.{extension}"),
                (5, $"event/extra.{extension}"),
                (5, $"{(root == "dungeon" ? "map" : "dungeon")}/event/extra.{extension}"),
                (5, $"{root}/../other/extra.{extension}"),
                (5, root + "/"),
                (5, "")
            })
                Reject<InvalidDataException>(() => DungeonCatalog.ParseDirectory(tokens, fixture.Resolve, root,
                    new Dictionary<int, string> { [id] = path }),
                    $"{root}补充登记拒绝负编号、未限定资源根、越界或空叶路径");
        }
    }

    static void VerifyDisjointDropRarity()
    {
        var fixture = new Fixture();
        var declaration = fixture.Tag("no disjoint drop rarity", fixture.String("epic"));
        var end = fixture.Tag("/no disjoint drop rarity");
        var definition = fixture.Parse([..fixture.Tag("minimum required level", fixture.Int(95)),
            ..fixture.Maze(), ..declaration, ..end]);
        That(definition.DisjointForbiddenDropRarities.SetEquals([4]),
            "星河裂缝DGN的no disjoint drop rarity epic解析成冻结稀有度4");
        var character = new Character(1, 0, "DropRestriction", 1, new(1, 0, 1, 1, 0), null) { Level = 95 };
        That(DungeonEntryPlan.TryCreate(definition, character, 3, 0, 0, out var entry, out var reason) &&
            entry!.DisjointForbiddenDropRarities.SetEquals([4]),
            "正式准入冻结原副本的掉落限制，不靠地图编号硬编码活动来源：" + reason);
        foreach (var source in EpicRoadResourcePaths.Dungeons)
        {
            var activity = definition with
            {
                Id = source.Key,
                Path = source.Value,
                RawSections = [..definition.RawSections,
                    new("difficulty level", [new(0, 1), new(0, 1), new(0, 1), new(0, 1), new(0, 1)])],
                DisjointForbiddenDropRarities = definition.DisjointForbiddenDropRarities.Add(3).Add(5)
            };
            That(DungeonEntryPlan.TryCreate(activity, character, (uint)source.Key, 0, 0, out var unlocked, out reason) &&
                unlocked!.DisjointForbiddenDropRarities.SetEquals([3, 5]) &&
                activity.DisjointForbiddenDropRarities.SetEquals([3, 4, 5]),
                "本服两图仅解除史诗来源限制，保留其它稀有度及原始资源集合：" + reason);
            foreach (var control in new[]
            {
                activity with { Path = "dungeon/other.dgn" },
                activity with { Path = EpicRoadResourcePaths.Dungeons.Single(row => row.Key != source.Key).Value },
                activity with { Id = 3 }
            })
                That(DungeonEntryPlan.TryCreate(control, character, (uint)control.Id, 0, 0, out var restricted, out reason) &&
                    restricted!.DisjointForbiddenDropRarities.SetEquals([3, 4, 5]),
                    "其它编号、其它路径及两图交叉配对不能解除来源限制：" + reason);
        }
        That(fixture.Parse(fixture.Maze()).DisjointForbiddenDropRarities.IsEmpty,
            "未声明的普通副本不附加禁止分解规则");
        foreach (var invalid in new PvfToken[][]
        {
            [..fixture.Tag("no disjoint drop rarity"), ..end],
            [..fixture.Tag("no disjoint drop rarity", fixture.String("unknown")), ..end],
            [..fixture.Tag("no disjoint drop rarity", fixture.Int(4)), ..end],
            declaration,
            [..declaration, ..end, ..declaration, ..end]
        })
            Reject<InvalidDataException>(() => fixture.Parse([..fixture.Maze(), ..invalid]),
                "非法或未闭合的掉落分解限制不能静默变成可分解");
    }

    static void VerifyGiveupPenaltyFlags()
    {
        var fixture = new Fixture();
        DungeonDefinition Parse(params PvfToken[][] declarations) => fixture.Parse(fixture.Maze()
            .Concat(declarations.SelectMany(row => row)).ToArray());
        That(Parse() is { DungeonType: null, DisableGiveupPenalty: false } &&
            !Parse(fixture.Tag("no fatigue")).DisableGiveupPenalty,
            "未声明豁免及普通免疲劳标记不自动授予退图免罚");
        That(Parse() is { DisableFatigueConsumption: false } &&
            Parse(fixture.Tag("no fatigue")) is { DisableFatigueConsumption: true, DisableGiveupPenalty: false } &&
            Parse(fixture.Tag("no fatigue"),fixture.Tag("no fatigue")).DisableFatigueConsumption &&
            !Parse(fixture.Tag("no fatigue",fixture.Int(1))).DisableFatigueConsumption,
            "no fatigue是无值疲劳值免扣开关，重复置位，不与Stamina退图虚弱混同");
        foreach (string tag in new[] { "raid dungeon","risk dungeon","blood dungeon","operation hope dungeon","event dungeon","ancient dungeon" })
        {
            foreach (int value in new[] { -1,0,1,2,3 })
                That(Parse(fixture.Tag(tag,fixture.Int(value))).DisableGiveupPenalty == (value == 1 || tag == "blood dungeon" && value == 2),
                    tag + "仅接受已支持数值，blood额外支持2：" + value);
            foreach (var invalid in new[] { Array.Empty<PvfToken>(),new[] { fixture.String("1") },
                new[] { fixture.Int(1),fixture.Int(0) },new[] { new PvfToken(7,1) } })
                That(!Parse(fixture.Tag(tag,invalid)).DisableGiveupPenalty,tag + "不把空值、错类型及多参数当开启");
            foreach (var values in new[] { new[] { 1,0 },new[] { 0,1 },new[] { 1,1 },new[] { 1,3 } })
                That(Parse(values.Select(value => fixture.Tag(tag,fixture.Int(value))).ToArray()).DisableGiveupPenalty == (values[^1] == 1),
                    tag + "重复数值声明由后值覆盖，不拒绝重复或累计旧值");
        }
        foreach (string tag in new[] { "raid normal dungeon","dimension dungeon","tower of despair","enable give up","anton dungeon","no giveup panalty" })
        {
            That(Parse(fixture.Tag(tag)).DisableGiveupPenalty && Parse(fixture.Tag(tag),fixture.Tag(tag)).DisableGiveupPenalty,
                tag + "无值开关重复出现仍开启");
            That(!Parse(fixture.Tag(tag,fixture.Int(0))).DisableGiveupPenalty && !Parse(fixture.Tag(tag,fixture.Int(1))).DisableGiveupPenalty,
                tag + "不把未确认的带参格式当无值开关");
        }
        foreach (string type in new[] { "tower of dazzlement","tower of death","boss rush dungeon","dungeon","assaultmode dungeon","future dungeon" })
        {
            var definition = Parse(fixture.Tag("dungeon type",fixture.String(type)));
            That(definition.DungeonType == type && definition.DisableGiveupPenalty ==
                (type is "tower of dazzlement" or "tower of death" or "boss rush dungeon"),
                "副本类型统一解析并保留未知字符串，只有明确类型豁免：" + type);
        }
        var tower = fixture.Tag("dungeon type",fixture.String("tower of death"));
        var ordinary = fixture.Tag("dungeon type",fixture.String("dungeon"));
        var overwritten = Parse(tower,ordinary);
        That(overwritten is { DungeonType: "dungeon", DisableGiveupPenalty: false } &&
            overwritten.RawSections.Count(row => row.Tag == "dungeon type") == 2 &&
            Parse(ordinary,tower) is { DungeonType: "tower of death", DisableGiveupPenalty: true },
            "重复副本类型后值覆盖，普通类型可撤销塔类型，原声明完整保留");
        foreach (var invalid in new[] { Array.Empty<PvfToken>(),new[] { fixture.Int(1) },
            new[] { new PvfToken(7,1) },new[] { fixture.String("tower of death"),fixture.Int(1) } })
            That(Parse(tower,fixture.Tag("dungeon type",invalid)) is { DungeonType: null, DisableGiveupPenalty: false },
                "末条类型声明畸形时不回退旧类型或猜测引用");
        That(Parse(fixture.Tag("raid dungeon",fixture.Int(1)),fixture.Tag("raid dungeon",fixture.Int(0)),
            fixture.Tag("dimension dungeon"),ordinary).DisableGiveupPenalty &&
            Parse(tower,fixture.Tag("event dungeon",fixture.Int(0))).DisableGiveupPenalty &&
            Parse(fixture.Tag("ancient dungeon",fixture.Int(1)),ordinary).DisableGiveupPenalty &&
            Parse(fixture.Tag("no giveup panalty"),fixture.Tag("raid dungeon",fixture.Int(0)),ordinary).DisableGiveupPenalty,
            "不同豁免标签互不抵消，同名后值关闭只影响自身");
        That(Parse(fixture.Tag("no fatigue"),fixture.Tag("raid dungeon",fixture.Int(1))) is
            { DisableFatigueConsumption: true, DisableGiveupPenalty: true } &&
            Parse(fixture.Tag("raid dungeon",fixture.Int(1))) is { DisableFatigueConsumption: false, DisableGiveupPenalty: true },
            "疲劳值免扣和退图免罚分别解析，不因另一标签开启而联动");

        var assault = fixture.Tag("dungeon type",fixture.String("assaultmode dungeon"));
        var level = fixture.Tag("minimum required level",fixture.Int(95));
        var items = fixture.Tag("required item",fixture.Int(10161610),fixture.Int(10),fixture.Int(1));
        That(AssaultModeCatalog.ReadRequirement(Parse(ordinary,assault,level,items)) is
            { MinimumLevel: 95, ItemId: 10161610, ItemCount: 10, ItemFlag: 1 },
            "强袭读取同一副本类型属性，不再单独解码或拒绝重复类型");
        Reject<NotSupportedException>(() => AssaultModeCatalog.ReadRequirement(Parse(assault,ordinary,level,items)),
            "末条类型非强袭时保持强袭准入拒绝");
    }

    static void VerifyMapBlocks()
    {
        var fixture = new Fixture();
        PvfToken[] Block(int x, int id) => [.. fixture.Tag("map specification", fixture.String("map"), fixture.Int(x), fixture.Int(0), fixture.Int(id)),
            .. fixture.Tag("/map specification")];
        var original = fixture.Maze(width: 3, greed: "BBFFEE");
        var combined = fixture.Parse([.. original, .. Block(1, 2), .. Block(2, 3)]);
        That(combined.Mazes.Single() is { ResourceError: null, MapSpecification.Count: 3 } &&
            combined.Mazes[0].MapSpecification!.Select(map => map.Id).SequenceEqual([1, 2, 3]) &&
            combined.RawSections.Count(section => section.Tag == "map specification") == 3,
            "多个不重叠地图块按源顺序累积，原始块完整保留");
        Reject<InvalidDataException>(() => fixture.Parse([.. original, .. Block(0, 2)]), "跨块同坐标不能任取或覆盖");
        Reject<InvalidDataException>(() => fixture.Parse([.. original, .. Block(1, 2), .. Block(1, 3)]), "第三块也必须与全部已有块互斥");
        Reject<InvalidDataException>(() => fixture.Parse([.. original, .. fixture.Tag("map specification"), .. fixture.Tag("/map specification")]),
            "空块不能在拼接后被有效块掩盖");
        Reject<InvalidDataException>(() => fixture.Parse([.. original, .. Block(1, 2)[..^1]]), "第二块缺少结束标记仍拒绝");
        Reject<InvalidDataException>(() => fixture.Parse([.. original, .. fixture.Tag("start map", fixture.Int(1), fixture.Int(0)),
            .. fixture.Tag("/start map")]), "重复起点字段仍拒绝");
        That(fixture.Parse([.. original, .. Block(3, 2)]).Mazes[0].ResourceError is not null,
            "分块合并不放宽坐标边界");
    }

    static void VerifyOrdinaryMapOverrides()
    {
        var fixture = new Fixture();
        foreach (var ids in new[] { new[] {1,2,3}, new[] {3,1,2} })
        {
            var rows = new PvfToken[] { fixture.String("map"),fixture.Int(0),fixture.Int(0),fixture.Int(ids[0]),
                fixture.String("map"),fixture.Int(0),fixture.Int(0),fixture.Int(ids[1]),fixture.Int(ids[2]) };
            var overridden = fixture.Parse(fixture.Maze(mapRows: rows));
            That(overridden.Mazes.Single() is { ResourceError: null, MapSpecification: [{ Kind: "map" } map] } &&
                map.Id == ids[1] && map.CandidateIds.SequenceEqual(ids.Skip(1)) &&
                overridden.Mazes[0].OverriddenMaps is [{ Id: var oldId }] && oldId == ids[0] &&
                overridden.RawSections.Single(section => section.Tag == "map specification").Tokens.SequenceEqual(rows),
                "普通同格末行替换完整候选列表并保留本行顺序与原始token，不合并旧候选");
        }
        var narrowed = fixture.Parse(fixture.Maze(mapRows: [fixture.String("map"),fixture.Int(0),fixture.Int(0),fixture.Int(1),fixture.Int(2),
            fixture.String("map"),fixture.Int(0),fixture.Int(0),fixture.Int(3)]));
        That(narrowed.Mazes.Single().MapSpecification is [{ Id: 3, CandidateIds: [3] }] &&
            narrowed.Mazes[0].OverriddenMaps is [{ CandidateIds: [1,2] }],
            "后声明单图覆盖前声明多候选，旧候选仅留资源来源不参与有效路线");
        var mixed = fixture.Parse(fixture.Maze(mapRows: [fixture.String("map"),fixture.Int(0),fixture.Int(0),fixture.Int(1),
            fixture.String("boss"),fixture.Int(0),fixture.Int(0),fixture.Int(2)]));
        That(mixed.Mazes.Single() is { ResourceError: null, MapSpecification: [{ Kind: "boss", Id: 2 }] } &&
            mixed.Mazes[0].MapAlternatives is [{ Kind: "map", Id: 1 }] && mixed.Mazes[0].OverriddenMaps.Count == 0,
            "同格map与boss独立保留，固定Boss格投影Boss而不丢失普通候选");
        var reverseMixed = fixture.Parse(fixture.Maze(mapRows: [fixture.String("boss"),fixture.Int(0),fixture.Int(0),fixture.Int(2),
            fixture.String("map"),fixture.Int(0),fixture.Int(0),fixture.Int(1)]));
        That(reverseMixed.Mazes.Single().MapSpecification is [{ Kind: "boss", Id: 2 }] &&
            reverseMixed.Mazes[0].MapAlternatives is [{ Kind: "map", Id: 1 }],"普通行写在Boss行后也不清除独立Boss候选");
        var repeated = fixture.Parse(fixture.Maze(mapRows: [fixture.String("map"),fixture.Int(0),fixture.Int(0),fixture.Int(1),
            fixture.String("map"),fixture.Int(0),fixture.Int(0),fixture.Int(2),
            fixture.String("map"),fixture.Int(0),fixture.Int(0),fixture.Int(3)]));
        That(repeated.Mazes.Single().MapSpecification is [{ Id: 3 }] &&
            repeated.Mazes[0].OverriddenMaps.Select(map => map.Id).SequenceEqual([1,2]) &&
            repeated.Mazes[0].OverriddenMaps is IList history && history.IsReadOnly,
            "三次覆盖保留前两行的只读来源，只有末行参与有效布局");
        var invalid = fixture.Parse(fixture.Maze(mapRows: [fixture.String("map"),fixture.Int(0),fixture.Int(0),fixture.Int(99),
            fixture.String("map"),fixture.Int(0),fixture.Int(0),fixture.Int(1)]));
        That(invalid.Mazes.Single() is { ResourceError: not null, MapSpecification: null },
            "旧行存在非法单图引用时保留原资源拒绝，不借末行覆盖隐藏畸形引用");
    }

    public static void Run(PvfArchive pvf)
    {
        int checks = Count;
        var countCatalog = DungeonEnterCountCatalog.Load(pvf);
        That(countCatalog.Limits.Count == 18 && countCatalog.Limits[4000].Daily == 1 &&
            countCatalog.Limits[11006] == new DungeonEnterCountLimit(0, 3, 1, -1, 1) &&
            countCatalog.Limits[11007] == countCatalog.Limits[11006], "实际PVF角色次数表保留18图，外传三图上限1、3、3");
        var catalog = global::DfoServer.Testing.SelfTestCatalogs.Dungeons(pvf);
        That(DungeonChampionRates.For(catalog.Get(3)) == new DungeonChampionRates(33, 50, 70, 80) &&
            DungeonChampionRates.For(catalog.Get(7322)) == new DungeonChampionRates(25, 40, 60, 60) &&
            new[] { 1, 2, 5000, 5101, 11006, 400000016 }.All(id => !DungeonChampionRates.For(catalog.Get(id)).IsEnabled),
            "匹配PVF普通图恢复实际精英率，零声明、训练、卢克、祭坛及超时空不新增精英");
        var lostLand = catalog.Get(4000);
        That(lostLand.Mazes[0] is { ResourceError: null, MapSpecification.Count: 86 } &&
            lostLand.RawSections.Count(section => section.Tag == "map specification") == 2 &&
            !lostLand.Mazes[0].StaticTopology.Supported, "遗忘之地两个原始块合计86格可解析，未证实拓扑仍不开放");

        void Reject(Action action, string name)
        {
            bool rejected = false;
            try
            {
                action();
            }
            catch (Exception e) when (e is InvalidDataException or NotSupportedException)
            {
                rejected = true;
            }

            That(rejected, name);
        }

        // 当前原始索引实测1372项；配套输入可追加七个古代无限副本和五个魔兽网关。
        // 分支取原dungeon.lst完整声明，不按catalog漏装的数量降低期望，也不把物理DGN存在当作登记。
        var declaredDungeons = PvfScript.List(pvf.ReadText("dungeon/dungeon.lst"));
        foreach (var (id, relativePath) in declaredDungeons)
        {
            string path = PvfArchive.NormalizePath("dungeon/" + relativePath);
            var tokens = pvf.ReadTokens(path);
            foreach (string tag in new[] { "[disable drop item]", "[disable clear reward]" })
            {
                bool declared = tokens.Select((token, index) => (token, index)).Any(row =>
                    row.token.Type == 3 && pvf.ResolveString(row.token.Value).Equals(tag, StringComparison.OrdinalIgnoreCase) &&
                    (row.index + 1 == tokens.Length || tokens[row.index + 1].Type == 3));
                if (declared)
                    That(tag == "[disable drop item]" ? catalog.Get(id).DisableItemDrops : catalog.Get(id).DisableClearReward,
                        "全部登记副本的无参数奖励门禁须进入统一领域定义：" + id + " " + tag);
            }
        }
        int ancientDeclarations = Enumerable.Range(7101, 7).Count(declaredDungeons.ContainsKey);
        int beastDeclarations = Enumerable.Range(5335, 5).Count(declaredDungeons.ContainsKey);
        That(ancientDeclarations is 0 or 7 && beastDeclarations is 0 or 5,
            "可选古代七图与魔兽五网关必须各自完整登记，不能把部分输入冒成完整或原始包");
        int expectedDungeonCount = 1372 + ancientDeclarations + beastDeclarations;
        That(declaredDungeons.Count == expectedDungeonCount && catalog.DungeonCount == expectedDungeonCount &&
            declaredDungeons.All(row => catalog.ContainsDungeon(row.Key) &&
                catalog.DungeonPath(row.Key) == PvfArchive.NormalizePath("dungeon/" + row.Value)),
            "原1372项与可选增量逐ID/path完整装载，不允许目录漏项或凭物理文件补登记");
        if (ancientDeclarations == 7 && beastDeclarations == 5)
            That(catalog.DungeonCount == 1384, "副本索引保留原1379项并追加五个魔兽兼容入口");
        string[] gatewayNames = ["canyon_of_beast", "forest_of_redwitch", "hideout_of_fear", "gust_zone", "time_plaza"];
        for (int index = 0; index < gatewayNames.Length; index++)
        {
            int gateway = 5335 + index;
            if (declaredDungeons.ContainsKey(gateway))
                That(catalog.Get(5335 + index).Path == $"dungeon/beast_metrocenter/beast_{gatewayNames[index]}.dgn",
                    "五个新增副本索引分别绑定独立魔兽入口：" + (5335 + index));
            else
            {
                bool missingRejected = false;
                try { catalog.Get(gateway); }
                catch (InvalidDataException error) when (error.Message == "副本索引不存在: " + gateway)
                { missingRejected = true; }
                That(!catalog.ContainsDungeon(gateway) && missingRejected,
                    "当前未登记的魔兽网关沿正式目录精确拒绝，不猜DGN路径：" + gateway);
            }
        }
        Console.WriteLine($"DUNGEON INDEX original=1372 ancient={ancientDeclarations} beast={beastDeclarations} total={expectedDungeonCount}; 未登记网关不代表真实魔兽入口正向通过");
        foreach (int rift in new[] { 316, 530 })
            That(catalog.Get(rift).AllowsDifficulty(3, false) && !catalog.Get(rift).AllowsDifficulty(0, false),
                "裂缝副本只放行资源声明的难度 3：" + rift);
        foreach (int ekern in new[] { 5097, 5098 })
        {
            var legend = catalog.Get(ekern);
            That(legend.MinimumLevel == 90 && legend.DisableFatigueConsumption &&
                legend.Mazes.Count > 0 && legend.Mazes.All(maze => maze.ResourceError is null && maze.StaticTopology.Supported) &&
                Enumerable.Range(0, 5).All(value => legend.AllowsDifficulty((byte)value, false) == (value == 2)),
                "艾肯传奇副本只放行重复声明的难度 2：" + ekern);
        }
        foreach (var (mooncreek, expectedWeights) in new (int Id, (int BuffId, int Weight)[] Cards)[]
            {
                (3200, [(110, 10), (111, 10), (112, 20), (113, 20), (114, 20), (115, 20)]),
                (3201, [(110, 20), (111, 20), (112, 10), (113, 10), (114, 20), (115, 20)]),
                (3202, [(110, 20), (111, 20), (112, 20), (113, 20), (114, 10), (115, 10)]),
            })
        {
            var dragon = catalog.Get(mooncreek);
            var cards = dragon.Mazes.Single().ClearPartyBuffCards;
            That(cards.Select(card => (card.BuffId, card.Weight)).SequenceEqual(expectedWeights) &&
                DungeonMazeConditions.RollClearPartyBuffCard(cards, _ => 0) == 110 &&
                DungeonMazeConditions.RollClearPartyBuffCard(cards, _ => expectedWeights[0].Weight - 1) == 110 &&
                DungeonMazeConditions.RollClearPartyBuffCard(cards, _ => expectedWeights[0].Weight) == 111 &&
                DungeonMazeConditions.RollClearPartyBuffCard(cards, _ => 99) == 115 &&
                DfoServer.Network.Builders.DungeonSettlementPackets.ClearPartyBuffCard(114).SequenceEqual(new byte[] { 1, 114, 0, 0, 0 }),
                "月轮山三图解析完整月溪镇Buff奖励卡权重并构造615翻牌通知：" + mooncreek);
        }
        That(Enumerable.Range(0, 5).All(value => !catalog.Get(1).AllowsDifficulty((byte)value, false)),
            "洛兰同时声明指定难度 0 和 1 时普通路线仍全部拒绝");
        Reject(() => catalog.Get(-1), "negative dungeon index rejected");
        Reject(() => catalog.Get(int.MaxValue), "unlisted dungeon index rejected");
        var dungeon = catalog.Get(3);
        That(dungeon.Id == 3 && dungeon.Path == "dungeon/act1/mirkwood.dgn" && dungeon.MinimumLevel == 1 && dungeon.BasisLevel == 5, "Dungeon3 metadata retains actual levels");
        That(dungeon.Mazes.Count == 5 && dungeon.Mazes.Select(m => m.SourceOrdinal).SequenceEqual(Enumerable.Range(0, 5)), "five repeated unclosed maze info groups retained in source order");
        var ordinary = dungeon.Mazes[0];
        That(ordinary.Width == 4 && ordinary.Height == 2 && ordinary.Starts.SequenceEqual([new DungeonPoint(0, 1)]) && ordinary.Bosses.SequenceEqual([new DungeonPoint(3, 0)]), "ordinary maze geometry retained");
        That(ordinary.MapSpecification is { Count: 6 } && ordinary.MapSpecification.Select(m => m.Id).SequenceEqual(Enumerable.Range(58548, 6)), "ordinary maze map references retained");
        That(ordinary.MapSpecification![0].Path == "map/mirkwood/mirkwood1.map" && ordinary.MapSpecification[^1].Kind == "boss", "map directory resolves actual paths and boss role");
        That(ordinary.QuestConnectionTokens.Count == 0 && dungeon.Mazes.Skip(1).Select(m => m.QuestConnectionTokens[1].Value).SequenceEqual([3145, 3146, 7939, 7940]), "ordinary and four quest connections remain separate");
        That(QuestMaps(dungeon, 3145).SequenceEqual([76121, 76122, 76123, 76124, 76125, 76126])
            && QuestMaps(catalog.Get(6), 3147).SequenceEqual([76141, 76142, 76143, 76144, 76145, 76146])
            && QuestMaps(catalog.Get(9), 3148).SequenceEqual([76151, 76152, 76153, 76154, 76155, 76156])
            && QuestMaps(catalog.Get(7), 3149).SequenceEqual([76161, 76162, 76163, 76164, 76165, 76166]),
            "story mazes keep their own maps and boss rooms");
        foreach (int id in new[] { 8, 1000 })
        {
            var mixedRoutes = catalog.Get(id);
            That(mixedRoutes.Mazes[0].QuestConnectionTokens.Count == 0 && mixedRoutes.Mazes[0].StaticTopology.Supported,
                $"普通Dungeon{id}不被任务叠层变体阻断");
            var layered = mixedRoutes.Mazes.Where(maze => maze.LayeredMaps.Count > 0).ToArray();
            That(layered.Length > 0 && layered.All(maze => maze.LayeredMaps.All(map => map.Kind == "layered") &&
                maze.MapSpecification!.All(map => map.Kind != "layered")), "普通主层与任务叠层原行分别保留，互不替换路线");
        }
        var questList = dungeon.RawSections.Single(s => s.Tag == "quest list").Tokens;
        That(questList.Count(t => t.Type == 7) == 2 && questList.Where(t => t.Type == 0).Select(t => t.Value).SequenceEqual([3145, 3146, 7939, 7940]), "story quest list keeps type7 opaque tokens");
        var lorien = catalog.Get(1);
        That(lorien.MinimumLevel == 55 && lorien.BasisLevel == 55 && lorien.Mazes[0].Starts.Count == 2 && lorien.Mazes[0].Bosses.Count == 2, "Lorien metadata and all start/boss points retained");
        That(lorien.Mazes[0].MapSpecification is null && lorien.Mazes[0].MapStructure == "unsupported" && lorien.Mazes[0].UnsupportedReason is not null, "missing map specification is unsupported rather than valid empty map");
        var grakkarak = catalog.Get(7120);
        That(grakkarak.MinimumLevel == 1 && grakkarak.BasisLevel == 1 && grakkarak.Mazes.Count == 1 && grakkarak.Mazes[0].Width == 6 && grakkarak.Mazes[0].Height == 1, "Dungeon7120 retains matching resource metadata and geometry");
        That(grakkarak.Mazes[0].MapSpecification is { Count: 6 } grakkarakMaps && grakkarakMaps.Select(m => m.Id).SequenceEqual(Enumerable.Range(57772, 6)), "Dungeon7120 retains explicit map references without claiming an entrance");
        var plaza = catalog.Get(310);
        That(plaza.Mazes.Count == 12 && plaza.Mazes.Select(maze => maze.SourceOrdinal).SequenceEqual(Enumerable.Range(0,12)) &&
            plaza.Mazes[1] is { Width: 7,Height: 2,ResourceError: null } && plaza.Mazes[1].ConnectsQuest(3946) &&
            QuestMaps(plaza,3946).SequenceEqual(Enumerable.Range(76670,9)) &&
            plaza.Mazes[^1] is { Width: 2,Height: 1,ResourceError: null } && plaza.Mazes[^1].ConnectsQuest(3973),
            "真实时间广场保留十二条普通路线，末尾魔兽布局不污染3946与3973");
        That(plaza.RawSections.Count(section => section.Tag == "beast dungeon") == 3 &&
            plaza.RawSections.Count(section => section.Tag == "boss map" && section.Tokens.SequenceEqual([new PvfToken(0,-1),new(0,-1)])) == 3,
            "真实时间广场三个魔兽区块及无普通Boss声明仍完整可查询");
        var canyon = catalog.Get(311);
        That(canyon.Mazes.Any(maze => maze.MapSpecification?.Any(map => map.Id == 655658) == true),
            "311多出的655658路线仍留在普通迷宫");
        var finalBattle = catalog.Get(85);
        var finalBattleMaze = finalBattle.Mazes.Single(maze => maze.ConnectsQuest(3553));
        That(finalBattleMaze is { ResourceError: null, MapSpecification.Count: 10, StoryStages.Count: 0, LayeredMaps.Count: 0 } &&
            finalBattleMaze.MapSpecification.Single(map => map.Position == new DungeonPoint(5,1)) is
                { Id: 92470, Kind: "map", CandidateIds: [92470] } &&
            finalBattleMaze.OverriddenMaps is [{ Id: 92467, CandidateIds: [92467] }] &&
            !finalBattleMaze.MapSpecification.Any(map => map.Id == 92467) && finalBattleMaze.StaticTopology is { Supported: true, Links.Count: 9 } &&
            finalBattle.RawSections.Any(section => section.Tag == "map specification" &&
                section.Tokens.Any(token => token.Type == 0 && token.Value == 92467) &&
                section.Tokens.Any(token => token.Type == 0 && token.Value == 92470)),
            "3553按本版末行覆盖保留十个有效格及92470，原始重复声明仍完整可查询");
        That(JsonSerializer.Serialize(dungeon).Contains("QuestConnectionTokens", StringComparison.Ordinal), "resource definition is serializable");
        var shared = new DungeonDefinition[16];
        Parallel.For(0, shared.Length, i => shared[i] = catalog.Get(3));
        That(shared.All(d => ReferenceEquals(d, dungeon)), "thread shared cache publishes one completed immutable definition");
        That(dungeon.Mazes is IList list && list.IsReadOnly && ordinary.Starts is IList points && points.IsReadOnly && questList is IList raw && raw.IsReadOnly, "collections do not expose mutable arrays");
        var fixture = new Fixture();
        var valid = fixture.Maze();
        var parsed = fixture.Parse(valid);
        That(parsed.MinimumLevel is null && parsed.BasisLevel is null, "absent level metadata remains unknown");
        That(!parsed.RoomMoveKeepState && !parsed.IndividualMapMovement, "absent movement flags retain native false defaults");
        foreach (int value in new[] { -1, 0, 1, 2 })
        {
            var flags = fixture.Tag("room move keep state",fixture.Int(value))
                .Concat(fixture.Tag("individual map movement",fixture.Int(value))).ToArray();
            var withFlags = fixture.Parse(flags.Concat(fixture.Maze()).ToArray());
            That(withFlags.RoomMoveKeepState == (value == 1) && withFlags.IndividualMapMovement == (value == 1) &&
                withFlags.RawSections.Single(section => section.Tag == "individual map movement").Tokens.Single().Value == value,
                $"movement flags accept only integer one and retain raw value {value}");
        }
        var independentFlags = fixture.Parse(fixture.Tag("room move keep state",fixture.Int(1))
            .Concat(fixture.Tag("individual map movement",fixture.Int(0))).Concat(fixture.Maze()).ToArray());
        That(independentFlags.RoomMoveKeepState && !independentFlags.IndividualMapMovement, "two movement flags keep separate native fields");
        var repeatedFlags = fixture.Parse(fixture.Tag("individual map movement",fixture.Int(1))
            .Concat(fixture.Tag("individual map movement",fixture.Int(0))).Concat(fixture.Maze()).ToArray());
        That(!repeatedFlags.IndividualMapMovement && repeatedFlags.RawSections.Count(section => section.Tag == "individual map movement") == 2,
            "repeated movement declaration uses the last value and preserves source declarations");
        var opaqueFlag = fixture.Parse(fixture.Tag("individual map movement",new PvfToken(7,1)).Concat(fixture.Maze()).ToArray());
        That(!opaqueFlag.IndividualMapMovement, "unresolved flag token is not interpreted as integer one");
        valid[1] = new(0, 99);
        That(parsed.Mazes[0].Width == 1, "published result does not retain caller token array");
        Unsupported(fixture.Maze(width: 0),"size","零尺寸路线保留诊断");
        Unsupported(fixture.Maze(height: 0),"size","零高度路线保留诊断");
        Unsupported(fixture.Maze(startX: -1),"坐标超出","负起点路线保留诊断");
        Unsupported(fixture.Maze(startX: 1),"坐标超出","越界起点路线保留诊断");
        That(!fixture.Parse(fixture.Maze(greed: "BBBB")).Mazes[0].GreedGrid.Supported, "过量Greed格保留为Unsupported而不阻断定义");
        That(!fixture.Parse(fixture.Maze(height: 2, greed: "BBB\nB")).Mazes[0].GreedGrid.Supported, "跨换行拆开的字母对Unsupported，不先抹空白");
        That(fixture.Parse(fixture.Maze(height: 2, greed: "BBBB")).Mazes[0].GreedGrid.Supported, "线性两个格不要求原文必须含两行");
        var mixed = fixture.Parse(fixture.Maze(greed: "QQ").Concat(fixture.Maze()).ToArray());
        That(mixed.Mazes.Count == 2 && !mixed.Mazes[0].GreedGrid.Supported && mixed.Mazes[0].GreedGrid.UnsupportedReason is not null &&
            mixed.Mazes[1].GreedGrid.Supported, "一个未知Greed不阻断同副本后续合法maze");
        Unsupported(fixture.Maze(kind: "random"),"类型 random","未知地图种类保留诊断");
        Unsupported(fixture.Maze(mapId: 99),"地图不存在: 99","未列入目录的地图引用保留诊断");
        Unsupported(fixture.Maze(),"地图不存在: 1","缺失地图文件保留诊断",exists: false);
        Reject(() => fixture.Parse(fixture.Maze().Concat(fixture.Tag("size", fixture.Int(1), fixture.Int(1))).ToArray()), "duplicate size in same maze rejected");
        That(fixture.Parse(fixture.Maze(duplicateMap: true)).Mazes[0] is { ResourceError: null,MapSpecification.Count: 1,OverriddenMaps.Count: 1 },
            "同种重复候选行按客户端清空后覆盖，不以重复坐标拒绝");
        Unsupported(fixture.Maze(duplicateStart: true),"重复 start/boss map 坐标","重复起点坐标保留诊断");
        Reject(() => fixture.Parse(fixture.Maze(closeMap: false)), "unclosed map specification rejected");
        Reject(() => fixture.Parse(fixture.Tag("minimum required level", fixture.Int(1)).Concat(fixture.Tag("minimum required level", fixture.Int(2))).Concat(fixture.Maze()).ToArray()), "duplicate minimum level rejected");
        Reject(() => fixture.Parse(fixture.Tag("minimum required level", new PvfToken(7, 123)).Concat(fixture.Maze()).ToArray()), "unresolved level reference cannot become default level");
        var connection = fixture.Tag("quest connection", fixture.Int(0), new(7, 987), fixture.Int(-1));
        var opaque = fixture.Parse(fixture.Maze().Concat(connection).ToArray());
        That(opaque.Mazes[0].QuestConnectionTokens.SequenceEqual([new PvfToken(0, 0), new(7, 987), new(0, -1)]), "quest connection raw reference tokens retained");
        var later = fixture.Parse(fixture.Maze().Concat(fixture.Tag("maze info"))
            .Concat(fixture.Tag("size",fixture.Int(1),fixture.Int(1))).ToArray());
        That(later.Mazes is [{ ResourceError: null, MapSpecification.Count: 1 },{ SourceOrdinal: 1, ResourceError: not null, MapSpecification: null }] &&
            later.Mazes[1].ResourceError!.Contains("缺少字段: greed",StringComparison.Ordinal) && !later.Mazes[1].StaticTopology.Supported,
            "后续缺字段路线单独记录诊断，前一合法资源不被阻断");
        var badConnection = fixture.Tag("quest connection",fixture.Int(0),fixture.Int(29880),fixture.Int(-1));
        var broken = fixture.Parse(fixture.Maze(width: 0).Concat(badConnection).ToArray()).Mazes[0];
        var retained = dungeon with { Mazes = new[] { broken,dungeon.Mazes.Single(maze => maze.ConnectsQuest(3145)) } };
        var character = new Character(99,0,"MazeFixture",0,new(38,2,1085,249),null)
        { ActiveMainQuests = [new(0,29880,1) { AcceptanceId = Guid.NewGuid() },new(1,3145,1) { AcceptanceId = Guid.NewGuid() }] };
        That(broken.ConnectsQuest(29880) && broken.QuestConnectionTokens.SequenceEqual([new PvfToken(0,0),new(0,29880),new(0,-1)]) &&
            !DungeonEntryPlan.TryCreate(retained,character,3,29880,0,out _,out var brokenReason) && brokenReason == "quest-maze-layout-unsupported" &&
            DungeonEntryPlan.TryCreate(retained,character,3,3145,0,out var supported,out _) && supported!.StartMap.Id == 76121,
            "坏路线保留自身任务关联但准入仍拒绝，同副本真实3145路线可继续使用");
        var goodDirectory = new PvfToken[]
        {
            fixture.Int(3),
            fixture.String("act1/mirkwood.dgn")
        };
        That(DungeonCatalog.ParseDirectory(goodDirectory, fixture.Resolve, "dungeon")[3] == "dungeon/act1/mirkwood.dgn", "directory path resolved");
        Reject(() => DungeonCatalog.ParseDirectory(goodDirectory.Concat(goodDirectory).ToArray(), fixture.Resolve, "dungeon"), "duplicate directory id rejected");
        Reject(() => DungeonCatalog.ParseDirectory([fixture.Int(-1), fixture.String("a.dgn")], fixture.Resolve, "dungeon"), "negative directory id rejected");
        Reject(() => DungeonCatalog.ParseDirectory([fixture.Int(1), fixture.String("../map/a.map")], fixture.Resolve, "dungeon"), "directory path escape rejected");
        Reject(() => DungeonCatalog.ParseDirectory([fixture.Int(1)], fixture.Resolve, "dungeon"), "truncated directory rejected");
        Console.WriteLine($"PASS {Count - checks} dungeon resource assertions");

        void Unsupported(PvfToken[] tokens,string diagnostic,string name,bool exists = true)
        {
            var maze = fixture.Parse(tokens,exists).Mazes.Single();
            That(maze.ResourceError is not null && maze.ResourceError.Contains(diagnostic,StringComparison.Ordinal) &&
                maze.MapSpecification is null && maze.MapStructure == "unsupported" && maze.UnsupportedReason == maze.ResourceError &&
                !maze.GreedGrid.Supported && !maze.StaticTopology.Supported,name + "，不计作支持或生成通路");
        }
    }

    static IEnumerable<int> QuestMaps(DungeonDefinition dungeon, int questId)
    {
        var maze = dungeon.Mazes.Single(m => m.QuestConnectionTokens.Count > 1 && m.QuestConnectionTokens[1].Value == questId);
        return maze.MapSpecification!.Select(m => m.Id);
    }

    private sealed class Fixture
    {
        private readonly Dictionary<int, string> strings = new();
        public string Resolve(int id) => strings[id];
        public PvfToken Int(int value) => new(0, value);
        public PvfToken String(string value)
        {
            int id = strings.Count + 1;
            strings.Add(id, value);
            return new(6, id);
        }

        public PvfToken[] Tag(string tag, params PvfToken[] values) => [new(3, String("[" + tag + "]").Value), .. values];
        public PvfToken[] Maze(int width = 1, int height = 1, int startX = 0, string greed = "BB", string kind = "boss", int mapId = 1, bool duplicateMap = false, bool duplicateStart = false, bool closeMap = true, PvfToken[]? mapRows = null)
        {
            var tokens = new List<PvfToken>();
            tokens.AddRange(Tag("maze info"));
            tokens.AddRange(Tag("size", Int(width), Int(height)));
            tokens.AddRange(Tag("greed", String(greed)));
            var maps = new List<PvfToken>
            {
                String(kind),
                Int(0),
                Int(0),
                Int(mapId)
            };
            if (duplicateMap)
                maps.AddRange([String(kind), Int(0), Int(0), Int(mapId)]);
            tokens.AddRange(Tag("map specification", mapRows ?? maps.ToArray()));
            if (closeMap)
                tokens.AddRange(Tag("/map specification"));
            var starts = new List<PvfToken>
            {
                Int(startX),
                Int(0)
            };
            if (duplicateStart)
                starts.AddRange([Int(startX), Int(0)]);
            tokens.AddRange(Tag("start map", starts.ToArray()));
            tokens.AddRange(Tag("/start map"));
            tokens.AddRange(Tag("boss map", Int(0), Int(0)));
            tokens.AddRange(Tag("/boss map"));
            return tokens.ToArray();
        }

        public DungeonDefinition Parse(PvfToken[] tokens, bool exists = true,Func<string,bool>? contains = null) =>
            DungeonCatalog.ParseResource(3,"dungeon/test.dgn",tokens,Resolve,
                Enumerable.Range(59453,15).ToDictionary(id => id,id => $"map/beast/{id}.map")
                    .Concat(new Dictionary<int,string> { [1] = "map/test.map",[2] = "map/second.map",[3] = "map/third.map" })
                    .ToDictionary(row => row.Key,row => row.Value),contains ?? (_ => exists));
    }
}
