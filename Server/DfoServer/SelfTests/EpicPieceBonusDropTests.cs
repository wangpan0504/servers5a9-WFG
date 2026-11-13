using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;
using DfoServer.Infrastructure;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class EpicPieceBonusDropTests
{
    public static void RunCore()
    {
        int before = Count;
        var labels = new Dictionary<int, string> { [1] = "[equip item list]", [2] = "[/equip item list]",
            [3] = "[stackable item list]", [4] = "[/stackable item list]" };
        PvfTokenDocument Document(params PvfToken[] tokens) => new(tokens, id => labels[id]);
        EpicPieceBonusDropData Equipment(params int[] values) => EpicPieceBonusDropFile.Read(Document(
            [new(3, 1), .. values.Select(value => new PvfToken(0, value)), new(3, 2)]));
        var parsed = EpicPieceBonusDropFile.Read(Document(new(3, 1), new(0, 100), new(0, 1), new(3, 2),
            new(3, 3), new(0, 400), new(0, 1), new(3, 4)));
        That(parsed.Equipment[100] == 1 && parsed.Stackable[400] == 1 && Equipment(100, 1, 100, 1).Equipment.Count == 1,
            "资源分别读取装备/持有来源的绝对增量，等价重复声明不重复计数");
        foreach (int[] invalid in new int[][] { [100], [0, 1], [100, -1], [100, 1, 100, 2] })
            Reject<InvalidDataException>(() => Equipment(invalid), "残缺、负增量、无效编号或冲突重复拒绝");
        Reject<InvalidDataException>(() => EpicPieceBonusDropFile.Read(Document(new(3, 1), new(0, 100), new(2, 1), new(3, 2))),
            "增量不能把浮点底层值当整数读取");
        var empty = new EpicPieceBonusDropCatalog(null, _ => null);
        That(!empty.Available && empty.MaximumBonus == 0 && empty.Equipment.Count == 0 && empty.Stackable.Count == 0,
            "旧资源没有可选加成表时只关闭本效果，不阻断原掉落");
        var weapon = Template(100, ItemKind.Equipment, 12);
        var bracelet = Template(300, ItemKind.Equipment, 20);
        var stone = Template(400, ItemKind.Material, 0);
        var templates = new Dictionary<int, ItemTemplate> { [100] = weapon, [200] = weapon with { Id = 200 }, [300] = bracelet, [400] = stone };
        var data = new EpicPieceBonusDropData(new Dictionary<int, int> { [100] = 1, [200] = 3, [300] = 2 }.ToImmutableDictionary(),
            new Dictionary<int, int> { [400] = 1 }.ToImmutableDictionary());
        var catalog = new EpicPieceBonusDropCatalog(data, id => templates.GetValueOrDefault(id));
        That(catalog.MaximumBonus == 6, "预留按同属性槽最大值计算，不把互斥武器累加；不同持有来源保留一次上界");
        var unknown = new EpicPieceBonusDropCatalog(parsed, _ => null);
        That(unknown.Available && unknown.MissingItems.Length == 2 && unknown.MaximumBonus == 0,
            "表中未提供的物品模板保留兼容诊断，不让旧资源的新加成条目阻断启动");
        var equipped = new InventoryItem(1, new(InventoryListType.Equipment, 12), new(100, ItemKind.Equipment, 1));
        var carried = new InventoryItem(2, new(InventoryListType.Main, 121), new(400, ItemKind.Material, 1));
        Character Role(params InventoryItem[] rows) => new(1, 0, "EpicPieceBonus", 0, new(1, 0, 0, 0), null)
            { Level = 95, Inventory = new([.. rows]) };
        int Capture(Character actor, long at = 100, EpicPieceBonusDropCatalog? source = null) =>
            EpicPieceBonusDropPolicy.Capture(actor, source ?? catalog, at, "dungeon");
        var role = Role(equipped, carried);
        That(Capture(role) == 2 && Capture(Role(equipped)) == 1 && Capture(Role(carried)) == 1,
            "本人当前穿戴与材料栏持有分别生效，可以相加而不消费丹石");
        That(Capture(Role(carried, carried with { Uid = 3, Position = new(InventoryListType.Main, 122) })) == 1 &&
            carried.Core.Count == 1 && role.Inventory.Find(carried.Position) == carried,
            "同种丹石多格不重复，资格汇总不修改物品核心、数量或期限");
        foreach (var position in new[] { new InventorySlot(InventoryListType.Main, 9), new(InventoryListType.PersonalCargo, 0),
            new(InventoryListType.AccountCargo, 0), new(InventoryListType.Equipment, 24), new(InventoryListType.Equipment, 33) })
            That(Capture(Role(equipped with { Position = position })).Equals(0), "未穿戴、仓库、副武器及纯外观槽不冒领主属性来源");
        foreach (var position in new[] { new InventorySlot(InventoryListType.Main, 3), new(InventoryListType.Main, 65),
            new(InventoryListType.PersonalCargo, 0), new(InventoryListType.AccountCargo, 0), new(InventoryListType.AccountCommon, 0) })
            That(Capture(Role(carried with { Position = position })) == 0, "丹石仅对应背包分类中的真实持有，不借快捷栏、仓库或公共容器");
        var expired = carried with { Core = carried.Core with { ExpireTime = 100 } };
        That(Capture(Role(equipped, expired), 99) == 2 && Capture(Role(equipped, expired)) == 1 &&
            Capture(Role(equipped with { Core = equipped.Core with { ExpireTime = 100 } }, carried)) == 1,
            "到期等号停止对应来源，不借另一件有效物品延长资格");
        var disabled = new EpicPieceBonusDropCatalog(parsed, id => templates.GetValueOrDefault(id) is { } t
            ? t with { ExperienceForbiddenModules = ImmutableHashSet.Create("dungeon") } : null);
        That(Capture(role, source: disabled) == 0 && Capture(Role() with { Id = 2 }) == 0,
            "当前模块禁用与另一角色的空库存不继承本人来源");
        Generation();
        Console.WriteLine($"PASS {Count - before} epic piece bonus source, capacity and generation assertions");
    }

    static ItemTemplate Template(int id, ItemKind kind, byte slot) =>
        new(id, kind, slot, 1, 0, kind == ItemKind.Material ? 999 : 1, 0, kind == ItemKind.Material ? 4 : 2, 1,
            "[free]", ushort.MaxValue, 0, "EpicPieceFixture") { CreationRate = 1 };

    static void Generation()
    {
        var material = Template(7001, ItemKind.Material, 0);
        var weapon = Template(8001, ItemKind.Equipment, 12);
        var pool = new DungeonItemDropPool(new[] { new DungeonWeightedDropItem(material, 1) });
        var equip = new DungeonDropTrial(0, new DungeonItemDropPool(new[] { new DungeonWeightedDropItem(weapon, 1) }), 1, 1);
        var fragment = new DungeonDropTrial(1000, pool, 1, 1, ProbabilityDenominator: 1000, Attempts: 3)
            { EpicPieceBonusCapacity = 2 };
        var plan = new DungeonDropGenerationPlan("fragment-fixture", [equip, fragment])
            { ExcludeLowMaterials = true, DropMultipliers = new() };
        var now = DateTimeOffset.FromUnixTimeSeconds(100);
        That(plan.MaximumObjects == 6 && plan.Generate(_ => 0, now).Length == 3 &&
            plan.WithEpicPieceBonus(1).Generate(_ => 0, now).Length == 4 && plan.WithEpicPieceBonus(2).Generate(_ => 0, now).Length == 5,
            "预留包括未命中装备上界及两片增量；实际是本次末杀总量加1/2，不把每片翻倍");
        var boosted = plan.WithEpicPieceBonus(2);
        That(plan.Trials[1].Attempts == 3 && boosted.Trials[0] == equip && boosted.ExcludeLowMaterials &&
            boosted.DropMultipliers == plan.DropMultipliers && boosted.MaximumObjects <= plan.MaximumObjects &&
            ReferenceEquals(boosted, boosted.WithEpicPieceBonus(2)),
            "派生不修改共享规则、不改变装备骰，保留过滤和倍率，不能重复叠加同一派生");
        Reject<InvalidDataException>(() => plan.WithEpicPieceBonus(3), "超预留不能截断碎片或借用其它对象键");
        Reject<InvalidDataException>(() => plan.WithEpicPieceBonus(-1), "额外数量不能为负");
        var ordinary = new DungeonDropGenerationPlan("not-a-hell-fragment", [equip]);
        That(ReferenceEquals(ordinary, ordinary.WithEpicPieceBonus(2)), "无原碎片试验的普通掉落不凭空补碎片");
        var rules = DungeonHellDropRules.Parse("""
            [dungeon difficulty drop prob] 1 10 1000 1000 1000 1000 1000
            [basis of rarity dicision] 1 1000000 1000000 1000000 1000000 1000000 1000000 1000000
            [item drop ref table] 1 0 2
            """);
        var pieces = new EpicPieceCatalog("[piece list]\n9001 7001\n[/piece list]");
        var epic = weapon with { Id = 9001, MinimumLevel = 1, Rarity = 4 };
        var fragments = new EpicPieceDropCatalog(pieces, id => id == 9001 ? epic : material);
        var candidates = DungeonItemDropCatalog.Create([]);
        var empty = new DungeonDropGenerationPlan("hell", []);
        var hell = new DungeonHellRewardPolicy(rules, candidates, fragments, 1000, 2);
        var normal = hell.Extend(empty, 1, 1, 1);
        That(normal.Trials.Count(trial => trial.EpicPieceBonusCapacity == 2) == 1 &&
            normal.WithEpicPieceBonus(2).Generate(_ => 0, now).Length == DungeonHellRewardPolicy.EpicFragmentRolls(1, 0) + 2,
            "正式深渊只把碎片试验标记为加成目标，不给额外史诗装备机会");
        foreach (int count in new[] { 11, 13 })
            That(hell.Extend(empty, 1, 1, 1, fragmentRolls: count).WithEpicPieceBonus(2).Generate(_ => 0, now).Length == count + 2,
                "时空裂缝普通/特殊迷宫保留11/13基数，只追加同一增量");
        That(!hell.Extend(empty, 1, 1, 1, suppressEpicFragments: true).AcceptsEpicPieceBonus &&
            !new DungeonHellRewardPolicy(rules, candidates, fragments, 0, 2).Extend(empty, 1, 1, 1).AcceptsEpicPieceBonus,
            "哈林等原抑制场景及0关闭配置不因持有加成重新生成碎片");
    }

    public static void RunResources(ItemResources resources)
    {
        var catalog = resources.EpicPieceBonusDrops;
        That(catalog.Available && catalog.Equipment.Count == 37 && catalog.Stackable.Count == 1 &&
            catalog.MaximumBonus == 2 && catalog.MissingItems.IsEmpty && catalog.Equipment.Values.All(row => row.Count == 1),
            "当前PVF完整绑定37件装备及1个持有源，最大并发增量为2");
        That(catalog.Equipment[404000041].Template.EquipmentSlot == 12 &&
            catalog.Stackable[490008657] is { Count: 1, Template.Kind: ItemKind.Material, Template.Expiration.Days: 30 },
            "地狱拓荒者来自真实主武器槽；丹石为30天材料，不注册为主动消耗品");
    }
}
