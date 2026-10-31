using DfoServer.GameWorld.Items;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Network.Parsers.Inventory;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class EnchantmentTests
{
    // 合成资源只核已取证的有序语法；真实六珠与事务回归沿用真实资源夹具。
    static void OrderedRaritySubtypes()
    {
        const int beadId = 991701, cardId = 991702;
        const string bead = "[stackable type]\n`[enchant waste]` 0\n[monster card id]\n991702\n";
        const string card = """
            [stackable type]
            `[material expert job]` 1
            [string data]
            `synthetic-card.img` `[shoulder]`
            [/string data]
            [enchant table]
            [enchant index] 0
            [physical attack] 1
            [/enchant index]
            [/enchant table]
            """;
        EnchantmentCatalog Catalog(string labels) => new(path => path == "stackable/bead.stk" ? bead :
            card + "\n[unable use rarity]\n" + labels + "\n[/unable use rarity]\n",
            new Dictionary<int, string> { [beadId] = "bead.stk", [cardId] = "card.stk" });

        // 首项直接覆盖五张实际卡的语法；修复前应在此因整张定义被拒而失败。
        foreach (var (labels, excluded) in new (string Labels, int[] Excluded)[]
        {
            ("`common` `uncommon` `rare` `normal`", [0, 1, 2]),
            ("`rare` `legacy`", [99]),
            ("`rare` `normal` `legacy`", [2, 99]),
            ("`rare` `legacy` `normal`", [99, 2]),
            ("`rare` `normal` `normal`", [2]),
            ("`common` `uncommon`", [0, 1]),
            ("`rare` `unique` `uncommon`", [3, 1]),
            ("`common` `uncommon` `rare`", [0, 1])
        })
        {
            var catalog = Catalog(labels);
            var definition = catalog.Get(beadId);
            That(definition is not null && definition.CardItemId == cardId &&
                definition.Upgrade == 0 && definition.Targets.SetEquals([(ItemKind.Equipment, (byte)15)]) &&
                definition.ExcludedTargetRarities.SetEquals(excluded),
                "unable use rarity按有序rare子类型保留禁集：" + labels);
        }

        foreach (string labels in new[]
        {
            "`normal`", "`legacy`", "`common` `normal`", "`unique` `legacy`",
            "`rare` `unknown`", "`rare` `normal` `unknown`", "`rare` `unique` `normal`",
            "`rare` `normal` 42", "`rare` `normal` `NORMAL`"
        })
        {
            var catalog = Catalog(labels);
            That(catalog.Get(beadId) is null &&
                !catalog.CanEnchantCard(cardId, 0, ItemKind.Equipment, 15),
                "未知标签或脱离rare上下文的子类型不得被丢弃后放行：" + labels);
        }

        var sourceTemplate = new ItemTemplate(beadId, ItemKind.Consumable, 0, 1, 0, 1000, 1, 3, 1,
            "[free]", ushort.MaxValue, 0, "SyntheticBead");
        var gear = new ItemTemplate(991703, ItemKind.Equipment, 15, 1, 45, 1, 20, 2, 1,
            "[free]", ushort.MaxValue, 0, "SyntheticShoulder");
        var source = new InventoryItem(81, new(InventoryListType.Main, 65), new(beadId, ItemKind.Consumable, 2));
        var target = new InventoryItem(82, new(InventoryListType.Main, 9), new(gear.Id, ItemKind.Equipment, 1234567)
            { Attr = 7, EnchantCardId = 3175, EnchantUpgrade = 2, SortLockFlag = 1 });
        Character role = new(1, 0, "OrderedRarity", 0, new(0, 0, 0, 0), null)
            { Level = 95, Inventory = new([source, target], Gold: 3456) };
        var request = new EnchantmentRequest(source.Position, target.Position, uint.MaxValue);
        foreach (var (labels, normalAllowed, legacyAllowed) in new[]
        {
            ("`common` `uncommon` `rare` `normal`", false, true),
            ("`rare` `legacy`", true, false),
            ("`rare` `normal` `legacy`", false, false)
        })
        {
            var catalog = Catalog(labels);
            var definition = catalog.Get(beadId)!;
            foreach (var (rarity, legacy, allowed) in new[]
            {
                (2, false, normalAllowed), (2, true, legacyAllowed), (3, false, true), (3, true, true)
            })
            {
                var actual = gear with { Rarity = rarity, IsLegacyEquipment = legacy };
                That(definition.AllowsTarget(actual) == allowed && catalog.CanEnchantCard(cardId, 0, actual) == allowed &&
                    catalog.CanEnchantCard(cardId, 0, actual.Kind, actual.EquipmentSlot),
                    "仅稀有传承映射99，神器legacy仍保留原稀有度；四参查询继续只校部位/等级");
                var result = InventoryEnchantmentService.Plan(role, request, 2_000_000_000, definition, sourceTemplate, actual);
                if (allowed)
                {
                    var after = result.Plan?.After;
                    That(result.Accepted && after is not null && after.Find(source.Position) == source with
                        { Core = source.Core with { Value = 1 } } &&
                        after.Find(target.Position) == target with
                        { Core = target.Core with { EnchantCardId = cardId, EnchantUpgrade = 0 } },
                        "正常与传承按同份已证资格规划，只扣一珠并改Card/index");
                }
                else
                    That(!result.Accepted && result.Plan is null, "普通稀有或禁用传承拒绝，不生成扣源或改目标计划");
                That(role.Inventory.Find(source.Position) == source && role.Inventory.Find(target.Position) == target &&
                    role.Inventory.Gold == 3456, "有序资格校验不修改原库存与金币");
            }
        }
    }
}
