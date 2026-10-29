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
        That(Parse("[dummy]","BB BB") is { Kind: "[dummy]",Layouts: 2 },"MAP级dummy有独立反向注册池，不混入普通或真Boss");
        That(Parse("[normal]","NN BB") is null && Parse("[unknown]","BB BB") is null && Parse("[normal]","BB BB",-1) is null,
            "异值pair、未知地图种类和失效归属不变成正常候选");
        var maze = new DungeonMaze(7,2,1,"BBEE",[new(0,0)],[new(1,0)],null,[]);
        var story = maze with { QuestConnectionTokens = [new(0,0),new(0,100),new(0,-1)],
            MapSpecification = [new("map",new(0,0),100,"map/story.map"),new("boss",new(1,0),200,"map/story-boss.map")] };
        var definition = new DungeonDefinition(10,"dungeon/test.dgn",1,1,[maze,story],[]);
        var pool = new DungeonMapPool([new(100,"map/story.map",[10],"[normal]",2),new(101,"map/start.map",[10],"[normal]",2),
            new(201,"map/boss.map",[10],"[boss]",16),new(202,"map/wrong-owner.map",[11],"[boss]",16)]);
        var result = pool.Resolve(definition,maze,_ => 0);
        That(result.StaticTopology.Supported && result.MapSpecification!.Select(row => row.Id).SequenceEqual([100,201]) && result.SourceOrdinal == 7,
            "普通生成按MAP归属、类型和出口注册，共享图不因被任务maze引用而排除");
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
            .Select(map => map.Id).SequenceEqual([104,201]),"任务候选和叠层引用不覆盖MAP本身的普通注册资格");
        var overriddenStory = protectedStory with
        {
            OverriddenMaps = [explicitMap with { CandidateIds = [107,108] },explicitMap with { CandidateIds = [109] }]
        };
        var historyPool = new DungeonMapPool([new(107,"map/overridden1.map",[10],"[normal]",2),
            new(108,"map/overridden2.map",[10],"[normal]",2),new(109,"map/overridden3.map",[10],"[normal]",2),
            new(101,"map/start.map",[10],"[normal]",2),new(201,"map/boss.map",[10],"[boss]",16)]);
        That(protectedStory.SameLayout(overriddenStory) &&
            historyPool.Resolve(definition with { Mazes = [maze,overriddenStory] },maze,_ => 0).MapSpecification!
                .Select(map => map.Id).SequenceEqual([107,201]),
            "覆盖历史不改变布局等价性，也不把有效反向注册的共享模板逐出地图池");
        OrdinaryCandidates();
        MixedDummyCandidates();
        ExplicitCandidates();
        BossSelectionWeights();
        LostLandResources();
        GriefResource();
        // BossRush保留合法格内终点；格外Boss沿已有最远格规划且保留原声明。
        // 类型大小写和Boss格投影不改变上述合同，缺资源或非法几何仍不能补图。
        var ordinary = definition with { Mazes = [result] };
        var ready = new Character(99,0,"BossRushBoundary",0,new(38,2,1085,249),null) { Level = 95 };
        That(DungeonEntryPlan.TryCreate(ordinary,ready,10,0,0,out _,out _),"BossRush隔离用例的对照路线可正式规划");
        foreach (string type in new[] { "boss rush dungeon", " BOSS RUSH DUNGEON " })
        foreach (bool bossCell in new[] { false,true })
        {
            That(DungeonEntryPlan.TryCreate(ordinary with { DungeonType = type },ready,10,0,0,out var regular,out _,
                    bossCellFromBossMap:bossCell) && regular is { IsBossRush: true, BossMap.Position: { X: 1, Y: 0 } } &&
                regular.Maze.IsBossRush && regular.Maze.StaticTopology.Supported,
                "BossRush 的合法格内终点保留，类型大小写与Boss格投影不改变落点");
            var offGrid = result with { Bosses = [new(6,6)] };
            That(DungeonEntryPlan.TryCreate(ordinary with { DungeonType = type, Mazes = [offGrid] },ready,10,0,0,out var planned,out _,
                    bossCellFromBossMap:bossCell) && planned is { IsBossRush: true, BossMap.Position: { X: 1, Y: 0 } } &&
                planned.Maze.Bosses.SequenceEqual([new DungeonPoint(6,6)]) && planned.Maze.IsBossRush &&
                planned.Maze.StaticTopology.Supported,
                "BossRush 格外Boss取已有最远格作终点，同时保留原资源Boss坐标");
            That(!DungeonEntryPlan.TryCreate(ordinary with { DungeonType = type, Mazes = [offGrid with { MapSpecification = null }] },
                    ready,10,0,0,out var offGridRejected,out string offGridReason,bossCellFromBossMap:bossCell) &&
                offGridRejected is null && offGridReason == "boss-rush-room-contract-unproven",
                "BossRush 缺少明确地图时仍拒绝，不从格外Boss伪造地图");
            var rush = ordinary with { DungeonType = type };
            int resolved = 0;
            That(DungeonEntryPlan.TryCreate(rush,ready,10,0,0,out var entry,out string reason,
                    resolveMaze: candidate =>
                    {
                        resolved++;
                        return candidate;
                    },bossCellFromBossMap:bossCell) &&
                entry is { StartMap.Id: 100, BossMap.Id: 201, IsBossRush: true } && reason == "resource-plan-ready" &&
                resolved == 1 && entry.BossMap.Position == new DungeonPoint(1,0) &&
                entry.StartMap == result.MapSpecification![0] && entry.BossMap == result.MapSpecification[1],
                "BossRush完整几何只解析一次，保留已声明的格子内Boss与共享资源身份");
            foreach (var invalid in new[] { result with { MapSpecification = null }, result with { Width = 0, Height = 0 } })
                That(!DungeonEntryPlan.TryCreate(rush with { Mazes = [invalid] },ready,10,0,0,out var rejected,out string rejectedReason,
                        bossCellFromBossMap:bossCell) && rejected is null && rejectedReason == "boss-rush-room-contract-unproven",
                    "BossRush缺地图或非法几何仍拒绝，不因类型或Boss格投影补造资源");
        }
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

    static void LostLandResources()
    {
        var start = new DungeonPoint(0, 0); var boss = new DungeonPoint(1, 0); var zero = new DungeonPoint(0, 1);
        var maze = new DungeonMaze(0, 2, 2, "BBIIAAAA", [start], [boss],
            [new("map", start, 100, "map/start.map"), new("map", zero, 102, "map/hidden.map")], [])
            { WarpMaps = [new(start, zero)] };
        var definition = new DungeonDefinition(4000, "dungeon/lost.dgn", 25, 85, [maze], []) { DungeonType = "lost land" };
        DungeonMapPoolEntry[] entries = [new(100, "map/start.map", [4000], "[normal]", 2),
            new(102, "map/hidden.map", [4000], "[normal]", 1), new(201, "map/boss.map", [4000], "[boss]", 256)];
        var pool = new DungeonMapPool([.. entries]);
        var resolved = pool.Resolve(definition, maze, _ => throw new InvalidOperationException());
        That(resolved.MapSpecification is { Count: 3 } && resolved.MapSpecification.Single(map => map.Position == boss) is { Id: 201, Kind: "boss" } &&
            resolved.MapSpecification.Single(map => map.Position == zero) == maze.MapSpecification![1],
            "遗忘之地保留显式AA房间，缺失Boss按真实归属、种类和II布局的唯一反向注册解析");
        That(resolved.IsLostLand && resolved.StaticTopology is { Supported: true, Rooms.Count: 3, Links.Count: 0 } &&
            resolved.StaticTopology.Rooms!.Single(room => room.Map.Position == zero).ExitMask == 0 &&
            resolved.WarpMaps.SequenceEqual(maze.WarpMaps) && resolved.Greed == maze.Greed &&
            !(resolved with { IsLostLand = false }).StaticTopology.Supported,
            "遗忘之地保留AA房和单侧出口原文，不据此补普通连接或放宽其它副本");
        var right = new DungeonPoint(1, 0); var down = new DungeonPoint(0, 1);
        var hidden = new DungeonPoint(1, 1); var isolated = new DungeonPoint(2, 1); var far = new DungeonPoint(3, 1);
        var specialBoss = new DungeonPoint(3, 0);
        var route = resolved with
        {
            Width = 4, Height = 2, Greed = "JJEEAAIICCAABBEE", Bosses = [specialBoss],
            MapSpecification = [new("map", start, 100, "map/start.map"), new("map", right, 103, "map/right.map"),
                new("map", down, 104, "map/down.map"), new("map", hidden, 102, "map/hidden.map"),
                new("map", isolated, 105, "map/isolated.map"), new("map", far, 106, "map/far.map"),
                new("boss", specialBoss, 201, "map/boss.map")],
            WarpMaps = [new(start, hidden), new(new(-1, -1), isolated)]
        };
        var topology = route.StaticTopology;
        That(topology is { Supported: true, Rooms.Count: 7, Links.Count: 3 } && topology.Links!.SequenceEqual(
            [new(start, right), new(start, down), new(isolated, far)]) &&
            topology.Rooms!.Single(room => room.Map.Position == hidden).ExitMask == 0,
            "专用路线只收双方出口匹配的水平和垂直门；独立房间保留，单侧门与两类传送不进入普通连接");
        That(topology.Rooms is System.Collections.IList { IsReadOnly: true } &&
            topology.Links is System.Collections.IList { IsReadOnly: true } &&
            (route with { Greed = "JJEEAAIICCAABBBB" }).StaticTopology.Links!.Count == 2 && topology.Links!.Count == 3,
            "专用路线只读且按当前Greed重算，不沿用修改前连接");
        foreach (var invalid in new[]
        {
            route with { MapSpecification = route.MapSpecification!.Where(map => map.Position != right).ToArray() },
            route with { MapSpecification = [.. route.MapSpecification!, route.MapSpecification![0]] },
            route with { WarpMaps = [new(new(-1, -1), hidden)] },
            route with { WarpMaps = [new(new(2, 0), hidden)] },
            route with { WarpMaps = [new(hidden, hidden)] },
            route with { Starts = [new(2, 0)] }, route with { Bosses = [new(4, 0)] },
            route with { ResourceError = "invalid" }, route with { LayeredMaps = [route.MapSpecification![0]] },
            route with { Greed = "QQ" }
        })
            That(invalid.StaticTopology is { Supported: false, Rooms: null, Links: null, UnsupportedReason: not null },
                "专用拓扑仍拒绝缺图、重复图、伪传送来源、无效起终点和混合资源，不发布部分路线");
        var character = new Character(99, 0, "LostLandPlan", 0, new(40, 0, 845, 155), null) { Level = 95 };
        foreach (var blocked in new[] { definition with { Mazes = [route] },
            definition with { DungeonType = "normal", Mazes = [route] },
            definition with { Id = 3, DungeonType = " LOST LAND ", Mazes = [route] } })
            That(!DungeonEntryPlan.TryCreate(blocked, character, (uint)blocked.Id, 0, 0, out var rejected, out string reason,
                draw: _ => throw new InvalidOperationException(), resolveMaze: _ => throw new InvalidOperationException()) &&
                rejected is null && reason == "lost-land-mode-unsupported",
                "专用静态路线不能替代无疲劳、无撤退惩罚声明，不借图号或类型变体进入普通Boss结算");
        That(!DungeonEntryPlan.TryCreate(definition with { Id = 3, DungeonType = "normal", Mazes = [route] },
            character, 3, 0, 0, out _, out string markedReason) && markedReason == "lost-land-resource-unsupported",
            "其它定义也不能借已经标记的遗忘之地路线进入正式普通实例");
        var explicitOnly = new DungeonMapPool([entries[2]], entries.ToDictionary(row => row.Id, row => row.Path));
        That(explicitOnly.Resolve(definition, maze, _ => 0).MapSpecification is { Count: 3 },
            "DGN显式地图不依赖MAP反向随机池资格，路径仍来自正式目录");
        foreach (var replacement in new[] { entries[2] with { Dungeons = [3] }, entries[2] with { Kind = "[normal]" }, entries[2] with { Layouts = 2 } })
            That(ReferenceEquals(new DungeonMapPool([entries[0], entries[1], replacement]).Resolve(definition, maze, _ => 0), maze),
                "Boss归属、种类或布局不符时不猜缺失地图");
        That(ReferenceEquals(new DungeonMapPool([.. entries, entries[2] with { Id = 202, Path = "map/other.map" }]).Resolve(definition, maze, _ => 0), maze),
            "多个反向Boss候选尚无专用抽签合同，不任取首项");
        foreach (var invalid in new[] { maze with { WarpMaps = [] }, maze with { WarpMaps = [new(new(-1, -1), zero)] },
            maze with { WarpMaps = [new(new(1, 1), zero)] }, maze with { WarpMaps = [new(zero, zero)] },
            maze with { Greed = "BBIIAABB" },
            maze with { MapSpecification = [maze.MapSpecification![0], maze.MapSpecification[1] with { Path = "map/forged.map" }] },
            maze with { MapSpecification = [maze.MapSpecification![0], maze.MapSpecification[1] with { CandidateIds = [999] }] },
            maze with { Starts = [boss] }, maze with { Bosses = [new(2, 0)] } })
            That(ReferenceEquals(pool.Resolve(definition, invalid, _ => 0), invalid),
                "零格无实际传送、缺普通格、伪造来源或坐标异常时拒绝补齐");
        That(ReferenceEquals(pool.Resolve(definition with { Id = 3 }, maze, _ => 0), maze) &&
            ReferenceEquals(pool.Resolve(definition with { DungeonType = "normal" }, maze, _ => 0), maze),
            "遗忘之地解析不扩展到其它副本或资源类型");
    }

    static void OrdinaryCandidates()
    {
        var a = new DungeonPoint(0,0); var b = new DungeonPoint(1,0); var c = new DungeonPoint(2,0);
        var maze = new DungeonMaze(0,3,1,"BBFFEE",[a,c],[a,c],null,[]);
        var definition = new DungeonDefinition(777,"dungeon/maze.dgn",1,1,[maze],[]);
        var pool = new DungeonMapPool([new(100,"map/right.map",[777],"[normal]",2),new(101,"map/both.map",[777],"[normal]",32),
            new(102,"map/left.map",[777],"[normal]",16),new(200,"map/boss-right.map",[777],"[boss]",2),
            new(202,"map/boss-left.map",[777],"[boss]",16),new(300,"map/dummy-right.map",[777],"[dummy]",2),
            new(302,"map/dummy-left.map",[777],"[dummy]",16)]);
        var selected = pool.Resolve(definition,maze,_ => 0);
        That(selected.StaticTopology.Supported && selected.Starts.SequenceEqual([a]) && selected.Bosses.SequenceEqual([a]) &&
            selected.MapSpecification!.Select(map => map.Id).SequenceEqual([200,101,102]),"普通候选起点与Boss在同一轮组格，未选Boss点使用普通图");
        var forced = new DungeonMapPool([new(100,"map/right.map",[777],"[normal]",2),new(101,"map/both.map",[777],"[normal]",32),
            new(200,"map/boss-right.map",[777],"[boss]",2),new(202,"map/boss-left.map",[777],"[boss]",16)])
            .Resolve(definition,maze,_ => 0);
        That(forced.StaticTopology.Supported && forced.Bosses.SequenceEqual([c]),"优先从能补齐全图的Boss候选中抽签，不因先抽到缺普通图的组合而拒绝");
        var dummy = pool.Resolve(definition with { RawSections = [new("dummy appear count",[new(0,2)])] },maze,_ => 0);
        That(dummy.StaticTopology.Supported && dummy.DummyStages.Select(map => map.Position).SequenceEqual([a,c,a]) &&
            dummy.DummyStages.Select(map => map.Id).SequenceEqual([300,302,200]) &&
            dummy.DummyStages.Select(map => map.DummyStageIndex).SequenceEqual([0,1,2]),
            "无显式DGN地图表也按反向dummy池构成两次假Boss与最终真Boss，非相邻阶段可重用坐标");
        var hell = maze with { HellParty = new(400,b),HellPartyPath = "map/hell.map" };
        var hellSelected = pool.Resolve(definition,hell,_ => 1,true);
        That(hellSelected.StaticTopology.Supported && hellSelected.Starts.SequenceEqual([c]) && hellSelected.Bosses.SequenceEqual([c]),
            "深渊与普通共用随机组格，不固定取第一起点或Boss");
        var conflicting = hell with { Starts = [b,a],Bosses = [b,c] };
        var safeHell = pool.Resolve(definition,conflicting,_ => 0,true);
        That(safeHell.Starts.SequenceEqual([a]) && safeHell.Bosses.SequenceEqual([c]),"深渊有合法候选时避开密门占位，不因抽到冲突点直接拒绝");
        var character = new Character(99,0,"MazeFixture",0,new(38,2,1085,249),null) { Level = 90 };
        That(DungeonEntryPlan.TryCreate(definition with { Mazes = [hell] },character,777,0,0,out var plan,out _,hellParty:true,
            resolveMaze: value => pool.Resolve(definition,value,_ => 0,true)) && plan!.HellParty?.Room == b &&
            plan.Maze.MapSpecification!.Single(map => map.Position == b).Id == 400,"反向生成深渊路线保留密门图替换及原maze身份");
    }

    static void MixedDummyCandidates()
    {
        var start = new DungeonMap("map",new(0,0),100,"map/start.map");
        var middle = new DungeonMap("map",new(1,0),101,"map/middle.map") { CandidateIds = [101,301] };
        var end = new DungeonMap("map",new(2,0),302,"map/end-dummy.map") { CandidateIds = [302,102] };
        var bosses = new[] { new DungeonMap("boss",middle.Position,201,"map/middle-boss.map"),
            new DungeonMap("boss",end.Position,202,"map/end-boss.map") };
        var maze = new DungeonMaze(0,3,1,"BBFFEE",[start.Position],[middle.Position,end.Position],[start,..bosses],[])
            { MapAlternatives = [middle,end] };
        var definition = new DungeonDefinition(778,"dungeon/mixed-dummy.dgn",1,1,[maze],
            [new("dummy appear count",[new(0,2)])]);
        ImmutableArray<DungeonMapPoolEntry> rows = [new(999,"map/unrelated-dummy.map",[778],"[dummy]",48),
            new(100,start.Path,[778],"[normal]",2),new(101,middle.Path,[778],"[normal]",32),new(102,"map/end.map",[778],"[normal]",16),
            new(201,bosses[0].Path,[778],"[boss]",32),new(202,bosses[1].Path,[778],"[boss]",16),
            new(301,"map/middle-dummy.map",[778],"[dummy]",32),new(302,end.Path,[778],"[dummy]",16)];
        var pool = new DungeonMapPool(rows);
        foreach (bool last in new[] { false,true })
        {
            var selected = pool.Resolve(definition,maze,count => last ? count-1 : 0);
            var initial = last ? end.Position : middle.Position;
            var other = last ? middle.Position : end.Position;
            That(selected.StaticTopology.Supported && selected.Bosses.SequenceEqual([initial]) &&
                selected.DummyStages.Select(map => map.Id).SequenceEqual(last ? new[] { 302,301,202 } : new[] { 301,302,201 }),
                "混合候选按当前用途选择对应槽位的dummy和最终Boss，不从同出口的其他任务模板抽取");
            That(selected.MapSpecification!.Single(map => map.Position == other).Id == (last ? 101 : 102) &&
                selected.MapSpecification!.Single(map => map.Position == other).Kind == "map",
                "未标记格只抽本位置normal候选，普通图与dummy在原列表的前后顺序不影响结果");
        }
        That(middle.CandidateIds.SequenceEqual([101,301]) && end.CandidateIds.SequenceEqual([302,102]) &&
            maze.MapAlternatives.SequenceEqual([middle,end]),"选图不修改共享原始候选或其他账号的规划输入");
        var fallbackMaze = maze with { MapAlternatives = [middle with { CandidateIds = [301] },end] };
        var fallback = new DungeonMapPool(rows.Where(row => row.Id != 101).ToImmutableArray()).Resolve(definition,fallbackMaze,count => count-1);
        That(fallback.StaticTopology.Supported && fallback.MapSpecification!.Single(map => map.Position == middle.Position) is
            { Kind:"map",Id:301,DummyStageIndex:-1 } && fallback.DummyStages[0].Id == 302,
            "没有普通模板时保留dummy场景的普通身份，由演员规划排除假Boss，不拒绝整条可通关路线");
        var hellMaze = maze with { HellParty = new(400,middle.Position),HellPartyPath = "map/hell.map" };
        var hell = pool.Resolve(definition,hellMaze,_ => 0,true);
        That(hell.StaticTopology.Supported && hell.Bosses.SequenceEqual([end.Position]) &&
            hell.DummyStages.All(map => map.Position == end.Position) && hell.MapSpecification!.Single(map => map.Position == middle.Position).Id == 101,
            "深渊密门不成为dummy目标，密门替换前仍使用普通模板");
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
                reason == "grief-tower-admission-required","资源几何不能让普通、显式或回放绕过未知票据/疲劳准入合同");
        }
    }

    static void BossSelectionWeights()
    {
        var start = new DungeonMap("map", new(0, 0), 100, "map/start.map");
        var boss = new DungeonMap("boss", new(1, 0), 92689, "map/boss1.map")
            { CandidateIds = [92689, 92690, 92697], CandidateWeights = [0, 0, 100] };
        var maze = new DungeonMaze(0, 2, 1, "BBEE", [start.Position], [boss.Position], [start, boss], []);
        var paths = new Dictionary<int, string>
            { [100] = start.Path, [92689] = boss.Path, [92690] = "map/boss2.map", [92697] = "map/white.map" };
        foreach (int[] weights in new[] { new[] { -1, 0, 101 }, new[] { 0, 0, 0 } })
        {
            var invalid = maze with { MapSpecification = [start, boss with { CandidateWeights = weights }] };
            Reject<InvalidDataException>(() => DungeonCatalog.ResolveMapCandidates(invalid, paths, _ => true,
                _ => throw new InvalidOperationException("非法权重不应抽样")), "负权重和全零候选仍拒绝且不抽样");
        }
        var character = new Character(99, 0, "StrongBossWeights", 0, new(38, 2, 1085, 249), null);
        var definition = new DungeonDefinition(777, "dungeon/weighted.dgn", 1, 1, [maze], []);
        foreach (int roll in new[] { 0, 99 })
        {
            int draws = 0;
            int Draw(int bound) { That(bound == 100, "强力药水保留实际总权重100"); draws++; return roll; }
            That(DungeonEntryPlan.TryCreate(definition, character, 777, 0, 0, out var entry, out string reason,
                    resolveMaze: value => DungeonCatalog.ResolveMapCandidates(value, paths, _ => true, Draw)) &&
                entry!.BossMap.Id == 92697 && entry.BossMap.Path == paths[92697] && draws == 1,
                "合法强力0/0/100权重可完整规划入场，零权重不抽中：" + reason);
        }
        That(boss.CandidateWeights.SequenceEqual([0, 0, 100]), "入场不改共享强力权重");
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
        var hellTokens = new PvfToken[] { Tag("maze info"),Tag("size"),new(0,2),new(0,1),Tag("greed"),Text("BBEE"),
            Tag("start map"),new(0,0),new(0,0),Tag("/start map"),Tag("boss map"),new(0,1),new(0,0),Tag("/boss map"),
            Tag("hell dungeon"),new(0,1),Tag("seal door map index"),new(0,200),Tag("seal door pos"),new(0,1),new(0,0) };
        That(DungeonCatalog.ParseResource(777,"dungeon/hell-pool.dgn",hellTokens,id => strings[id],paths,_ => true).Mazes.Single() is
            { ResourceError: null,MapSpecification: null,HellParty.MapId: 200 },
            "深渊密门声明不依赖DGN显式地图表，后续仍可通过反向池组格");
        var rushPaths = new Dictionary<int,string> { [100]="map/r00.map",[101]="map/r10.map",[102]="map/r01.map",[103]="map/r11.map" };
        PvfToken[] rushTokens = [Tag("minimum required level"),new(0,1),Tag("dungeon type"),Text("boss rush dungeon"),
            Tag("maze info"),Tag("size"),new(0,2),new(0,2),Tag("greed"),Text("BBBBEEEE"),
            Tag("map specification"),Text("map"),new(0,0),new(0,0),new(0,100),Text("map"),new(0,1),new(0,0),new(0,101),
            Text("map"),new(0,0),new(0,1),new(0,102),Text("map"),new(0,1),new(0,1),new(0,103),Tag("/map specification"),
            Tag("start map"),new(0,0),new(0,0),Tag("/start map"),Tag("boss map"),new(0,3),new(0,3),Tag("/boss map")];
        var rush = DungeonCatalog.ParseResource(778,"dungeon/boss-rush.dgn",rushTokens,id => strings[id],rushPaths,_ => true);
        var rushMaze = rush.Mazes.Single();
        var rushCharacter = new Character(99,0,"BossRush",0,new(38,2,1085,249),null) { Level = 95 };
        That(rush.IsBossRushDungeon && rushMaze is { Width: 2, Height: 2, ResourceError: null } &&
            rushMaze.Starts.SequenceEqual([new DungeonPoint(0,0)]) && rushMaze.Bosses.SequenceEqual([new DungeonPoint(3,3)]) &&
            DungeonEntryPlan.TryCreate(rush,rushCharacter,778,0,0,out var rushEntry,out string rushReason) &&
            rushReason == "resource-plan-ready" && rushEntry is { StartMap.Id: 100, BossMap.Id: 103, BossMap.Kind: "boss" } &&
            rushEntry.BossMap.Position == new DungeonPoint(1,1) && rushMaze.Bosses.SequenceEqual([new DungeonPoint(3,3)]),
            "BossRush 保留格子外 Boss，入场用最远格作结算房，不把 Boss 坐标搬进网格");
        var noType = DungeonCatalog.ParseResource(778,"dungeon/off-grid.dgn",
            [..rushTokens.Where((_,index) => index is not (2 or 3))],id => strings[id],rushPaths,_ => true).Mazes.Single();
        That(noType is { Width: 0, Height: 0, ResourceError: not null } &&
            noType.ResourceError!.Contains("maze 坐标超出 size", StringComparison.Ordinal),
            "非 BossRush 的格子外 Boss 仍按解析失败处理");
        var fakeRush = rushMaze with { Width = 0, Height = 0, ResourceError = "maze 坐标超出 size",
            MapSpecification = [new("boss", new(0,0), 1, "map/fake.map")] };
        That(!DungeonEntryPlan.TryCreate(rush with { Mazes = [fakeRush] }, rushCharacter, 778, 0, 0, out _, out string fakeRushReason) &&
            fakeRushReason == "boss-rush-room-contract-unproven","宽高为 0 时补一张假地图也不能进 BossRush");
    }

    public static void Run(PvfArchive pvf)
    {
        int checks = Count;
        var catalog = global::DfoServer.Testing.SelfTestCatalogs.Dungeons(pvf); var definition = catalog.Get(10);
        var lostLand = catalog.Get(4000);
        var lostMaze = catalog.ResolveOrdinaryMaze(lostLand, lostLand.Mazes[0], _ => 0);
        var lostTopology = lostMaze.StaticTopology;
        That(lostMaze.IsLostLand && lostTopology is { Supported: true, Rooms.Count: 87, Links.Count: 81 } &&
            lostTopology.Rooms!.Single(room => room.Map.Id == 38009).ExitMask == 0 &&
            lostTopology.Links!.All(link => link.First != lostMaze.Bosses[0] && link.Second != lostMaze.Bosses[0]),
            "真实遗忘之地87图与81条双向门可解析，AA房及独立Boss均保留，传送不补普通连接");
        var cosmoDefinition = catalog.Get(7191);
        That(cosmoDefinition.IsCosmoDungeon && cosmoDefinition.CosmoInvitationCount == 40 && cosmoDefinition.CosmoStoneCount == 4 &&
            cosmoDefinition.CosmoQuestId == 10112 && cosmoDefinition.StartFatigue == 8 && cosmoDefinition.TryGetDesignatedDifficulty(out byte cosmoDifficulty) &&
            cosmoDifficulty == 4, "超星空准入按PVF解析40邀请函、4灵石、10112任务、固定难度4和8疲劳");
        var cosmoMaze = cosmoDefinition.Mazes[0];
        That(cosmoMaze.ResourceError is null && cosmoMaze.MapSpecification is { Count: 4 } &&
            cosmoMaze.MapSpecification.Single(map => map.Position == new DungeonPoint(2,0)) is
                { Kind: "boss", CandidateIds: [92689,92690,92697], CandidateWeights: [45,45,10] },
            "超星空PVF boss_selection_probability解析为权重终点，不再标记maze不支持");
        foreach (var result in new[] { (Roll:0, Boss:92689),(Roll:45, Boss:92690),(Roll:90, Boss:92697) })
        {
            var entryCharacter = new Character(98,0,"CosmoPlan",0,new(54,2,0,0),null) { Level = 95 };
            int calls = 0;
            int Draw(int bound) { calls++; return calls == 1 ? 0 : result.Roll; }
            That(DungeonEntryPlan.TryCreate(catalog,entryCharacter,7191,0,4,out var cosmoEntry,out var cosmoReason,
                    draw: Draw,hellParty: true) && cosmoEntry is { IsCosmo: true, IsOrdinary: true, HellParty: null, StartFatigue: 8 } &&
                cosmoEntry.BossMap.Id == result.Boss && cosmoEntry.Maze.StaticTopology.Supported && cosmoReason == "resource-plan-ready",
                "超星空正式入口随机抽六个可选迷宫并按45/45/10冻结终点 " + result.Boss + "/" + cosmoReason);
        }
        var actual = definition.Mazes.Single(maze => maze.QuestConnectionTokens.Count == 0);
        foreach (var result in new[] { (Roll: 0, Boss: 92689), (Roll: 39, Boss: 92689), (Roll: 40, Boss: 92690),
            (Roll: 79, Boss: 92690), (Roll: 80, Boss: 92697), (Roll: 99, Boss: 92697) })
        {
            var boosted = catalog.ResolveOrdinaryMaze(cosmoDefinition, cosmoMaze, _ => result.Roll, true,
                new Dictionary<int,int> { [92689] = 40, [92690] = 40, [92697] = 20 });
            That(boosted.MapSpecification!.Single(map => map.Kind == "boss").Id == result.Boss,
                "白斯追击按40/40/20完整边界冻结终点，不改变原PVF候选或其它房间");
        }
        That(cosmoMaze.MapSpecification!.Single(map => map.Kind == "boss").CandidateWeights.SequenceEqual([45,45,10]),
            "角色药效不能污染共享迷宫或其他玩家的原权重");
        foreach (int roll in new[] { 0, 99 })
        {
            var strongCharacter = new Character(98, 0, "StrongCosmoPlan", 0, new(54, 2, 0, 0), null) { Level = 95 };
            int draws = 0;
            int Draw(int bound) { draws++; return bound == 100 ? roll : 0; }
            That(DungeonEntryPlan.TryCreate(catalog, strongCharacter, 7191, 0, 4, out var strongEntry, out string strongReason,
                    draw: Draw, hellParty: true,
                    bossWeights: new Dictionary<int, int> { [92689] = 0, [92690] = 0, [92697] = 100 }) &&
                strongEntry is { IsCosmo: true, BossMap.Id: 92697, StartFatigue: 8 } && draws == 2,
                "真实超星空接受强力0/0/100，保留选迷宫与终点各一次抽样：" + strongReason);
        }
        That(ReferenceEquals(catalog.ResolveOrdinaryMaze(definition,actual,_ => throw new InvalidOperationException()),actual),
            "实际龙人之塔完整显式普通路线不进入地图池或重新抽图");
        // 仅将布局改为合成二房fixture，MAP归属、greed和type仍取实际PVF。
        var maze = new DungeonMaze(0,2,1,"BBEE",[new(0,0)],[new(1,0)],null,[]);
        var resolved = catalog.ResolveOrdinaryMaze(definition with { Mazes = [maze,..definition.Mazes.Where(row => row.QuestConnectionTokens.Count != 0)] },maze,_ => 0);
        That(resolved.StaticTopology.Supported && resolved.MapSpecification!.Select(row => row.Id).SequenceEqual([58597,58605]),
            "实际PVF按MAP归属、类型及出口选择龙人之塔58597/58605，不依赖DGN显式地图表");
        var griefDefinition = catalog.Get(5115);
        var griefMaze = griefDefinition.Mazes.Single();
        var grief = catalog.ResolveOrdinaryMaze(griefDefinition,griefMaze,_ => throw new InvalidOperationException());
        That(griefDefinition.IsGriefTower && griefMaze.MapSpecification is null && griefMaze.GreedGrid.Cells is [{ Index: 1 }] &&
            grief.IsGriefTower && grief.Width == 1 && grief.Height == 1 && grief.Greed == griefMaze.Greed &&
            grief.StaticTopology is { Supported: true, Rooms.Count: 1, Links.Count: 0 } &&
            grief.MapSpecification is [{ Id: 100247, Path: "map/towerofgrief_down/100247_001.map" }],
            "真实D5115仅补精确100247，不改原始BB几何");
        var room = global::DfoServer.Testing.SelfTestCatalogs.Rooms(pvf).Get(grief.MapSpecification![0]);
        That(room.DeclaredType == "[normal]" && room.AiCharacters.Where(row => !DungeonCompanion.IsCompanionRow(row.Id)).ToArray() is [{ Id: 470, Faction: "[monster]", RankKind: "[boss]" }],
            "真实100247仍保留敌对Boss APC470，不为单房拓扑删除演员");
        var eligible = new Character(99,0,"GriefResource",0,new(38,2,1085,249),null) { Level = 90 };
        That(!DungeonEntryPlan.TryCreate(catalog,eligible,5115,0,0,out var entry,out string reason) && entry is null &&
            reason == "grief-tower-admission-required","真实目录正式入口仍明确拒绝，资源成功不创建Run");
        That(griefDefinition.DisableFatigueConsumption && griefDefinition.EnterWithoutFatigue &&
            griefDefinition.RequiredItemRows.SequenceEqual([new(10161065,1,1), new(4473,1,1)]),
            "5115 同时声明无疲劳、enter without fatigue=1，以及两行票据三元组");
        var fatigueGap = griefDefinition with { DisableFatigueConsumption = false };
        That(!DungeonEntryPlan.TryCreate(fatigueGap,eligible,5115,0,0,out _,out string fatigueReason) &&
            fatigueReason == "grief-tower-fatigue-mismatch","无疲劳标记未成立时不能再走零疲劳准入");
        var soul = catalog.Get(337);
        var soulMaze = soul.Mazes.Single();
        That(soul.IsBossRushDungeon && soul.TimeoutSeconds == 120 && soulMaze is { Width: 5, Height: 5, ResourceError: null, IsBossRush: true } &&
            soulMaze.Starts.SequenceEqual([new DungeonPoint(2,2)]) && soulMaze.Bosses.SequenceEqual([new DungeonPoint(6,6)]) &&
            soulMaze.MapSpecification is { Count: 25 } && soulMaze.StaticTopology.Supported &&
            DungeonEntryPlan.TryCreate(catalog,eligible with { Level = 95 },337,0,0,out var soulEntry,out string soulReason) &&
            soulReason == "resource-plan-ready" && soulEntry is { IsBossRush: true, TimeoutMilliseconds: 120000,
                StartMap.Position: { X: 2, Y: 2 }, BossMap.Position: { X: 4, Y: 4 }, BossMap.Kind: "boss" } &&
            soulEntry.Maze.StaticTopology.Supported && soulMaze.Bosses.SequenceEqual([new DungeonPoint(6,6)]),
            "337 保留 5×5 与格子外 Boss 6,6；拓扑不要求 6,6 落格，入场用最远格作结算房");
        var fake = soulMaze with { Width = 0, Height = 0, ResourceError = "maze 坐标超出 size",
            MapSpecification = [new("boss", new(0,0), 1, "map/fake.map")] };
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
            DungeonEntryPlan.TryCreate(catalog, eligible, 5101, 0, 2, out var lukeEntry, out _) &&
            lukeEntry is { Difficulty: 2,StartMap.Id: 55916,BossMap.Id: 55918 },
            "5101 按唯一指定难度2进入原普通路线，仍拒绝难度0");
        var lukeChannel = Infrastructure.ServerRuntime.LoadChannels(Path.Combine(Path.GetTempPath(), "no-such-channel-" + Guid.NewGuid().ToString("N")))
            .Channels.Single(row => row.Number == 20);
        That(lukeChannel.Type == 32 && lukeChannel.Group == "[luke_raid]" && lukeChannel.NameReference == "<chn_channel_info_037>",
            "频道 20 仍是类型 32、分组 [luke_raid]，没有另造频道类型");
        Console.WriteLine($"PASS {Count-checks} 匹配PVF普通地图池与显式路线资源断言（合成布局，非实机）");
    }
}
