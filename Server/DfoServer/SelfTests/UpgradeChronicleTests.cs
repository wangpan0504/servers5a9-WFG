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
        That(catalog.Get(490013661) is null && catalog.Get(10012055) is { TicketType: 2 }, "活动action136独立，成长ticket按自己的成长规则装载");
        foreach (var row in new (int Book, int Source, int Output)[] { (1229, 400160, 400888), (1230, 400160, 400888),
            (10089568, 20168, 100310932), (10149520, 116010034, 116010065), (10149519, 10105, 100050639),
            (10150749, 116010105, 116010107), (10305525, 100140430, 100140440) })
        {
            var book = catalog.Get(row.Book);
            That(book is not null && catalog.Find(book, row.Source)?.OutputItemId == row.Output,
                "旧勇者、古代及圣物转换按自身EQU映射，不凭名称推断目标");
        }
        var growthBook = catalog.Get(10012056)!;
        That(catalog.Find(growthBook, 108030499) is null &&
            catalog.Find(growthBook, 108030500) is { RequiredGrowthExperience: 4830, Materials: [{ ItemId: 10000224, Count: 2 }] },
            "高级龙跃仅接受三至六阶、满成长经验，并按卷轴声明增加百分之百材料");
        var levelBook = catalog.Get(10094062)!;
        That(items.EquipmentGrowth.Get(108030498) is { Stage: 1, Limit: 1794, Automatic: true, Output: 108030499 } &&
            items.EquipmentGrowth.Get(108030500) is { Stage: 3, Limit: 4830, Automatic: false },
            "龙跃早期自动进化和卷轴进化读取同一份正式资源");
        That(levelBook is { LevelTicket: { Increase: 3, Maximum: 90 } } &&
            catalog.Get(490000095) is { LevelTicket.Maximum: 86 }, "活动次元能量上限86不得套普通90");
        That(catalog.LevelCost(5, 12, 70, 0, 0, 0) == new ItemMaterialCost(3311, 7) &&
            catalog.LevelCost(5, 14, 70, 0, 0, 0) == new ItemMaterialCost(3311, 6) &&
            catalog.LevelCost(5, 14, 70, 10, 0, 4) == new ItemMaterialCost(3311, 15),
            "次元能量费用按线性插值和锻造加项截断，不将全部权重直接相乘");
        That(catalog.LevelCost(5,14,78,17,0,4) == new ItemMaterialCost(3311,189) &&
            catalog.LevelCost(5,14,70,0,1,7) == new ItemMaterialCost(3311,18),
            "最终整数边界不被double误差少收，float中间恰半舍入不多收");
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
        var levelTarget = target with { Core = target.Core with { EmancipateLevel = 0 } };
        var template = items.Get(409040007);
        That(template is { Rarity: 5, MinimumLevel: 70, EquipmentSlot: 12 } && items.Get(10094062).AllowsUse(95, 0),
            "正式七十级勇者模板存在，目标上限不得误作角色使用上限");
        levelTarget = levelTarget with { Core = levelTarget.Core with { ItemId = template.Id } };
        var levelActor = actor with { Inventory = new([new(11, bookSlot, new(10094062, ItemKind.Consumable, 1)), levelTarget,
            new(13, materialSlot, new(3311, ItemKind.Material, 10000))]) };
        var increased = service.Upgrade(levelActor, bookSlot, 10094062, targetSlot, template.Id);
        That(increased.Accepted && increased.Plan!.After.Find(targetSlot)!.Core == levelTarget.Core with { EmancipateLevel = 3 },
            $"次元能量保持模板和完整打造，仅推进实际等级：template={template.Id}, accepted={increased.Accepted}, core={increased.Plan?.After.Find(targetSlot)?.Core}");
        var negative = levelTarget with { Core = levelTarget.Core with { EmancipateLevel = 255 } };
        // 在本测试私有目录中合成较高基础等级，独立覆盖有符号偏移，不扫描无关装备。
        var higher = template with { MinimumLevel = 75 };
        items.PrefillTemplates([new(template.Id, higher)]);
        negative = negative with { Core = negative.Core with { ItemId = higher.Id } };
        var signed = service.Upgrade(levelActor with { Inventory = levelActor.Inventory with
            { Items = levelActor.Inventory.Items.Replace(levelTarget, negative) } }, bookSlot, 10094062, targetSlot, higher.Id);
        That(signed.Accepted && signed.Plan!.After.Find(targetSlot)!.Core.EmancipateLevel == 2,
            "已有负一级偏移按有符号相加，不发生byte溢出");
        var wearer = actor with { Level = 72 };
        That(!InventoryMoveService.WearLevelSatisfied(wearer, template, 2000000000, core: levelTarget.Core with { EmancipateLevel = 3 }) &&
            InventoryMoveService.WearLevelSatisfied(wearer with { Level = 73 }, template, 2000000000, core: levelTarget.Core with { EmancipateLevel = 3 }),
            "穿戴等级包含实例增加值并保留恰好达标边界");
        Console.WriteLine($"PASS {Count - checks} 设计图升级配方断言");
    }
}
