using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

// 誓约之缚武器制作图：成对声明装配、不扣[need material]、只换物品ID并保留其余核心字段。
internal static class EquipmentPeriodChangeTests
{
    const int Design = 9001;
    const int ShopMaterial = 9002;
    const int SourceWeapon = 9100;
    const int ProductWeapon = 9101;

    static readonly ItemTemplate DesignTemplate = new(Design, ItemKind.Consumable, 0, 95, 0, 1, 0, 0, 10, "[trade]",
        ushort.MaxValue, 0, "action");

    static RecoveryConsumableFile File(uint[] parameters, params string[] places) => new(Design, "action.stk",
        new(Design, "action.stk", new("[etc]", [0]), 100, null, null, 95, null, [])
        {
            ActionDeclaration = new("[equipment item period change]", parameters.Select(value => (int)value).ToArray()),
            HasUseActionPacket = true, DeclaredUseActionPacket = 0,
            HasActionUsablePlace = places.Length > 0, DeclaredActionUsablePlaces = places.Length > 0 ? places : null,
            // 商店卖价：使用时不扣。
            DeclaredNeededMaterials = [ShopMaterial, 600]
        }, 0, null, null, null, [], []);

    public static void RunCore()
    {
        var definition = EquipmentPeriodChangeDefinition.Parse(File([SourceWeapon, ProductWeapon]), DesignTemplate)!;
        That(definition.Upgrades.Count == 1 && definition.Upgrades[SourceWeapon] == ProductWeapon,
            "★ 成对声明装配为前项武器到后项哈林史诗的映射");
        That(definition.AllowsPlace("[village]") && definition.AllowsPlace("[seria room]") && !definition.AllowsPlace("[dungeon]"),
            "制作图只在村庄与赛丽亚房间可用，地下城内拒绝");
        foreach (var (invalid, reason) in new (RecoveryConsumableFile, string)[]
        {
            (File([SourceWeapon]), "奇数长度"),
            (File([SourceWeapon, ProductWeapon, SourceWeapon, ProductWeapon]), "重复源武器"),
            (File([SourceWeapon, 0]), "非正输出武器"),
            (File([SourceWeapon, ProductWeapon], "[dungeon]"), "声明了不支持的地点")
        })
            That(EquipmentPeriodChangeDefinition.Parse(invalid, DesignTemplate) is null,
                "★ " + reason + "的声明整份拒绝，不做猜测性对齐");

        var catalog = new ItemCatalog(null!, new Dictionary<int, string>(), new Dictionary<int, string>(), 0);
        catalog.PrefillTemplates(new Dictionary<int, ItemTemplate>
        {
            [Design] = DesignTemplate,
            [SourceWeapon] = new(SourceWeapon, ItemKind.Equipment, 12, 95, 20, 1, 0, 2, 60, "[trade]", ushort.MaxValue, 0, "weapon"),
            [ProductWeapon] = new(ProductWeapon, ItemKind.Equipment, 12, 95, 20, 1, 0, 2, 60, "[trade]", ushort.MaxValue, 0, "weapon")
        });
        catalog.PrefillPeriodChanges(new Dictionary<int, EquipmentPeriodChangeDefinition>
        {
            [Design] = EquipmentPeriodChangeDefinition.Parse(File([SourceWeapon, ProductWeapon], "[village]", "[seria room]"),
                DesignTemplate)!
        });
        var clock = new ConsumableUseSessionTests.FakeClock();
        var designSlot = new InventorySlot(InventoryListType.Main, 81);
        var weaponSlot = new InventorySlot(InventoryListType.Main, 10);
        var shopSlot = new InventorySlot(InventoryListType.Main, 12);
        var design = new InventoryItem(1, designSlot, new(Design, ItemKind.Consumable, 3));
        var shopMaterial = new InventoryItem(2, shopSlot, new(ShopMaterial, ItemKind.Material, 1000));
        // 强化11、增幅、附魔与随机属性都要随实例保留，只有物品ID变化。
        var weapon = new InventoryItem(3, weaponSlot, new(SourceWeapon, ItemKind.Equipment, 123456)
        {
            Attr = 11, EnchantCardId = 1234, AmplifyType = 1, AmplifyValue = 55, Random0 = new(1, 2, 3), Extension27 = 888
        });
        var character = new Character(1, 0, "Period", 1, new(1, 0, 1, 1), null)
        { Level = 95, Job = 1, Inventory = new([design, shopMaterial, weapon]) };
        var service = new InventoryEquipmentPeriodChangeService(catalog, time: clock);
        var plan = service.Plan(character, designSlot, weaponSlot, inTown: true);
        That(plan.Accepted, "★ 合法制作图与誓约之缚武器在村庄内可制作");
        var updated = plan.Plan!.After.Find(weaponSlot)!;
        That(updated.Core.ItemId == ProductWeapon && updated.Uid == weapon.Uid &&
            updated.Core.Attr == 11 && updated.Core.EnchantCardId == 1234 && updated.Core.AmplifyValue == 55 &&
            updated.Core.Random0 == new RandomOption(1, 2, 3) && updated.Core.Extension27 == 888,
            "★ 只换物品ID；强化、附魔、增幅、随机属性与UID全部保留");
        That(plan.Plan.After.Find(designSlot)!.Core.Count == 2,
            "制作图本身按源消费一件");
        That(plan.Plan.After.Find(shopSlot)!.Core.Count == 1000,
            "★ [need material]是商店卖价，使用时一张都不扣");
        That(character.Inventory.Find(weaponSlot)!.Core.ItemId == SourceWeapon &&
            character.Inventory.Find(designSlot)!.Core.Count == 3,
            "规划不修改原快照");

        That(!service.Plan(character with { Inventory = new([design, shopMaterial,
                weapon with { Core = weapon.Core with { ItemId = ProductWeapon } }]) },
                designSlot, weaponSlot, inTown: true).Accepted, "已转换过的目标不再二次制作");
        That(!service.Plan(character, designSlot, weaponSlot, inTown: false).Accepted,
            "地下城内不能使用该制作图");
        That(!service.Plan(character with { Inventory = new([design, shopMaterial,
                weapon with { Core = weapon.Core with { EquipmentLockId = 1 } }]) },
                designSlot, weaponSlot, inTown: true).Accepted, "安全锁目标不参与制作");
        // 即使完全没有商店材料，制作也必须成功：材料不是使用成本。
        var noMaterial = character with { Inventory = new([design, weapon]) };
        That(service.Plan(noMaterial, designSlot, weaponSlot, inTown: true).Accepted,
            "★ 没有任何商店材料时仍可制作，证明不把[need material]当作使用成本");
        Console.WriteLine("PASS 誓约之缚武器制作图成对装配、不扣商店材料与核心保留断言");
    }
}
