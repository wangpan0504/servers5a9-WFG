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
        ExplicitCandidates();
        Console.WriteLine($"PASS {Count-checks} 普通地图池归属、方向、随机与剧情隔离断言");
    }

    static void ExplicitCandidates()
    {
        var start = new DungeonMap("map",new(0,0),100,"map/start.map") { CandidateIds = [100,101,101] };
        var boss = new DungeonMap("boss",new(1,0),200,"map/boss.map");
        var maze = new DungeonMaze(0,2,1,"BBEE",[start.Position],[boss.Position],[start,boss],[]);
        var paths = new Dictionary<int,string> { [100] = start.Path,[101] = "map/start2.map",[200] = boss.Path };
        int draws = 0;
        var selected = DungeonCatalog.ResolveMapCandidates(maze,paths,_ => true,limit =>
        {
            That(limit == 2,"普通同行地图按不同ID均匀选择，不将重复声明作为权重"); draws++; return 1;
        });
        That(draws == 1 && selected.MapSpecification![0].Id == 101 && selected.MapSpecification[0].Path == paths[101] &&
            selected.MapSpecification[0].CandidateIds.SequenceEqual([101]),"入场规划冻结实际地图ID与路径，单候选不重复抽签");
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
        var catalog = DungeonCatalog.Load(pvf); var definition = catalog.Get(10);
        var actual = definition.Mazes.Single(maze => maze.QuestConnectionTokens.Count == 0);
        That(ReferenceEquals(catalog.ResolveOrdinaryMaze(definition,actual,_ => throw new InvalidOperationException()),actual),
            "实际龙人之塔完整显式普通路线不进入地图池或重新抽图");
        // 仅将布局改为合成二房fixture，MAP归属、greed、type和剧情排除仍取实际PVF。
        var maze = new DungeonMaze(0,2,1,"BBEE",[new(0,0)],[new(1,0)],null,[]);
        var resolved = catalog.ResolveOrdinaryMaze(definition with { Mazes = [maze,..definition.Mazes.Where(row => row.QuestConnectionTokens.Count != 0)] },maze,_ => 0);
        That(resolved.StaticTopology.Supported && resolved.MapSpecification!.Select(row => row.Id).SequenceEqual([58597,58605]),
            "实际PVF普通MAP池选择龙人之塔58597/58605，排除任务7984专用地图");
        Console.WriteLine($"PASS {Count-checks} 匹配PVF普通地图池与显式路线资源断言（合成布局，非实机）");
    }
}
