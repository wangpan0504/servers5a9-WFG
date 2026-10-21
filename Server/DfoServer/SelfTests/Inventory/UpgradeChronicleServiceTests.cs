using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

// 设计图升级的行为断言：只扣配方材料、产物沿用原实例、规划不改原快照，
// 以及材料不足、目标不在 0 号位、目标被锁、槽位与 id 不符时整单拒绝。
internal static class UpgradeChronicleServiceTests
{
    const int Blueprint = 64001, Target = 64002, Output = 64003, Cube = 64004, Cloth = 64005, Other = 64006;
    static readonly InventorySlot BlueprintSlot = new(InventoryListType.Main, 5);
    static readonly InventorySlot TargetSlot = new(InventoryListType.Main, 6);
    static readonly InventorySlot CubeSlot = new(InventoryListType.Main, 7);
    static readonly InventorySlot ClothSlot = new(InventoryListType.Main, 8);

    public static void RunCore()
    {
        int checks = Count;
        var catalog = new ItemCatalog(null!, new Dictionary<int, string>(), new Dictionary<int, string>(), 0);
        catalog.PrefillTemplates(new Dictionary<int, ItemTemplate>
        {
            [Blueprint] = new(Blueprint, ItemKind.Consumable, 0, 95, 0, 0, 10, 4, 95, "[trade]", 255, 0, "设计图"),
            [Target] = new(Target, ItemKind.Equipment, 14, 95, 0, 0, 10, 6, 95, "[trade]", 255, 0, "传说上衣"),
            [Output] = new(Output, ItemKind.Equipment, 14, 95, 0, 0, 10, 4, 95, "[trade]", 255, 0, "史诗上衣"),
            [Cube] = new(Cube, ItemKind.Material, 0, 1, 0, 999, 0, 3, 1, "[trade]", 255, 0, "晶块"),
            [Cloth] = new(Cloth, ItemKind.Material, 0, 1, 0, 999, 0, 3, 1, "[trade]", 255, 0, "布片"),
            [Other] = new(Other, ItemKind.Equipment, 14, 95, 0, 0, 10, 6, 95, "[trade]", 255, 0, "别的传说")
        });
        var recipes = new UpgradeChronicleCatalog([new UpgradeChronicleDefinition(Blueprint,
            [new UpgradeChronicleRecipe(0, [Target], [new ItemMaterialCost(Cube, 100), new ItemMaterialCost(Cloth, 20)], Output, 1)])]);
        var service = new InventoryUpgradeChronicleService(recipes, catalog, new EpicPieceCatalog("[piece list]\n60000 70001\n[/piece list]"));

        Character Fixture(int cubes = 250, int cloth = 60, byte targetLock = 0, int targetItemId = Target)
        {
            var blueprint = new InventoryItem(11, BlueprintSlot, new(Blueprint, ItemKind.Consumable, 1));
            var target = new InventoryItem(12, TargetSlot, new(targetItemId, ItemKind.Equipment, 1)
            {
                Attr = 7, EnchantCardId = 900, EmblemSocketCount = 1, Emblem1 = 42, Random0 = new(1, 2, 3), EquipmentLockId = targetLock
            });
            var cube = new InventoryItem(13, CubeSlot, new(Cube, ItemKind.Material, cubes));
            var clothItem = new InventoryItem(14, ClothSlot, new(Cloth, ItemKind.Material, cloth));
            return new Character(1, 0, "Upgrade", 0, new(1, 0, 0, 0), null) { Level = 95, Inventory = new([blueprint, target, cube, clothItem]) };
        }

        var character = Fixture();
        var before = character.Inventory;
        var result = service.Upgrade(character, BlueprintSlot, Blueprint, TargetSlot, Target);
        That(result.Accepted && result.Plan is not null, "配方匹配时接受");
        var after = result.Plan!.After;
        That(after.Find(BlueprintSlot) is null, "设计图被整件消耗");
        var upgraded = after.Items.Single(item => item.Uid == 12);
        That(upgraded.Core.ItemId == Output && upgraded.Position == TargetSlot && upgraded.Core.Attr == 7 &&
            upgraded.Core.EnchantCardId == 900 && upgraded.Core.EmblemSocketCount == 1 && upgraded.Core.Emblem1 == 42 &&
            upgraded.Core.Random0 == new RandomOption(1, 2, 3), "产物沿用原实例并保留强化/附魔/徽章/随机属性");
        That(after.Items.Single(item => item.Uid == 13).Core.Count == 150 &&
            after.Items.Single(item => item.Uid == 14).Core.Count == 40, "只按配方数量扣材料");
        That(before.Items.Length == 4 && before.Find(TargetSlot)!.Core.ItemId == Target &&
            before.Find(BlueprintSlot) is not null, "规划不修改原快照");

        That(!service.Upgrade(Fixture(cubes: 99), BlueprintSlot, Blueprint, TargetSlot, Target).Accepted, "材料不足整单拒绝");
        That(!service.Upgrade(Fixture(cloth: 19), BlueprintSlot, Blueprint, TargetSlot, Target).Accepted, "第二种材料不足同样拒绝");
        That(!service.Upgrade(Fixture(targetLock: 1), BlueprintSlot, Blueprint, TargetSlot, Target).Accepted, "锁定装备不能升级");
        That(!service.Upgrade(Fixture(targetItemId: Other), BlueprintSlot, Blueprint, TargetSlot, Other).Accepted, "不在0号位的装备不能升级");
        That(!service.Upgrade(Fixture(), BlueprintSlot, Other, TargetSlot, Target).Accepted, "设计图槽位与id不符时拒绝");
        That(!service.Upgrade(Fixture(), new(InventoryListType.Equipment, 5), Blueprint, TargetSlot, Target).Accepted, "非Main槽位不能当设计图");
        Console.WriteLine($"PASS {Count - checks} 设计图升级行为断言");
    }
}
