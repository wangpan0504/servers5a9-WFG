using DfoServer.GameWorld.Items;
using PvfLib;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

// 设计图升级规则全部来自真实 PVF：断言解析出的组数、可变装备槽、固定材料与产物，
// 以及「同一组的多件传说共用 0 号位」这一关键结构。
internal static class UpgradeChronicleTests
{
    public static void Run(PvfArchive pvf)
    {
        int checks = Count;
        var stackable = PvfScript.List(pvf.ReadText("stackable/stackable.lst"));
        var equipment = PvfScript.List(pvf.ReadText("equipment/equipment.lst"));
        var catalog = new UpgradeChronicleCatalog(pvf, stackable, equipment);
        var definition = catalog.Get(10308046);
        That(definition is not null && definition.Recipes.Length == 5, "兵法之神设计图解析出5组");
        if (definition is null)
        {
            Console.WriteLine($"PASS {Count - checks} 设计图升级配方断言");
            return;
        }
        That(definition.Recipes.Length == 5, "5组配方");
        var first = definition.Recipes[0];
        That(first.Group == 0 && first.OutputItemId == 116602299 && first.Accepts.Contains(100090439), "0组产物与可变装备");
        That(first.Materials.Length == 3 && first.Materials.Contains(new ItemMaterialCost(3262, 100)) &&
            first.Materials.Contains(new ItemMaterialCost(3137, 20)) && first.Materials.Contains(new ItemMaterialCost(3155, 10)),
            "0组固定材料数量正确");
        // 设计图的 [need material] 是 NPC 卖价（异形结晶），不能混进升级材料。
        That(first.Materials.All(cost => cost.ItemId != 10305892), "NPC卖价不计入升级材料");
        That(definition.Recipes[4].OutputItemId == 116602303 &&
            definition.Recipes.Select(recipe => recipe.OutputItemId).Distinct().Count() == 5, "5组产物互不相同");
        var shared = definition.Find(100050801);
        That(shared is not null && shared.OutputItemId == 116602299, "同一组的多件传说共用0号位");
        That(definition.Find(101040485) is null, "非本设计图装备不匹配");
        var sibling = catalog.Get(10305608);
        That(sibling is not null && sibling.Recipes.Length == 5 &&
            sibling.Recipes[0].Materials.Contains(new ItemMaterialCost(3160, 20)), "兄弟设计图材料不同且可解析");
        That(catalog.Get(10308047) is null && catalog.Get(0) is null, "非设计图不产出配方");
        var ticket = catalog.Get(10308623);
        That(ticket is { TicketType: 23, Recipes.IsEmpty: true }, "普通胜武器书读取ticket而非伪造group");
        var conversion = catalog.Find(ticket!, 101040543);
        That(conversion is { OutputItemId: 101040551, OutputCount: 1 } &&
            conversion.Materials.SequenceEqual([new ItemMaterialCost(10308622, 2)]), "截图光剑使用碎片2，不是NPC售价8");
        var items = new ItemCatalog(pvf, equipment, stackable, 0);
        var eventBook = items.EquipmentPeriodChange(490013661)!;
        That(eventBook.Upgrades.Count == 62, "同类清单覆盖62种原武器");
        foreach (var pair in eventBook.Upgrades)
        {
            var mapped = catalog.Find(ticket!, pair.Key);
            That(mapped?.OutputItemId == pair.Value && mapped.Materials.SequenceEqual([new ItemMaterialCost(10308622, 2)]),
                "全部职业武器均按自身EQU映射转换，不硬码截图ID");
            That(catalog.Find(ticket!, pair.Value) is null, "胜武器不可再次转换");
        }
        That(catalog.Get(490013661) is null && catalog.Get(10012055) is null, "活动action136与其他成长ticket不套type23策略");
        var bookSlot = new InventorySlot(InventoryListType.Main, 60);
        var targetSlot = new InventorySlot(InventoryListType.Main, 9);
        var materialSlot = new InventorySlot(InventoryListType.Main, 100);
        var target = new InventoryItem(12, targetSlot, new(101040543, ItemKind.Equipment, 987654321)
        { Attr = 12, GenuineUpgrade = 5, EnchantCardId = 100000201, AmplifyType = 2, AmplifyValue = 43,
            Random0 = new(1, 2, 3), Emblem1 = 42, EmblemSocketCount = 1, TradeRestriction = 1 });
        var actor = new Character(1, 0, "Victory", 0, new(1, 0, 0, 0), null) { Level = 95,
            Inventory = new([new(11, bookSlot, new(10308623, ItemKind.Consumable, 1)), target,
                new(13, materialSlot, new(10308622, ItemKind.Material, 2))]) };
        var service = new InventoryUpgradeChronicleService(catalog, items, new EpicPieceCatalog("[piece list]\n60000 70001\n[/piece list]"));
        var result = service.Upgrade(actor, bookSlot, 10308623, targetSlot, 101040543);
        That(result.Accepted && result.Plan!.After.Items is [{ Uid: 12 }] &&
            result.Plan.After.Find(targetSlot)!.Core == target.Core with { ItemId = 101040551 },
            "真实书成功仅扣书和两碎片，完整保留UID/品级seed/强化/锻造/附魔/异次元/限制");
        That(result.Plan!.CharacterPrecondition is not null && actor.Inventory.Items.Length == 3,
            "规划携带角色资格且原快照不变");
        var repeated = actor with { Inventory = result.Plan.After };
        That(!service.Upgrade(repeated, bookSlot, 10308623, targetSlot, 101040543).Accepted, "相同旧请求不能再次消费");
        Console.WriteLine($"PASS {Count - checks} 设计图升级配方断言");
    }
}
