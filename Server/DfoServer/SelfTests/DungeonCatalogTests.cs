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

        var catalog = DungeonCatalog.Load(pvf);
        That(catalog.DungeonCount == 1372, "matching dungeon index contains 1372 entries");
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
        That(JsonSerializer.Serialize(dungeon).Contains("QuestConnectionTokens", StringComparison.Ordinal), "resource definition is serializable");
        var shared = new DungeonDefinition[16];
        Parallel.For(0, shared.Length, i => shared[i] = catalog.Get(3));
        That(shared.All(d => ReferenceEquals(d, dungeon)), "thread shared cache publishes one completed immutable definition");
        That(dungeon.Mazes is IList list && list.IsReadOnly && ordinary.Starts is IList points && points.IsReadOnly && questList is IList raw && raw.IsReadOnly, "collections do not expose mutable arrays");
        var fixture = new Fixture();
        var valid = fixture.Maze();
        var parsed = fixture.Parse(valid);
        That(parsed.MinimumLevel is null && parsed.BasisLevel is null, "absent level metadata remains unknown");
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
        Unsupported(fixture.Maze(duplicateMap: true),"重复 map specification 坐标","重复地图坐标保留诊断");
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
        public PvfToken[] Maze(int width = 1, int height = 1, int startX = 0, string greed = "BB", string kind = "boss", int mapId = 1, bool duplicateMap = false, bool duplicateStart = false, bool closeMap = true)
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
            tokens.AddRange(Tag("map specification", maps.ToArray()));
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

        public DungeonDefinition Parse(PvfToken[] tokens, bool exists = true) => DungeonCatalog.ParseResource(3, "dungeon/test.dgn", tokens, Resolve, new Dictionary<int, string> { [1] = "map/test.map" }, _ => exists);
    }
}
