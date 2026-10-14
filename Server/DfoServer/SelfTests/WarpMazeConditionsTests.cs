using DfoServer.GameWorld.Dungeons;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class WarpMazeConditionsTests
{
    public static void Run()
    {
        int checks = Count;
        DungeonWarpMap[] edges = [new(new(0,0),new(1,0)),new(new(0,0),new(2,0)),
            new(new(1,0),new(0,0)),new(new(1,0),new(2,0))];
        var raw = Sections(edges);
        That(DungeonMazeConditions.Warps(raw).SequenceEqual(edges),
            "多目标传送保留有向边及声明原序，不生成反向边或选择首目标");
        Reject<InvalidDataException>(() => DungeonMazeConditions.Warps(Sections([edges[0],edges[0]])),
            "同一块内完全重复的有向边仍拒绝");
        foreach (var invalid in new DungeonRawSection[][]
        {
            [raw[0],raw[0],raw[^1]],
            [raw[0],raw[1],raw[2],raw[^1] with { Tokens = [new(0,1)] }],
            [raw[0],raw[2],raw[^1]],
            [raw[0],raw[1],raw[^1]],
            [raw[0],raw[1],raw[3],raw[^1]],
            [raw[^1]],
            raw[..^1]
        })
            Reject<InvalidDataException>(() => DungeonMazeConditions.Warps(invalid),
                "嵌套、非法结束标记、半对及未闭合的传送块不发布部分声明");
        Reject<NotSupportedException>(() => DungeonMazeConditions.Warps(
            [raw[0],raw[1] with { Tokens = [new(6,0),new(0,0)] },raw[2],raw[^1]]),
            "传送坐标只接受完整整数对");
        Reject<NotSupportedException>(() => DungeonMazeConditions.Warps(
            [raw[0],new("unknown warp field",[]),raw[^1]]),"传送块内未知字段不能被忽略");

        var declared = Parse(raw,false);
        That(declared.ResourceError is null && declared.WarpMaps.SequenceEqual(edges) &&
            declared.BossEntrance is null && declared.ConditionalMaps.Count == 0 &&
            declared.MapSpecification!.Select(map => map.Id).SequenceEqual([100,101,102]),
            "无Boss入口的多目标声明完整进入目录，不生成同格替换或改写正式地图");
        var ambiguous = Parse(raw,true);
        That(ambiguous.ResourceError == "Boss入口条件传送的同一来源存在多个目标" &&
            ambiguous.ConditionalMaps.Count == 0 && ambiguous.MapSpecification is null,
            "Boss入口多目标仍按整条迷宫错误隔离，不发布任意替换目标");
        var entrance = new DungeonBossEntrance(7,9,[new(1,4,2)]);
        var paths = Enumerable.Range(100,3).ToDictionary(id => id,id => "map/warp"+id+".map");
        var unresolvedEntrance = declared with { BossEntrance = entrance };
        Reject<InvalidDataException>(() => DungeonCatalog.ResolveMapCandidates(unresolvedEntrance,paths,_ => true,_ => 0),
            "直接Resolve在地图候选未变化时仍拒绝多目标Boss入口，不依赖ParseMaze先检查");
        var specification = declared.MapSpecification ?? throw new InvalidOperationException("合成基础地图未建立");
        var source = specification[0]; var destination = specification[1];
        var oneToOne = unresolvedEntrance with { WarpMaps = [edges[0]] };
        var resolved = DungeonCatalog.ResolveMapCandidates(oneToOne,paths,_ => true,_ => 0);
        That(resolved.ConditionalMaps is [var replacement] && replacement.IsConditional &&
            replacement.Position == source.Position && replacement.Kind == source.Kind && replacement.Id == destination.Id &&
            replacement.Path == destination.Path && resolved.WarpMaps.SequenceEqual([edges[0]]) &&
            resolved.BossEntrance == entrance && resolved.MapSpecification!.SequenceEqual(specification),
            "一对一Boss入口复验恢复唯一同格替换，保留原地图、方向和猎杀声明");
        var noWarp = declared with { BossEntrance = entrance, WarpMaps = [] };
        That(ReferenceEquals(DungeonCatalog.ResolveMapCandidates(resolved,paths,_ => true,_ => 0),resolved) &&
            ReferenceEquals(DungeonCatalog.ResolveMapCandidates(declared,paths,_ => true,_ => 0),declared) &&
            ReferenceEquals(DungeonCatalog.ResolveMapCandidates(noWarp,paths,_ => true,_ => 0),noWarp),
            "已一致的一对一、无Boss入口与无warp保持简单快路径");
        Console.WriteLine($"PASS {Count-checks} 有向多目标传送声明及Boss入口歧义断言（合成）");
    }

    static DungeonRawSection[] Sections(IReadOnlyList<DungeonWarpMap> edges) =>
        [new("warp map condition",[]),.. edges.SelectMany(edge => new DungeonRawSection[]
        {
            new("source grid pos",[new(0,edge.Source.X),new(0,edge.Source.Y)]),
            new("dest grid pos",[new(0,edge.Destination.X),new(0,edge.Destination.Y)])
        }),new("/warp map condition",[])];

    static DungeonMaze Parse(IReadOnlyList<DungeonRawSection> warps,bool entrance)
    {
        var strings = new Dictionary<int,string>();
        PvfToken Text(string value,byte type = 6) { int id = strings.Count+1; strings.Add(id,value); return new(type,id); }
        PvfToken Tag(string value) => Text("["+value+"]",3);
        var tokens = new List<PvfToken>
        {
            Tag("minimum required level"),new(0,1),Tag("basis level"),new(0,20),Tag("maze info"),
            Tag("size"),new(0,3),new(0,1),Tag("greed"),Text("BBFFEE"),Tag("map specification"),
            Text("map"),new(0,0),new(0,0),new(0,100),Text("map"),new(0,1),new(0,0),new(0,101),
            Text("boss"),new(0,2),new(0,0),new(0,102),Tag("/map specification"),
            Tag("start map"),new(0,0),new(0,0),Tag("/start map"),
            Tag("boss map"),new(0,2),new(0,0),Tag("/boss map"),Tag("/maze info")
        };
        if (entrance) tokens.AddRange([Tag("boss room entrance condition"),Text("[clear action]"),Text("[time condition]"),
            new(0,7),new(0,9),Text("[hunt monster]"),new(0,1),new(0,1),new(0,4),new(0,2)]);
        foreach (var section in warps) { tokens.Add(Tag(section.Tag)); tokens.AddRange(section.Tokens); }
        var paths = Enumerable.Range(100,3).ToDictionary(id => id,id => "map/warp"+id+".map");
        return DungeonCatalog.ParseResource(777,"dungeon/warp.dgn",tokens,id => strings[id],paths,_ => true).Mazes.Single();
    }
}
