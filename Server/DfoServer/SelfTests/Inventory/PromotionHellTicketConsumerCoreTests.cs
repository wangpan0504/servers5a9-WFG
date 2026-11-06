using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Towns;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing.Inventory;

internal static class PromotionHellTicketConsumerCoreTests
{
    internal const int Ticket = 880001102; // 隔离材料有意使用非3330，避免单一样本白名单。

    internal static DungeonDefinition Ordinary()
    {
        var first = new DungeonMap("map", new(0, 0), 1, "map/fixture-start.map");
        var middle = new DungeonMap("map", new(1, 0), 2, "map/fixture-middle.map");
        var boss = new DungeonMap("boss", new(2, 0), 3, "map/fixture-boss.map");
        var maze = new DungeonMaze(0, 3, 1, "BB FF EE", [first.Position], [boss.Position], [first, middle, boss], [])
        {
            HellParty = new(4, middle.Position) { RequiredItemCount = 30 },
            HellPartyPath = "map/fixture-hell.map"
        };
        return new(101, "dungeon/fixture.dgn", 91, 91, [maze], []);
    }

    internal static TownDungeonMap Map() => new(1, "worldmap/fixture.map", [new(101, -1, false)])
        { HellDungeon = true, HellItemConditions = [Ticket] };

    internal static IReadOnlySet<int> Tickets(DungeonDefinition? definition = null, TownDungeonMap? map = null)
    {
        var selected = definition ?? Ordinary();
        return HellPartyTicketConsumers.From([map ?? (Map() with { Options = [new(selected.Id, -1, false)] })], _ => selected, (_, maze) => maze);
    }

    static void StaticCandidateExistence()
    {
        var ordinary = Ordinary();
        var maze = ordinary.Mazes.Single();
        var paths = new Dictionary<int, string>
        {
            [1] = "map/fixture-start.map", [2] = "map/fixture-middle.map",
            [3] = "map/fixture-boss.map", [5] = "map/fixture-start-alternative.map"
        };
        bool Exists(string path) => paths.Values.Contains(path);
        DungeonMaze Candidate(int[] ids, int[]? weights = null, int story = -1) => maze with
        {
            MapSpecification = maze.MapSpecification!.Select(map => map.Position == maze.Starts[0]
                ? map with { Id = ids[0], Path = paths.GetValueOrDefault(ids[0]) ?? "", CandidateIds = ids,
                    CandidateWeights = weights ?? [], StoryStageIndex = story } : map).ToArray()
        };
        IReadOnlySet<int> Connected(DungeonMaze source, bool prepare)
        {
            var definition = ordinary with { Mazes = [source] };
            return HellPartyTicketConsumers.From([Map()], _ => definition, (_, row) =>
            {
                // 此fixture没有dummy/备选行或多起点/领主；候选kind均相同，
                // 直接走正式最终解析器即可覆盖静态过滤，不加载额外MAP或怪物目录。
                bool dynamic = !row.StaticTopology.Supported;
                var prepared = prepare ? DungeonCatalog.PrepareStaticConsumerMaze(row, paths, Exists, dynamic) : row;
                return DungeonCatalog.ResolveMapCandidates(prepared, paths, Exists, _ => 0);
            });
        }

        var missingFirst = Candidate([99, 1]);
        var validFirst = Candidate([1, 99]);
        That(Connected(missingFirst, false).Count == 0 && Connected(validFirst, false).Contains(Ticket),
            "原固定0样本的missing首项/反序存在性结论不同，反例真实可达");
        That(Connected(missingFirst, true).Contains(Ticket) && Connected(validFirst, true).Contains(Ticket),
            "静态消费者临时过滤后missing首项/反序均连接同一真实可用候选");
        That(Connected(Candidate([98, 99]), true).Count == 0, "全部候选缺失仍沿原解析拒绝");
        That(missingFirst.MapSpecification![0].CandidateIds.SequenceEqual([99, 1]) &&
            validFirst.MapSpecification![0].CandidateIds.SequenceEqual([1, 99]) &&
            missingFirst.MapSpecification[0].Id == 99 && missingFirst.MapSpecification[0].Path == "",
            "静态存在性检查不修改正式候选顺序、Id或Path");

        // 原静态拓扑已支持：最终解析器按权重选择，唯一存在但权重0的图不构成可达分支。
        var onlyZeroExists = Candidate([1, 99], [0, 10]);
        That(onlyZeroExists.StaticTopology.Supported && Connected(onlyZeroExists, true).Count == 0,
            "纯静态加权路径仅有零权重候选存在时仍拒绝");
        var weighted = Candidate([1, 5, 99], [0, 7, 3]);
        var filtered = DungeonCatalog.PrepareStaticConsumerMaze(weighted, paths, Exists, dynamicPool: false);
        That(filtered.MapSpecification![0].CandidateIds.SequenceEqual([5]) &&
            filtered.MapSpecification[0].CandidateWeights.SequenceEqual([7]) && Connected(weighted, true).Contains(Ticket),
            "过滤后候选与正权重对应，不把缺失或零权重编号变为可选");
        That(weighted.MapSpecification![0].CandidateIds.SequenceEqual([1, 5, 99]) &&
            weighted.MapSpecification[0].CandidateWeights.SequenceEqual([0, 7, 3]), "正式加权池完整保留");
        var unweighted = Candidate([1, 99, 5], [7, 0]);
        That(DungeonCatalog.PrepareStaticConsumerMaze(unweighted, paths, Exists, dynamicPool: false)
            .MapSpecification![0].CandidateWeights.Count == 0, "原未对齐权重按均匀选择，过滤后不能意外转为加权");

        // 原动态路径当前忽略显式权重，不借静态证明修复擅自改变该既有行为。
        var dynamicZero = Candidate([99, 1], [10, 0]);
        That(!dynamicZero.StaticTopology.Supported && Connected(dynamicZero, true).Contains(Ticket),
            "动态MapPool可选的真实图不统一套静态零权重收窄");
        That(Connected(Candidate([99, 1], story: 0), true).Count == 0 &&
            Connected(Candidate([1, 99], story: 0), true).Contains(Ticket),
            "story固定首项不能被后项替换，后项缺失也不影响固定首项");
        var requiredLayers = maze with
        {
            LayeredMaps = [new("layered", maze.Starts[0], 1, paths[1]) { CandidateIds = [1, 99] }]
        };
        That(Connected(requiredLayers, true).Count == 0, "全部必需叠层保留，不能删缺失层假装存在路线");
    }

