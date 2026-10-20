using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Quests;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class DungeonMapPoolTests
{
    public static void RunCore()
    {
        int checks = Count;
        var text = new List<string>();
        PvfToken S(string value,byte type = 6) { text.Add(value); return new(type,text.Count-1); }
        PvfToken[] Tokens(string kind,string greed,int owner = 10) => [S("[dungeon]",3),new(0,owner),S("[/dungeon]",3),
            S("[type]",3),S(kind),S("[greed]",3),S(greed)];
        DungeonMapPoolEntry? Parse(string kind,string greed,int owner = 10) => DungeonMapPool.Parse(100,"map/test.map",Tokens(kind,greed,owner),id => text[id]);
        That(Parse("[normal]","BB BB") is { Layouts: 2 } && Parse("[boss]","EE EE") is { Layouts: 16 },"MAP pair按真实资源方向建立普通与Boss候选");
        That(Parse("[normal]","BB BB\r\nEE EE") is { Layouts: 18 } && Parse("[normal]","B B BB") is null && Parse("[normal]","BB\tBB") is null,
            "入口编码多行有效，字符对内部空格及未知空白不被修补");
        That(Parse("[normal]","NN BB") is null && Parse("[dummy]","BB BB") is null && Parse("[normal]","BB BB",-1) is null,
            "异值pair、未知地图种类和失效归属不变成正常候选");
        var maze = new DungeonMaze(7,2,1,"BBEE",[new(0,0)],[new(1,0)],null,[]);
        var story = maze with { QuestConnectionTokens = [new(0,0),new(0,100),new(0,-1)],
            MapSpecification = [new("map",new(0,0),100,"map/story.map"),new("boss",new(1,0),200,"map/story-boss.map")] };
        var definition = new DungeonDefinition(10,"dungeon/test.dgn",1,1,[maze,story],[]);
        var pool = new DungeonMapPool([new(100,"map/story.map",[10],"[normal]",2),new(101,"map/start.map",[10],"[normal]",2),
            new(201,"map/boss.map",[10],"[boss]",16),new(202,"map/wrong-owner.map",[11],"[boss]",16)]);
        var result = pool.Resolve(definition,maze,_ => 0);
        That(result.StaticTopology.Supported && result.MapSpecification!.Select(row => row.Id).SequenceEqual([101,201]) && result.SourceOrdinal == 7,
            "普通生成匹配归属、类型和双向出口，排除剧情专属地图，保持原maze索引");
        That(ReferenceEquals(pool.Resolve(definition,story,_ => 0),story),"缺失剧情定义不能由普通地图池猜测替代");
        var explicitMap = new DungeonMap("map",new(0,0),300,"map/explicit.map");
        var partial = maze with { MapSpecification = [explicitMap] };
        That(pool.Resolve(definition,partial,_ => 0).MapSpecification![0] == explicitMap,"部分普通布局保留明确指定的实际地图");
        var missing = new DungeonMapPool([new(201,"map/wrong-gate.map",[10],"[boss]",2)]);
        That(ReferenceEquals(missing.Resolve(definition,maze,_ => 0),maze),"候选为空或出口不匹配时保留不可用布局，不补伪地图");
        var choices = new DungeonMapPool([new(101,"map/start.map",[10],"[normal]",2),new(102,"map/start2.map",[10],"[normal]",2),
            new(201,"map/boss.map",[10],"[boss]",16)]);
        Reject<InvalidDataException>(() => choices.Resolve(definition,maze,count => count),"随机返回越界不能部分发布地图");
        var protectedStory = story with { MapSpecification = [story.MapSpecification![0] with { CandidateIds = [100,104] },story.MapSpecification[1]],
            LayeredMaps = [explicitMap with { Kind = "layered",Id = 105,CandidateIds = [105,106] }] };
        var protectedPool = new DungeonMapPool([new(104,"map/story2.map",[10],"[normal]",2),
            new(106,"map/layer2.map",[10],"[normal]",2),new(101,"map/start.map",[10],"[normal]",2),
            new(201,"map/boss.map",[10],"[boss]",16)]);
        That(protectedPool.Resolve(definition with { Mazes = [maze,protectedStory] },maze,_ => 0).MapSpecification!
            .Select(map => map.Id).SequenceEqual([101,201]),"普通池排除剧情和叠层全部候选，不只排除首张地图");
        var overriddenStory = protectedStory with
        {
            OverriddenMaps = [explicitMap with { CandidateIds = [107,108] },explicitMap with { CandidateIds = [109] }]
        };
        var historyPool = new DungeonMapPool([new(107,"map/overridden1.map",[10],"[normal]",2),
            new(108,"map/overridden2.map",[10],"[normal]",2),new(109,"map/overridden3.map",[10],"[normal]",2),
            new(101,"map/start.map",[10],"[normal]",2),new(201,"map/boss.map",[10],"[boss]",16)]);
        That(protectedStory.SameLayout(overriddenStory) &&
            historyPool.Resolve(definition with { Mazes = [maze,overriddenStory] },maze,_ => 0).MapSpecification!
                .Select(map => map.Id).SequenceEqual([101,201]),
            "覆盖历史不改变有效路线等价性，任务专用历史的全部候选仍不能进入普通池");
        ExplicitCandidates();
        GriefResource();
        var trainingMaze = new DungeonMaze(0,1,1,"BB",[new(0,0)],[new(0,0)],null,[]);
        var trainingDefinition = new DungeonDefinition(5000,"dungeon/poongjintrainingroom/poongjintrainingroom.dgn",1,null,[trainingMaze],[]);
        var trainingPool = new DungeonMapPool([new(36250,"map/poongjintrainingroom/11100(0,0)start.map",[5000],"[normal]",2)]);
        var trainingRoom = trainingPool.Resolve(trainingDefinition,trainingMaze,_ => throw new InvalidOperationException());
        That(trainingRoom.IsTrainingRoom && trainingRoom.Greed == "BB" && trainingRoom.StaticTopology is { Supported: true, Links.Count: 0 } &&
            trainingRoom.MapSpecification is [{ Id: 36250, Kind: "boss" }],"修炼场同格起点终点使用归属start资源，保留BB而不建立越界邻接");
        var invalidTrainingMaze = trainingMaze with { Bosses = [new(1,0)] };
        That(ReferenceEquals(trainingPool.Resolve(trainingDefinition with { Id = 4999 },trainingMaze,_ => 0),trainingMaze) &&
            ReferenceEquals(trainingPool.Resolve(trainingDefinition,invalidTrainingMaze,_ => 0),invalidTrainingMaze),
            "修炼场资源放行不扩大到别的副本或不匹配坐标");
        Console.WriteLine($"PASS {Count-checks} 普通地图池归属、方向、随机与剧情隔离断言");
    }

    static void GriefResource()
    {
        var maze = new DungeonMaze(0,1,1,"BB",[new(0,0)],[new(0,0)],null,[]);
        var definition = new DungeonDefinition(5115,"dungeon/towers/grief/5115_001.dgn",90,90,[maze],
            [new("tower of grief",[])]);
        var candidate = new DungeonMapPoolEntry(100247,"map/towerofgrief_down/100247_001.map",[5115],"[normal]",2);
        var pool = new DungeonMapPool([candidate]);
        var resolved = pool.Resolve(definition,maze,_ => throw new InvalidOperationException());
        That(resolved.IsGriefTower && resolved.Greed == "BB" && resolved.StaticTopology is { Supported: true, Links.Count: 0 } &&
            resolved.StaticTopology.Rooms is [{ ExitMask: 0, Map.Id: 100247 }] && resolved.MapSpecification![0].Path == candidate.Path &&
            maze.MapSpecification is null,"悲叹精确资源生成无邻接单房间，保留共享原始1x1 BB");
        foreach (var invalid in new[] { definition with { Id = 5116 },definition with { Path = "dungeon/wrong.dgn" },
            definition with { RawSections = [] },definition with { RawSections = [new("tower of grief",[new(0,1)])] },
            definition with { RawSections = [new("tower of grief",[]),new("tower of grief",[])] } })
            That(ReferenceEquals(pool.Resolve(invalid,maze,_ => 0),maze),"错误副本、路径或模式声明不授予悲叹几何");
        foreach (var wrong in new[] { candidate with { Dungeons = [5116] },candidate with { Dungeons = [5115,5116] },candidate with { Id = 100248 },
            candidate with { Path = "map/wrong.map" },candidate with { Kind = "[boss]" },candidate with { Layouts = 18 } })
            That(ReferenceEquals(new DungeonMapPool([wrong]).Resolve(definition,maze,_ => 0),maze),"归属、ID、路径、类型或布局不符拒绝");
        That(ReferenceEquals(new DungeonMapPool([candidate,candidate with { Id = 100248 }]).Resolve(definition,maze,_ => 0),maze),
            "反向池存在两个可用候选时不任选首项");
        foreach (var wrong in new[] { maze with { Greed = "AA" },maze with { Bosses = [new(1,0)] },
            maze with { MapSpecification = resolved.MapSpecification },maze with { LayeredMaps = resolved.MapSpecification! },
            maze with { WarpMaps = [new(new(0,0),new(0,0))] },
            maze with { QuestConnectionTokens = [new(0,0),new(0,2929),new(0,-1)] } })
            That(ReferenceEquals(pool.Resolve(definition,wrong,_ => 0),wrong),"错误布局、坐标、显式图、叠层或任务连接不进入专用补图");
        That(!(resolved with { IsGriefTower = false }).StaticTopology.Supported &&
            !(resolved with { MapSpecification = [resolved.MapSpecification![0] with { Id = 100248 }] }).StaticTopology.Supported,
            "普通BB出口仍拒绝，专用标记也不接受其他地图");
        var quest = QuestEligibilityTests.Quest(2929);
        var character = new Character(99,0,"GriefFixture",0,new(38,2,1085,249),null)
            { Level = 90, ActiveMainQuests = [new(0,2929,1)] };
        foreach (var scenario in new[] { (Quest:0u,Replay:false),(Quest:2929u,Replay:false),(Quest:2929u,Replay:true) })
        {
            var expected = scenario.Replay ? character with { CompletedQuests = [2929] } : character;
            That(!DungeonEntryPlan.TryCreate(definition with { Mazes = [resolved] },expected,5115,scenario.Quest,0,
                out var entry,out string reason,quest: quest,isReplay: scenario.Replay) && entry is null &&
                reason == "grief-tower-entry-contract-unsupported","资源几何不能让普通、显式或回放绕过未知票据/疲劳准入合同");
        }
    }

    static void ExplicitCandidates()
    {
        var start = new DungeonMap("map",new(0,0),100,"map/start.map") { CandidateIds = [100,101,101] };
        var boss = new DungeonMap("boss",new(1,0),200,"map/boss.map");
        var maze = new DungeonMaze(0,2,1,"BBEE",[start.Position],[boss.Position],[start,boss],[])
            { OverriddenMaps = [start with { Id = 300, CandidateIds = [300,301] }] };
        var paths = new Dictionary<int,string> { [100] = start.Path,[101] = "map/start2.map",[200] = boss.Path };
        int draws = 0;
        var selected = DungeonCatalog.ResolveMapCandidates(maze,paths,_ => true,limit =>
        {
            That(limit == 2,"普通同行地图按不同ID均匀选择，不将重复声明作为权重"); draws++; return 1;
        });
        That(draws == 1 && selected.MapSpecification![0].Id == 101 && selected.MapSpecification[0].Path == paths[101] &&
            selected.MapSpecification[0].CandidateIds.SequenceEqual([101]),"入场规划冻结实际地图ID与路径，单候选不重复抽签");
        That(ReferenceEquals(selected.OverriddenMaps,maze.OverriddenMaps),
            "候选冻结不解析或丢失覆盖历史，历史不会重新参与入场抽选");
        That(maze.MapSpecification![0] == start && maze.MapSpecification[0].CandidateIds.SequenceEqual([100,101,101]),
            "抽选不修改共享资源定义或另一账号的候选");
        That(ReferenceEquals(DungeonCatalog.ResolveMapCandidates(selected,paths,_ => true,_ => throw new InvalidOperationException()),selected),
            "已冻结布局不重抽，回房保持原实际地图身份");
        var character = new Character(99,0,"CandidateFixture",0,new(38,2,1085,249),null);
        var definition = new DungeonDefinition(777,"dungeon/candidates.dgn",1,1,[maze],[]);
        That(DungeonEntryPlan.TryCreate(definition,character,777,0,0,out var entry,out _,
            resolveMaze: value => DungeonCatalog.ResolveMapCandidates(value,paths,_ => true,_ => 1)) &&
            entry!.StartMap.Id == 101 && entry.BaseBossMap.Id == 200,"普通准入的起点和终点使用同一已冻结布局");
        That(DungeonEntryPlan.TryCreate(definition with { Mazes = [selected] },character,777,0,0,out _,out _,
            draw: _ => throw new InvalidOperationException(),quests: new QuestCatalog(new()),
            resolveMaze: value => DungeonCatalog.ResolveMapCandidates(value,paths,_ => true,_ => throw new InvalidOperationException())),
            "单普通路线和已冻结候选无需再次随机抽取");
        That(!DungeonEntryPlan.TryCreate(definition,character,777,0,0,out _,out var reason,
            resolveMaze: value => DungeonCatalog.ResolveMapCandidates(value,paths,path => path != paths[101],_ => 1)) &&
            reason == "dungeon-resource-unavailable","选中正文缺失按正常入场失败处理，不替换为其他候选");
        Reject<InvalidDataException>(() => DungeonCatalog.ResolveMapCandidates(maze,paths,_ => true,limit => limit),
            "显式候选越界不发布部分布局");
        var missing = maze with { MapSpecification = [start with { Id = 999,Path = "",CandidateIds = [999,101] },boss] };
        That(DungeonCatalog.ResolveMapCandidates(missing,paths,_ => true,_ => 1).MapSpecification![0].Id == 101,
            "未选中的失效地图索引不阻断已选有效地图");
        var rawLayer = start with { Kind = "layered",CandidateIds = [101,101] };
        var layered = DungeonCatalog.ResolveMapCandidates(maze with { MapSpecification = [start with { CandidateIds = [] },boss],LayeredMaps = [rawLayer] },
            paths,_ => true,_ => throw new InvalidOperationException());
        That(layered.LayeredMaps.Select(map => map.Id).SequenceEqual([101,101]) &&
            layered.LayeredMaps.Select(map => map.LayerIndex).SequenceEqual([0,1]) && layered.LayeredMaps.Distinct().Count() == 2,
            "叠层不随机和去重，同模板按原序保留独立层身份");
        That(ReferenceEquals(DungeonCatalog.ResolveMapCandidates(layered,paths,_ => true,_ => throw new InvalidOperationException()),layered),
            "已展开的叠层再次解析不扩张或改写层序");
        var pendingPool = DungeonCatalog.ResolveMapCandidates(maze with { MapSpecification = null,LayeredMaps = [rawLayer] },
            paths,_ => true,_ => throw new InvalidOperationException());
        That(pendingPool.MapSpecification is null && pendingPool.LayeredMaps.Select(map => map.LayerIndex).SequenceEqual([0,1]),
            "缺基础显式布局时仍独立展开叠层，保留普通池补图入口");
        var generated = new DungeonMapPool([new(100,start.Path,[777],"[normal]",2),new(200,boss.Path,[777],"[boss]",16)])
            .Resolve(definition,pendingPool,_ => throw new InvalidOperationException());
        That(generated.StaticTopology.Supported && generated.LayeredMaps.Count == 2 &&
            ReferenceEquals(DungeonCatalog.ResolveMapCandidates(generated,paths,_ => true,_ => throw new InvalidOperationException()),generated),
            "补齐普通池后冻结基础房间与原序叠层，再解析不增加随机抽取");
        var layeredQuest = QuestEligibilityTests.Quest(200) with { Condition = "[clear map]",IntData = [101] };
        var active = character with { ActiveMainQuests = [new(0,200,1) { AcceptanceId = Guid.NewGuid() }] };
        var layeredBoss = maze with { QuestConnectionTokens = [new(0,0),new(0,200),new(0,-1)],
            LayeredMaps = [rawLayer with { Position = boss.Position }] };
        That(DungeonEntryPlan.TryCreate(definition with { Mazes = [layeredBoss] },active,777,200,0,out var layeredEntry,out _,quest: layeredQuest,
            resolveMaze: value => DungeonCatalog.ResolveMapCandidates(value,paths,_ => true,_ => 1)) &&
            layeredEntry!.BossMap.LayerIndex == 1,"同模板重复层的清图终点绑定原序末次目标层，不因MapID重复拒绝准入");
        var repeatedBase = layeredBoss with { MapSpecification = [start with { CandidateIds = [] },boss with { Id = 101,Path = paths[101] }] };
        That(DungeonEntryPlan.TryCreate(definition with { Mazes = [repeatedBase] },active,777,200,0,out var repeatedBaseEntry,out _,quest: layeredQuest,
            resolveMaze: value => DungeonCatalog.ResolveMapCandidates(value,paths,_ => true,_ => throw new InvalidOperationException())) &&
            repeatedBaseEntry!.BossMap.LayerIndex == 1,"基础Boss也复用目标模板时，终点仍是原序最后目标层");
        var strings = new List<string>();
        PvfToken Text(string value,byte type = 6) { strings.Add(value); return new(type,strings.Count-1); }
        PvfToken Tag(string value) => Text("["+value+"]",3);
        PvfToken[] tokens = [Tag("minimum required level"),new(0,1),Tag("maze info"),Tag("size"),new(0,2),new(0,1),
            Tag("greed"),Text("BBEE"),Tag("map specification"),Text("map"),new(0,0),new(0,0),new(0,999),new(0,100),
            Text("boss"),new(0,1),new(0,0),new(0,200),Text("layered"),new(0,0),new(0,0),new(0,101),new(0,101),
            Tag("/map specification"),Tag("start map"),new(0,0),new(0,0),Tag("/start map"),
            Tag("boss map"),new(0,1),new(0,0),Tag("/boss map")];
        var parsed = DungeonCatalog.ParseResource(777,"dungeon/candidates.dgn",tokens,id => strings[id],paths,_ => true).Mazes.Single();
        That(parsed.ResourceError is null && parsed.MapSpecification![0].CandidateIds.SequenceEqual([999,100]) &&
            parsed.LayeredMaps[0].CandidateIds.SequenceEqual([101,101]),"资源解析完整保留普通候选和叠层原序，不因未选首候选失效拒绝路线");
        var parsedChoice = DungeonCatalog.ResolveMapCandidates(parsed,paths,_ => true,_ => 1);
        That(parsedChoice.MapSpecification![0].Id == 100 && parsedChoice.LayeredMaps.Count == 2,
            "实际入场解析只发布有效选图和全部有序层，不替换失败地图");
    }

    public static void Run(PvfArchive pvf)
    {
        int checks = Count;
        var catalog = global::DfoServer.Testing.SelfTestCatalogs.Dungeons(pvf); var definition = catalog.Get(10);
        var actual = definition.Mazes.Single(maze => maze.QuestConnectionTokens.Count == 0);
        That(ReferenceEquals(catalog.ResolveOrdinaryMaze(definition,actual,_ => throw new InvalidOperationException()),actual),
            "实际龙人之塔完整显式普通路线不进入地图池或重新抽图");
        // 仅将布局改为合成二房fixture，MAP归属、greed、type和剧情排除仍取实际PVF。
        var maze = new DungeonMaze(0,2,1,"BBEE",[new(0,0)],[new(1,0)],null,[]);
        var resolved = catalog.ResolveOrdinaryMaze(definition with { Mazes = [maze,..definition.Mazes.Where(row => row.QuestConnectionTokens.Count != 0)] },maze,_ => 0);
        That(resolved.StaticTopology.Supported && resolved.MapSpecification!.Select(row => row.Id).SequenceEqual([58597,58605]),
            "实际PVF普通MAP池选择龙人之塔58597/58605，排除任务7984专用地图");
        var griefDefinition = catalog.Get(5115);
        var griefMaze = griefDefinition.Mazes.Single();
        var grief = catalog.ResolveOrdinaryMaze(griefDefinition,griefMaze,_ => throw new InvalidOperationException());
        That(griefDefinition.IsGriefTower && griefMaze.MapSpecification is null && griefMaze.GreedGrid.Cells is [{ Index: 1 }] &&
            grief.IsGriefTower && grief.Width == 1 && grief.Height == 1 && grief.Greed == griefMaze.Greed &&
            grief.StaticTopology is { Supported: true, Rooms.Count: 1, Links.Count: 0 } &&
            grief.MapSpecification is [{ Id: 100247, Path: "map/towerofgrief_down/100247_001.map" }],
            "真实D5115仅补精确100247，不改原始BB几何");
        var room = global::DfoServer.Testing.SelfTestCatalogs.Rooms(pvf).Get(grief.MapSpecification![0]);
        That(room.DeclaredType == "[normal]" && room.AiCharacters is [{ Id: 470, Faction: "[monster]", RankKind: "[boss]" }],
            "真实100247仍保留敌对Boss APC470，不为单房拓扑删除演员");
        var eligible = new Character(99,0,"GriefResource",0,new(38,2,1085,249),null) { Level = 90 };
        That(!DungeonEntryPlan.TryCreate(catalog,eligible,5115,0,0,out var entry,out string reason) && entry is null &&
            reason == "grief-tower-entry-contract-unsupported","真实目录正式入口仍明确拒绝，资源成功不创建Run");
        That(griefDefinition.DisableFatigueConsumption && griefDefinition.EnterWithoutFatigue &&
            griefDefinition.RequiredItemRows.SequenceEqual([new(10161065,1,1), new(4473,1,1)]),
            "5115 同时声明无疲劳、enter without fatigue=1，以及两行票据三元组");
        var fatigueGap = griefDefinition with { DisableFatigueConsumption = false };
        That(!DungeonEntryPlan.TryCreate(fatigueGap,eligible,5115,0,0,out _,out string fatigueReason) &&
            fatigueReason == "grief-tower-fatigue-mismatch","无疲劳标记未成立时不能再走零疲劳准入");
        var soul = catalog.Get(337);
        var soulMaze = soul.Mazes.Single();
        That(soul.IsBossRushDungeon && soulMaze is { Width: 0, Height: 0, ResourceError: not null } &&
            soulMaze.ResourceError!.Contains("maze 坐标超出 size", StringComparison.Ordinal) &&
            !DungeonEntryPlan.TryCreate(catalog,eligible with { Level = 95 },337,0,0,out var soulEntry,out string soulReason) &&
            soulEntry is null && soulReason == "boss-rush-room-contract-unproven",
            "337 保持解析失败后的 0×0，不改成 5×5，也不把 Boss 6,6 搬进格子");
        var fake = soulMaze with { MapSpecification = [new("boss", new(0,0), 1, "map/fake.map")] };
        That(!DungeonEntryPlan.TryCreate(soul with { Mazes = [fake] }, eligible with { Level = 95 }, 337, 0, 0, out _, out string fakeReason) &&
            fakeReason == "boss-rush-room-contract-unproven","宽高为 0 时补一张假地图也不能进灵魂之冢");
        var dazzlement = catalog.Get(7601);
        var dazzleCharacter = eligible with { Level = 95 };
        That(dazzlement.IsDazzlementTower && !dazzlement.RequiresGriefEntryContract && dazzlement.RequiredItemRows.IsEmpty &&
            dazzlement.DisableFatigueConsumption && dazzlement.EnterWithoutFatigue &&
            DungeonEntryPlan.TryCreate(catalog, dazzleCharacter, 7601, 0, 0, out var dazzleEntry, out string dazzleReason) &&
            dazzleReason == "resource-plan-ready" && dazzleEntry is { UsesFatigue: false, DisableFatigueConsumption: true } &&
            dazzleEntry.StartMap.Id == 450001 && dazzleEntry.BossMap.Id == 450002 && dazzleEntry.BossMap.Kind == "boss",
            "7601 按类型 0x4A 的零疲劳合同进入，房间是 450001/450002，不套 5115 的票据");
        That(!DungeonEntryPlan.TryCreate(catalog, eligible with { Level = 94 }, 7601, 0, 0, out _, out string lowLevel) &&
            lowLevel == "dungeon-level-too-low","7601 低于资源最低等级 95 时拒绝");
        var ticketed = dazzlement with { RawSections = [..dazzlement.RawSections, new DungeonRawSection("required item",
            [new(0,10161065), new(0,1), new(0,1)])] };
        That(!DungeonEntryPlan.TryCreate(ticketed, dazzleCharacter, 7601, 0, 0, out _, out string ticketReason) &&
            ticketReason == "dazzlement-tower-entry-unproven","7601 一旦带上未证明的票据行就不能进");
        That(!DungeonEntryPlan.TryCreate(dazzlement with { DisableFatigueConsumption = false }, dazzleCharacter, 7601, 0, 0, out _, out string dazzleFatigue) &&
            dazzleFatigue == "dazzlement-tower-entry-unproven","7601 的无疲劳标记不成立时不能进");
        var lukePhase = catalog.Get(5054);
        var lukeNormal = catalog.Get(5101);
        That(lukePhase.DungeonType == "dungeon" && lukeNormal.DungeonType == "dungeon" &&
            !lukePhase.IsBossRushDungeon && !lukeNormal.IsDazzlementTower &&
            lukePhase.Mazes.Count == 15 && lukeNormal.Mazes.Count == 1,
            "卢克 phase 与普通图仍是 dungeon 类型；5054 有 15 张迷宫，5101 只有一张");
        That(!DungeonEntryPlan.TryCreate(catalog, eligible, 5054, 0, 0, out _, out string lukePhaseReason) &&
            lukePhaseReason == "route-maze-ambiguous",
            "5054 没有证明过的迷宫选择器，15 张无任务连接的迷宫不能猜一张进去");
        That(!DungeonEntryPlan.TryCreate(catalog, eligible, 5101, 0, 0, out _, out string lukeNormalReason) &&
            lukeNormalReason == "difficulty-unsupported" &&
            !DungeonEntryPlan.TryCreate(catalog, eligible, 5101, 0, 2, out _, out string lukeDesignated) &&
            lukeDesignated == "difficulty-unsupported",
            "5101 指定难度 2，但没有难度表或成对的 difficulty 标记，现有合同拒绝难度 0 和 2");
        var lukeChannel = Infrastructure.ServerRuntime.LoadChannels(Path.Combine(Path.GetTempPath(), "no-such-channel-" + Guid.NewGuid().ToString("N")))
            .Channels.Single(row => row.Number == 20);
        That(lukeChannel.Type == 32 && lukeChannel.Group == "[luke_raid]" && lukeChannel.NameReference == "<chn_channel_info_037>",
            "频道 20 仍是类型 32、分组 [luke_raid]，没有另造频道类型");
        Console.WriteLine($"PASS {Count-checks} 匹配PVF普通地图池与显式路线资源断言（合成布局，非实机）");
    }
}
