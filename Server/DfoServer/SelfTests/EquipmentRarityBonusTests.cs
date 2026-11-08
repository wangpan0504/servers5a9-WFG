using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class EquipmentRarityBonusTests
{
    public static void RunCore()
    {
        int before = Count;
        Capture();
        Distribution();
        MixedPools();
        Console.WriteLine($"PASS {Count - before} equipment rarity bonus assertions");
    }

    static void Capture()
    {
        var templates = EquipmentRarityBonusDefinition.Items.ToDictionary(row => row.Key, row =>
            new ItemTemplate(row.Key, ItemKind.Equipment, row.Value.Slot, 1, 100, 1, 0, 2, 1, "[free]", ushort.MaxValue, 0, "Rarity")
            { QuickSlotEquipment = row.Value.Slot == 31 ? new(false) : null });
        var catalog = new ItemCatalog(null!, new Dictionary<int, string>(), new Dictionary<int, string>(), 0);
        void Reload() => catalog.PrefillTemplates(templates);
        Reload();
        InventoryItem Worn(int id) => new(id, templates[id].IsQuickSlotEquipment ? new(InventoryListType.Main, 3) :
            new(InventoryListType.Equipment, templates[id].EquipmentSlot), InventoryRewardGrantService.Create(templates[id]));
        Character Role(params InventoryItem[] rows) => new(1, 0, "Rarity", 0, new(1, 0, 0, 0), null)
            { Level = 50, Inventory = new([.. rows]) };
        EquipmentRarityBonus Bonus(Character role, long at = 100) => EquipmentRarityBonusPolicy.Capture(role, catalog, at, "dungeon");
        That(templates.Count == 60 && EquipmentRarityBonusDefinition.Get(100331150) is null &&
            EquipmentRarityBonusDefinition.Get(100300602) is null && EquipmentRarityBonusDefinition.SetBonus(12531).IsEmpty,
            "60件真实登记装备，不按文件名登记错误ID，不启用预留套装");
        foreach (var (id, definition) in EquipmentRarityBonusDefinition.Items)
        {
            var worn = Worn(id);
            That(Bonus(Role(worn)) == definition.Bonus, "每件明确编号使用本人基础加成：" + id);
            That(Bonus(Role(worn with { Position = new(InventoryListType.Main, 9) })).IsEmpty &&
                Bonus(Role(worn with { Position = new(InventoryListType.PersonalCargo, 0) })).IsEmpty &&
                Bonus(Role(worn with { Position = new(InventoryListType.Equipment, 33) })).IsEmpty,
                "未穿戴、仓库及外观副槽不生效");
        }
        int[] family = [20171, 22163, 24192];
        foreach (int id in family) templates[id] = templates[id] with { PartSetIds = [11151, 11151] };
        Reload();
        var complete = Role(family.Select(Worn).ToArray());
        That(Bonus(complete) == new EquipmentRarityBonus(23, 11) &&
            Bonus(Role(Worn(20171), Worn(22163))) == new EquipmentRarityBonus(12, 6),
            "三件套只计算一次，共23%稀有与11%神器；两件没有三件套效果，重复set声明不重复计数");
        foreach (int id in family) templates[id] = templates[id] with { PartSetIds = [11094] };
        Reload();
        That(Bonus(complete) == new EquipmentRarityBonus(18, 15), "守护者套装使用独立的神器6%加成，不套通用5%/2%");
        var title = Worn(400330027);
        var expired = title with { Core = title.Core with { ExpireTime = 100 } };
        That(Bonus(Role(expired), 99).UniquePercent == 200 && Bonus(Role(expired)).IsEmpty,
            "装备有限期限在等号截止，不以入图时的旧资格维持整局");
        templates[400330027] = templates[400330027] with { UsableExpired = true };
        Reload();
        That(Bonus(Role(expired)).UniquePercent == 200, "保留允许过期使用的装备契约");
        templates[400330027] = templates[400330027] with { ExperienceForbiddenModules = ImmutableHashSet.Create("dungeon") };
        Reload();
        That(Bonus(Role(title)).IsEmpty, "impossible equip module使用当前业务模块，不把禁用装备加成带入副本");
        var charm = Worn(400360003);
        var depleted = charm with { Core = charm.Core with { Durability = 0 } };
        var second = Worn(400360004) with { Position = new(InventoryListType.Main, 8) };
        That(Bonus(Role(charm)).UniquePercent == 300 && Bonus(Role(charm, second)).UniquePercent == 300 &&
            Bonus(Role(depleted, second)).IsEmpty && Bonus(Role(charm) with { Level = 4 }).IsEmpty,
            "有效首枚快捷栏符咒生效，重复不叠加、不绕过首枚空能量，低于5级不生效");
        templates[400360003] = templates[400360003] with { Durability = 0 };
        Reload();
        That(Bonus(Role(depleted)).UniquePercent == 300 &&
            Bonus(Role(depleted with { Core = depleted.Core with { ExpireTime = 100 } })).IsEmpty,
            "永久无能量上限型不按当前耐久拒绝；期限仍独立检查");
        That(Bonus(Role() with { Id = 2 }).IsEmpty && Bonus(Role(Worn(100320775))) == new EquipmentRarityBonus(5, 0),
            "独立掉落只读本人装备，真心愿固定5%不依赖队友");
    }

    static void Distribution()
    {
        ImmutableArray<int> original = [500000, 800000, 950000, 990000, 995000, 999000, 1000000];
        var adjusted = EquipmentRarityDistribution.Adjust(original, new(50, 30));
        That(adjusted.SequenceEqual([445625, 713000, 938000, 990000, 995000, 999000, 1000000]) &&
            original[1] == 800000 && EquipmentRarityDistribution.Adjust(original, default) == original,
            "稀有及神器按各自原宽度一次相加，优先扣低档，不递乘、不修改原规则");
        That(EquipmentRarityDistribution.Adjust(original, new(50, 30), false, true)[1] == 788000,
            "目标稀有池缺席时不制造或转给其它档位的增量");
        var sentinel = EquipmentRarityDistribution.Adjust([545500, 899500, 999500, 1000000, 1000001, 1000002, 1000003], new(50, 30));
        That(sentinel[1] == 849350 && sentinel[2] == 999350 && sentinel.Skip(3).All(n => n == 1000000),
            "当前PVF尾部哨兵保留不可达语义，稀有100000增为150000、神器500增为650，不制造史诗");
        var capped = EquipmentRarityDistribution.Adjust(original, new(int.MaxValue, int.MaxValue));
        That(capped[0] == 0 && capped[1] == 0 && capped.Skip(3).SequenceEqual(original.Skip(3)) &&
            capped.SequenceEqual(capped.Order()), "超大增量只用可转移空间，封顶后高档绝对概率与总和仍保持");
        long[] tiny = [10, 1, 1, 0];
        EquipmentRarityDistribution.Expand(tiny, [0, 2, 3, -2], new(50, 50));
        That(tiny.SequenceEqual([10L, 1, 1, 0]), "稀有/神器各自截尾，不把两个半单位合成一个额外中奖");
        Reject<InvalidDataException>(() => EquipmentRarityDistribution.Adjust(original, new(-1, 0)), "负加成不作合法概率使用");
    }

    static void MixedPools()
    {
        ItemTemplate Template(int rarity) => new(9000 + rarity, ItemKind.Equipment, 12, 1, 10, 1, 0, rarity, 1, "[free]", ushort.MaxValue, 0, "Pool");
        var equipment = Enumerable.Range(0, 7).Select(Template).ToArray();
        var material = new ItemTemplate(9999, ItemKind.Material, 0, 1, 0, 100, 0, 3, 1, "[free]", ushort.MaxValue, 0, "Material");
        var pool = new DungeonItemDropPool(equipment.Append(material).Select((template, i) =>
            new DungeonWeightedDropItem(template, new[] { 10, 10, 20, 10, 5, 4, 1, 40 }[i])));
        var trial = new DungeonDropTrial(2500, pool, 1, 1) { RarityBonusGroup = DungeonRarityBonusGroup.MonsterSpecific };
        var widths = new DungeonEquipmentRarityAdjustment(new(50, 30)).Widths(trial);
        That(widths.SequenceEqual([8750L, 8750, 75000, 32500, 12500, 10000, 2500, 100000, 750000]),
            "混池装备只占用低档区间，材料、史诗/传说/异界及空抽概率保持；候选原权重保留");
        var onlyRare = new DungeonDropTrial(100, new(new DungeonWeightedDropItem[] { new(equipment[2], 5), new(material, 5) }), 1, 1)
            { RarityBonusGroup = DungeonRarityBonusGroup.WorldLottery };
        That(new DungeonEquipmentRarityAdjustment(new(50, 30)).Widths(onlyRare).SequenceEqual([750L, 500, 98750]),
            "没有低档装备时只使用原空抽，不挤占混合材料的绝对概率");
        var epic = trial with { EpicAdjustment = new(5000) };
        var combined = new DungeonEquipmentRarityAdjustment(new(50, 30)).Widths(epic);
        That(combined[4] == epic.EpicAdjustment.Widths(epic).ElementAt(4) && combined[7] == widths[7],
            "GM定向史诗混池先保留既有史诗区间，个人稀有度加成不覆盖史诗倍率或材料");
        foreach (var group in new[] { DungeonRarityBonusGroup.MonsterSpecific, DungeonRarityBonusGroup.IndependentLottery,
            DungeonRarityBonusGroup.WorldLottery, DungeonRarityBonusGroup.EquipmentLottery })
        {
            var plan = new DungeonDropGenerationPlan("fixture", [trial with { RarityBonusGroup = group }]);
            var boosted = plan.WithEquipmentRarityBonus(new(50, 30));
            That(boosted.Trials[0].EquipmentRarityAdjustment is not null && plan.Trials[0].EquipmentRarityAdjustment is null &&
                boosted.MaximumObjects == plan.MaximumObjects && boosted.Trials[0] with { EquipmentRarityAdjustment = null } == plan.Trials[0],
                "每个登记的随机策略派生独立调整，原池、数量与试验次数不变：" + group);
            int calls = 0;
            var result = boosted.Generate(bound => ++calls == 1 ? 500 : 0, DateTimeOffset.FromUnixTimeSeconds(100));
            That(result is [{ Item.ItemId: 9002 }], "调整后的稀有区间可实际生成装备，不额外追加掉落");
        }
        var fixedPlan = new DungeonDropGenerationPlan("fixed", [trial with { RarityBonusGroup = DungeonRarityBonusGroup.None }]);
        That(ReferenceEquals(fixedPlan, fixedPlan.WithEquipmentRarityBonus(new(50, 30))), "固定指定物品默认不接装备率");
        var rarityPools = equipment.Select((template, i) => new DungeonDropRarityPool(
            new[] { 500000, 800000, 950000, 990000, 995000, 999000, 1000000 }[i],
            new(new DungeonWeightedDropItem[] { new(template, 1) }))).ToImmutableArray();
        foreach (var group in new[] { DungeonRarityBonusGroup.OrdinaryEquipment, DungeonRarityBonusGroup.HellEquipment })
        {
            var basis = new DungeonDropTrial(300, null, 1, 1, RarityPools: rarityPools, Attempts: 8) { RarityBonusGroup = group };
            var plan = new DungeonDropGenerationPlan("rarity", [basis]);
            var boosted = plan.WithEquipmentRarityBonus(new(50, 30));
            That(boosted.Trials[0].RarityPools.Select(row => row.Threshold).SequenceEqual(
                    EquipmentRarityDistribution.Adjust(rarityPools.Select(row => row.Threshold).ToImmutableArray(), new(50, 30))) &&
                boosted.Trials[0] with { RarityPools = rarityPools } == basis && boosted.MaximumObjects == 8,
                "普通/深渊保留外层触发率、次数和各档候选，只有七档稀有度分布改变");
        }
    }

    public static void RunResources(ItemCatalog items)
    {
        foreach (var (id, definition) in EquipmentRarityBonusDefinition.Items)
        {
            var template = items.Get(id);
            That(template.Kind == ItemKind.Equipment && template.EquipmentSlot == definition.Slot &&
                !template.Name.Contains("不使用", StringComparison.Ordinal), "真实PVF加成编号、类型与槽位符合登记：" + id);
        }
    }
}
