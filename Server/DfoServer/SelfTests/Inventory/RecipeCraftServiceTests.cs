using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.Network.Builders.Inventory;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

// [recipe] 设计图制造的行为断言：按配方扣设计图与材料、产物沿用原实例、
// 以及缺少被改造装备、材料不足、目标被锁、非配方物品时整单拒绝。
internal static class RecipeCraftServiceTests
{
    const int Blueprint = 65001, Target = 65002, Output = 65003, Cube = 65004, Cloth = 65005;
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
            [Blueprint] = new(Blueprint, ItemKind.Consumable, 0, 90, 0, 999, 10, 4, 90, "[trade]", 255, 0, "设计图"),
            [Target] = new(Target, ItemKind.Equipment, 14, 90, 0, 0, 10, 6, 90, "[trade]", 255, 0, "原装备"),
            [Output] = new(Output, ItemKind.Equipment, 14, 90, 0, 0, 10, 4, 90, "[trade]", 255, 0, "产物"),
            [Cube] = new(Cube, ItemKind.Material, 0, 1, 0, 999, 0, 3, 1, "[trade]", 255, 0, "晶块"),
            [Cloth] = new(Cloth, ItemKind.Material, 0, 1, 0, 999, 0, 3, 1, "[trade]", 255, 0, "布片")
        });
        var recipes = new ItemRecipeCatalog([new ItemRecipeDefinition(Blueprint, Target,
            [new ItemMaterialCost(Cube, 100), new ItemMaterialCost(Cloth, 20)], Output, 1)]);
        var service = new InventoryRecipeCraftService(recipes, catalog,
            new EpicPieceCatalog("[piece list]\n60000 70001\n[/piece list]"));

        Character Fixture(int cubes = 250, int cloth = 60, byte targetLock = 0, bool withTarget = true, int blueprintItem = Blueprint)
        {
            var items = new List<InventoryItem>
            {
                new(11, BlueprintSlot, new(blueprintItem, ItemKind.Consumable, 1)),
                new(13, CubeSlot, new(Cube, ItemKind.Material, cubes)),
                new(14, ClothSlot, new(Cloth, ItemKind.Material, cloth))
            };
            if (withTarget)
                items.Add(new InventoryItem(12, TargetSlot, new(Target, ItemKind.Equipment, 1)
                {
                    Attr = 7, GenuineUpgrade = 3, EnchantCardId = 900, EmblemSocketCount = 1, Emblem1 = 42,
                    Random0 = new(1, 2, 3), EquipmentLockId = targetLock
                }));
            return new Character(1, 0, "Recipe", 0, new(1, 0, 0, 0), null) { Level = 90, Inventory = new([.. items]) };
        }

        var character = Fixture();
        var before = character.Inventory;
        var result = service.Craft(character, BlueprintSlot, out var receipt);
        That(result.Accepted && result.Plan is not null, "配方匹配时接受");
        That(receipt is { ItemId: Output, Count: 1 }, "回执携带产物编号与数量");
        That(CompoundPackets.Result(result) is [1, 0, 0, 0], "成功回包为4字节 01 00 00 00");
        That(CompoundPackets.Result(InventoryResult.Reject(7)) is [0, 7, 0], "失败回包为0+u16错误码");
        var after = result.Plan!.After;
        That(after.Find(BlueprintSlot) is null, "设计图被整件消耗");
        var crafted = after.Items.Single(item => item.Uid == 12);
        That(crafted.Core.ItemId == Output && crafted.Position == TargetSlot && crafted.Core.Attr == 7 &&
            crafted.Core.GenuineUpgrade == 3 && crafted.Core.EnchantCardId == 900 && crafted.Core.EmblemSocketCount == 1 &&
            crafted.Core.Emblem1 == 42 && crafted.Core.Random0 == new RandomOption(1, 2, 3),
            "产物沿用原实例并保留强化/锻造/附魔/徽章/随机属性");
        That(after.Items.Single(item => item.Uid == 13).Core.Count == 150 &&
            after.Items.Single(item => item.Uid == 14).Core.Count == 40, "只按配方数量扣材料");
        That(before.Items.Length == 4 && before.Find(TargetSlot)!.Core.ItemId == Target, "规划不修改原快照");

        That(!service.Craft(Fixture(cubes: 99), BlueprintSlot, out _).Accepted, "材料不足整单拒绝");
        That(!service.Craft(Fixture(cloth: 19), BlueprintSlot, out _).Accepted, "第二种材料不足同样拒绝");
        That(!service.Craft(Fixture(targetLock: 1), BlueprintSlot, out _).Accepted, "锁定装备不能制造");
        That(!service.Craft(Fixture(withTarget: false), BlueprintSlot, out _).Accepted, "缺少被改造装备时拒绝");
        That(!service.Craft(Fixture(blueprintItem: Cube), BlueprintSlot, out _).Accepted, "槽位不是配方物品时拒绝");
        That(!service.Craft(Fixture(), new(InventoryListType.PersonalCargo, 5), out _).Accepted, "非Main槽位不能当设计图");
        // 请求宽度与字段：实机 9 字节 610000000001000001 = 槽位97/方式0/数量1/尾 00 01。
        var extended = DfoServer.Network.Parsers.Inventory.CompoundRequests.Read(Convert.FromHexString("610000000001000001"));
        That(extended.Raw == 97 && extended.Mode == 0 && extended.Count == 1, "9字节请求解出槽位/方式/数量");
        var short7 = DfoServer.Network.Parsers.Inventory.CompoundRequests.Read(Convert.FromHexString("61000000000100"));
        That(short7.Raw == 97 && short7.Mode == 0 && short7.Count == 1, "7字节请求解出同样字段");
        Reject<InvalidDataException>(() => DfoServer.Network.Parsers.Inventory.CompoundRequests.Read(
            Convert.FromHexString("610000000001000002")), "9字节尾部不是0001时拒绝");
        Reject<InvalidDataException>(() => DfoServer.Network.Parsers.Inventory.CompoundRequests.Read(
            Convert.FromHexString("610000000000000001")), "数量为零时拒绝");
        Reject<InvalidDataException>(() => DfoServer.Network.Parsers.Inventory.CompoundRequests.Read(
            Convert.FromHexString("61000000020100")), "来源方式不是0或1时拒绝");
        Console.WriteLine($"PASS {Count - checks} 配方制造行为断言");
    }
}