    public static void RunCore()
    {
        StaticCandidateExistence();
        var ordinary = Ordinary();
        var maze = ordinary.Mazes.Single();
        That(Tickets().SetEquals([Ticket]), "非3330材料连接有效普通深渊正数收费入口");
        That(Tickets(map: Map() with { HellDungeon = false }).Count == 0, "非深渊区域不建立消费者");
        That(Tickets(map: Map() with { Error = "invalid" }).Count == 0, "错误选图不建立消费者");
        That(Tickets(map: Map() with { Options = [] }).Count == 0, "没有DGN选项不建立消费者");
        That(Tickets(ordinary with { Mazes = [maze with { HellParty = null }] }).Count == 0,
            "声明候选但没有深渊maze仍拒绝");
        That(Tickets(ordinary with { Mazes = [] }).Count == 0, "无maze仍拒绝");
        That(Tickets(ordinary with { Mazes = [maze with { ResourceError = "invalid" }] }).Count == 0,
            "错误maze仍拒绝");
        That(Tickets(ordinary with { Mazes = [maze with { Greed = "AA AA AA" }] }).Count == 0,
            "不可达静态拓扑仍拒绝");
        That(Tickets(ordinary with { MinimumLevel = null,
            Mazes = [maze with { HellParty = maze.HellParty! with { RequiredItemCount = 0 } }] }).Count == 0,
            "普通深渊无正数收费及等级回退依据仍拒绝");
        That(Tickets(ordinary with { Mazes = [maze with { HellParty = maze.HellParty! with { RequiredItemCount = 0 } }] }).Contains(Ticket),
            "普通深渊合法等级公式回退仍连接");
        That(Tickets(ordinary with { Mazes = [maze with { HellParty = maze.HellParty! with { Room = maze.Starts[0] } }] }).Count == 0,
            "深渊替换格与起点冲突仍拒绝");
        That(HellPartyTicketConsumers.From([Map()], _ => throw new InvalidDataException("missing"), (_, row) => row).Count == 0,
            "悬空DGN索引不能建立消费者");

        DungeonRawSection Section(string tag, params int[] values) => new(tag, values.Select(value => new PvfToken(0, value)).ToArray());
        var inferno = ordinary with
        {
            Mazes = [maze with { HellParty = null, HellPartyPath = null }],
            RawSections = [Section("inferno dungeon"), Section("hell dungeon", 1), Section("hell party required item count", 22)]
        };
        That(Tickets(inferno).Contains(Ticket), "时空裂缝不要求密门房间，沿实际副本头22张收费连接");
        That(Tickets(inferno with { RawSections = [Section("inferno dungeon"), Section("hell dungeon", 1)] }).Count == 0,
            "时空裂缝缺收费不能借普通等级公式放行");
        var cosmo = ordinary with
        {
            Id = checked((int)DungeonCosmoRewardCatalog.DungeonId),
            Mazes = [maze with { HellParty = null, HellPartyPath = null }],
            RawSections = [Section("cosmo dungeon"), Section("hell party required item count", 40),
                Section("hell party required item count add", -1, 4), Section("cosmo quest", 10112),
                Section("designate dungeon difficulty", 4), Section("use fatigue only start dungeon", 8)]
        };
        That(Tickets(cosmo).Contains(Ticket), "超星空完整收费及可达maze连接，不要求密门");
        That(Tickets(cosmo with { RawSections = cosmo.RawSections.Where(row => row.Tag != "hell party required item count add").ToArray() }).Count == 0,
            "超星空缺额外灵石合同不能建立消费者");
        That(Tickets(cosmo with { Mazes = [] }).Count == 0, "超星空只有收费头但无maze仍拒绝");
        That(Tickets(inferno with { Mazes = [] }).Count == 0, "时空裂缝只有收费头但无maze仍拒绝");
    }
}
