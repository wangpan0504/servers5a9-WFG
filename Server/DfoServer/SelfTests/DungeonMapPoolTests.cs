using System.Collections.Immutable;
using DfoServer.GameWorld.Dungeons;
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
        Console.WriteLine($"PASS {Count-checks} 普通地图池归属、方向、随机与剧情隔离断言");
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
