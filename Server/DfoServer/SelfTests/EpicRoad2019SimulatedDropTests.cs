using DfoServer.Game.Dungeons;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;
using DfoServer.Infrastructure;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class EpicRoad2019SimulatedDropTests
{
    static ItemTemplate Equipment(int id) => new(id, ItemKind.Equipment, 0, 95, 37, 1, 1, 4, 101,
        "[free]", ushort.MaxValue, 0, "合成模拟史诗") { CreationRate = 1 };

    public static void RunCore()
    {
        int before = Count;
        var map = new DungeonMap("boss", new(2, 0), 400000459, "map/simulated-boss.map");
        var start = map with { Kind = "map", Id = 400000457, Position = new(0, 0) };
        var maze = new DungeonMaze(0, 3, 1, "", [start.Position], [map.Position], [start, map], []);
        var entry = new DungeonEntryPlan(1, 0, 400000097, 0, 0, 0, maze, start, map);
        var source = new DungeonRoomMonster(407000595, [407000595, 1], "[fixed]", "[boss]", "monster/simulated.mob");
        var monster = new DungeonPlannedMonster(0, 10, source.MonsterId, 95, source) { SpawnType = 3, SourceMap = map };
        var pool = new DungeonItemDropPool([new DungeonWeightedDropItem(Equipment(9501), 2), new(Equipment(9502), 3)]);
        var policy = new EpicRoad2019SimulatedDropPolicy(pool, true);
        var baseline = new DungeonDropGenerationPlan("pvf-existing", []);
        That(ReferenceEquals(new EpicRoad2019SimulatedDropPolicy(pool, false).Extend(baseline, entry, monster), baseline),
            "模拟开关关闭时保持原计划");
        foreach (uint id in new uint[] { 400000097, 400000098 })
        {
            var plan = policy.Extend(baseline, entry with { DungeonId = id }, monster);
            That(plan.ResourceSource == "pvf-existing;epic-road-2019-simulated-v2" && plan.MaximumObjects == 1 &&
                plan.Trials is [{ Threshold: 1, MinimumCount: 1, MaximumCount: 1,
                    Attempts: 1, Category: DungeonDropCategory.Equipment } trial] &&
                trial.ProbabilityDenominator == (id == 400000097 ? 5 : 1), "普通末房模拟史诗20%，超星河仍固定一件");
            foreach (bool last in new[] { false, true })
            {
                int drawIndex = 0;
                That(plan.Generate(bound => drawIndex++ == 0 ? 0 : last ? bound - 1 : 0, DateTimeOffset.UnixEpoch) is [{ Item.Count: 1 } content] &&
                    content.Item!.ItemId == (last ? 9502 : 9501), "抽样两端实际创建一件完整装备");
            }
            if (id == 400000097)
                That(plan.Generate(_ => 1, DateTimeOffset.UnixEpoch).IsEmpty, "普通星河史诗骰1至4未命中，仅骰0命中");
        }
        var activityMaterial = Equipment(490013716) with { Kind = ItemKind.Material, StackLimit = 100, Rarity = 0 };
        var retainedEquipment = new DungeonItemDropPool([new DungeonWeightedDropItem(Equipment(9503), 1)]);
        var activityMaterials = new DungeonItemDropPool([new DungeonWeightedDropItem(activityMaterial, 1)]);
        var materialTrial = new DungeonDropTrial(2, activityMaterials, 1, 1, ProbabilityDenominator: 10,
            ItemCounts: System.Collections.Immutable.ImmutableDictionary<int,int>.Empty.Add(490013716, 2), IndividualObjects: true);
        var materialPlan = new DungeonDropGenerationPlan("materials", [materialTrial]);
        var easy = policy.Extend(materialPlan, entry with { DungeonId = 400000097 }, monster);
        That(easy.Trials[0] is { Threshold: 2, ProbabilityDenominator: 10, IndividualObjects: true } &&
            easy.Trials[0].ItemCounts![490013716] == 10 && easy.Trials[0].MinimumCount == 1 && easy.Trials[0].MaximumCount == 1,
            "普通星河两种兑换材料数量乘五，保留独立骰和物件模式");
        var hard = policy.Extend(materialPlan, entry with { DungeonId = 400000098 }, monster);
        That(hard.Trials[0] is { Threshold: 6, ProbabilityDenominator: 10 } && hard.Trials[0].ItemCounts![490013716] == 2,
            "超星河兑换材料概率乘三，数量保持");
        var capped = policy.Extend(new("cap", [materialTrial with { Threshold = 4 }, materialTrial with { Threshold = 0 }]),
            entry with { DungeonId = 400000098 }, monster);
        That(capped.Trials[0].Threshold == 10 && capped.Trials[1].Threshold == 0,
            "材料概率封顶且不从零新增来源");
        var mixed = new DungeonItemDropPool([new DungeonWeightedDropItem(activityMaterial, 1),
            new( activityMaterial with { Id = 490013727 }, 1)]);
        var mixedTrial = materialTrial with { Items = mixed, ItemCounts = null };
        That(ReferenceEquals(policy.Extend(new("mixed", [mixedTrial]), entry, monster).Trials[0], mixedTrial),
            "包含非目标材料的混池不改变整池概率和数量");
        var originalRewards = new DungeonDropGenerationPlan("original", [
            new(1, retainedEquipment, 1, 1, ProbabilityDenominator: 1),
            new(1, activityMaterials, 1, 1, ProbabilityDenominator: 1)]);
        var closed = policy.Extend(originalRewards, entry, monster).Generate(_ => 0, DateTimeOffset.UnixEpoch, epicRoad2019Available: false);
        That(closed is [{ Item.ItemId: 9503 }], "关闭后不新增活动材料和模拟装备，原普通掉落保留");
        foreach (var invalid in new[] { entry with { DungeonId = 3 }, entry with { IsPractice = true },
            entry with { HellParty = new(1, new(0, 0)) }, entry with { IsInfernoParty = true },
            entry with { IsHellLegend = true }, entry with { IsCosmo = true }, entry with { IsTutorial = true },
            entry with { RouteQuestId = 10 }, entry with { DisableItemDrops = true } })
            That(ReferenceEquals(policy.Extend(baseline, invalid, monster), baseline), "非活动或特殊模式不能借用模拟奖励");
        foreach (var invalid in new[] { monster with { SourceMap = start }, monster with { SourceMap = null },
            monster with { IsFriendly = true }, monster with { IsBlocking = false }, monster with { IsDummyMapBoss = true },
            monster with { Source = source with { RankKind = "[normal]" } },
            monster with { Source = source with { Modifiers = ["[dummy]"] } }, monster with { SpawnType = 5 } })
            That(ReferenceEquals(policy.Extend(baseline, entry, invalid), baseline), "非末房、非领主、友方和脚本演员保持原计划");
        var originalTrial = new DungeonDropTrial(1, pool, 1, 1, ProbabilityDenominator: 1);
        var original = new DungeonDropGenerationPlan("original", [originalTrial])
            { ExcludeLowEquipment = true, ExcludeLowMaterials = true, OrdinaryLukeDailySource = 7 };
        var extended = policy.Extend(original, entry, monster);
        That(extended.Trials.Length == 2 && ReferenceEquals(extended.Trials[0], originalTrial) &&
            extended.ExcludeLowEquipment && extended.ExcludeLowMaterials && extended.OrdinaryLukeDailySource == 7,
            "追加奖励保留原通道、引用和计划附加标记");
        Reject<InvalidDataException>(() => new EpicRoad2019SimulatedDropPolicy(new DungeonItemDropPool(Array.Empty<ItemTemplate>()), true), "空模拟池拒绝");
        foreach (var bad in new[] { Equipment(1) with { MinimumLevel = 90 }, Equipment(1) with { Rarity = 3 },
            Equipment(1) with { Kind = ItemKind.Material }, Equipment(1) with { StackLimit = 2 } })
            Reject<InvalidDataException>(() => new EpicRoad2019SimulatedDropPolicy(new DungeonItemDropPool([new DungeonWeightedDropItem(bad, 1)]), true),
                "模拟池仅接受可创建的95级史诗装备");
        Console.WriteLine($"PASS {Count - before} 星河裂缝模拟掉落领域断言");
    }

    internal static void RunCache(DungeonDropRuleCatalog rules, DungeonEntryPlan basis)
    {
        var gear = Equipment(9501);
        var pool = new DungeonItemDropPool([new DungeonWeightedDropItem(gear, 1)]);
        var policy = new EpicRoad2019SimulatedDropPolicy(pool, true);
        var resource = new DungeonResourceDropPolicy(rules, DungeonItemDropCatalog.Create([]),
            id => new(id, "monster/simulated.mob", null, null), _ => gear, epicRoadSimulated: policy);
        var map = basis.BossMap;
        var entry = basis with { DungeonId = 400000097 };
        var source = new DungeonRoomMonster(407000595, [407000595, 1], "[fixed]", "[boss]", "monster/simulated.mob");
        var monster = new DungeonPlannedMonster(0, 10, source.MonsterId, 1, source) { SpawnType = 3, SourceMap = map };
        foreach (bool eligibleFirst in new[] { true, false })
        {
            var prepare = resource.PrepareEntry(entry);
            var outside = monster with { SourceMap = map with { Id = 42 } };
            _ = prepare(eligibleFirst ? monster : outside);
            That(prepare(monster).ResourceSource.Contains(EpicRoad2019SimulatedDropPolicy.Rule) &&
                !prepare(outside).ResourceSource.Contains(EpicRoad2019SimulatedDropPolicy.Rule), "末房资格参与缓存身份，两种顺序均不串奖");
        }
        var control = new DungeonDropControlSettings();
        control.Replace([new(DungeonDropScope.All, [], new(Equipment: 0))], true);
        var controlled = new DungeonResourceDropPolicy(rules, DungeonItemDropCatalog.Create([]),
            id => new(id, "monster/simulated.mob", null, null), _ => gear, dropControl: control, epicRoadSimulated: policy);
        That(controlled.PrepareEntry(entry)(monster).Trials[^1] is { Threshold: 1, ProbabilityDenominator: 5 },
            "普通模拟20%独立于通用掉率倍率");
    }

    public static void RunResources(PvfArchive pvf, ItemResources items)
    {
        int before = Count;
        var pool = DungeonHellEpicCatalog.Load(pvf).Pool(54, 2, items.Items.Get);
        var policy = new EpicRoad2019SimulatedDropPolicy(pool, true);
        var catalog = DungeonCatalog.Load(pvf, EpicRoadResourcePaths.Dungeons, EpicRoadResourcePaths.Maps);
        var rooms = DungeonRoomCatalog.Load(pvf);
        var seen = new HashSet<int>();
        var character = new Character(1, 0, "EpicDrop", 1, new(1, 0, 1, 1, 0), null) { Level = 95 };
        foreach (int id in new[] { 400000097, 400000098 })
        foreach (var maze in catalog.Get(id).Mazes)
        {
            var map = maze.MapSpecification!.Single(row => row.Kind == "boss");
            That(DungeonEntryPlan.TryCreate(catalog.Get(id) with { Mazes = [maze] }, character, (uint)id, 0, 2,
                out var entry, out var reason), "真实入场冻结两图每条路线：" + reason);
            var monsters = DungeonMonsterPlan.FromRoom(rooms.Get(map, requirePassiveDefinitions: false), 1, 10, 95).Monsters;
            var boss = monsters.Single(row => row.IsBoss);
            seen.Add(boss.MonsterId);
            var generated = policy.Extend(new("resource-test", []), entry!, boss).WithDisjointRestrictions(entry!.DisjointForbiddenDropRarities)
                .Generate(_ => 0, DateTimeOffset.UtcNow);
            That(generated is [{ Item.Count: 1, Item.DisjointForbidden: false }] &&
                pool.Entries.Any(row => row.Template.Id == generated[0].Item!.ItemId), "本服每条末房路线生成一件95史诗且解除来源禁止分解");
        }
        That(seen.SetEquals([407000590, 407000595, 407000596, 407000601]), "真实四个末房领主均覆盖");
        var service = new InventoryDisjointService(items.Items, items.Disjoint, items.GoldLimits, items.EpicPieces);
        var position = new InventorySlot(InventoryListType.Main, 3);
        var request = new DisjointRequest(position, -1, 0);
        foreach (var candidate in pool.Entries)
        {
            var core = DungeonDropCreation.Create(candidate.Template, 1, _ => 0, DateTimeOffset.UtcNow);
            var current = character with { Inventory = new([new(1, position, core)]) };
            That(!candidate.Template.DisjointForbidden && service.Disjoint(current, request, out var receipt).Accepted &&
                receipt is not null, "模拟池每种普通来源史诗均能沿现有分解服务规划产物：" + candidate.Template.Id);
            var restricted = current with { Inventory = new([new(1, position, core with { DisjointForbidden = true })]) };
            That(!service.Disjoint(restricted, request, out _).Accepted,
                "同模板的其它禁分来源仍拒绝：" + candidate.Template.Id);
        }
        Console.WriteLine($"PASS {Count - before} 星河裂缝原资源模拟装备生成及分解资格断言");
    }
}
