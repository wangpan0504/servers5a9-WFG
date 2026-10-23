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
    public static void RunCore()
    {
        int checks = Count;
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
            beastTokens.AddRange(fixture.Tag("start map",fixture.Int(0),fixture.Int(0)));
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
        Reject<InvalidDataException>(() => fixture.Parse(fixture.Maze()
            .Concat(fixture.Tag("beast dungeon",fixture.Int(1))).Concat(beastTokens).ToArray()),"带参数的魔兽边界不能放行");
        Reject<InvalidDataException>(() => fixture.Parse(fixture.Maze().Concat(fixture.Tag("unknown dungeon"))
            .Concat(fixture.Tag("size",fixture.Int(5),fixture.Int(3))).ToArray()),"未知标记不能用作放宽重复布局的边界");
        VerifyOrdinaryMapOverrides();
        VerifyGiveupPenaltyFlags();
        Console.WriteLine($"PASS {Count-checks} 无资源副本难度、作用域及退图豁免断言");

        static DungeonDefinition Definition(params DungeonRawSection[] sections) => new(17,"dungeon/difficulty-fixture.dgn",1,1,[],sections);
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

        var catalog = global::DfoServer.Testing.SelfTestCatalogs.Dungeons(pvf);
        That(catalog.DungeonCount == 1372, "matching dungeon index contains 1372 entries");
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

        public DungeonDefinition Parse(PvfToken[] tokens, bool exists = true) => DungeonCatalog.ParseResource(3, "dungeon/test.dgn", tokens, Resolve,
            new Dictionary<int, string> { [1] = "map/test.map", [2] = "map/second.map", [3] = "map/third.map" }, _ => exists);
    }
}
