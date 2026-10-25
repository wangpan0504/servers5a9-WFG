using System.Collections.Frozen;
using System.Collections.Immutable;
using DfoServer.GameWorld.Items;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Network.Parsers.Inventory;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing.Inventory;

internal static class EquipmentUpgradeTests
{
    public static void RunCore()
    {
        var request = EquipmentUpgradeRequests.ReadUpgrade(System.Convert.FromHexString("0C00CBF4F605010B0001E4F4F60501"));
        That(request == new EquipmentUpgradeRequest(12, 100070603, true, 11, 0, true, 100070628, true),
            "1564实收请求保留槽位、源目标与逐项打造选择");
        That(EquipmentUpgradeRequests.ReadFusion(System.Convert.FromHexString("0C00CBF4F60516000B1CF705")) ==
            new EquipmentFusionRequest(12, 100070603, 22, 100080651), "1644实收请求按源目标双槽双编号解析");
        That(EquipmentUpgradeRequests.ReadDarkRevelation(System.Convert.FromHexString("2600AC9B05060104")) ==
            new DarkRevelationRequest(38, 101030828, 1, 4), "1605实收请求保留权能槽与能力编号");
        foreach (byte[] body in new byte[][] { [], [0], new byte[14], new byte[16],
            System.Convert.FromHexString("FFFFCBF4F605010B0001E4F4F60501"),
            System.Convert.FromHexString("0C00CBF4F605020B0001E4F4F60501") })
            Reject<InvalidDataException>(() => EquipmentUpgradeRequests.ReadUpgrade(body), "升级请求拒绝长度和字段越界");
        const string table = """
            [group]
            [index]
            1
            [need item]
            100 96 101 10
            [/need item]
            [min upgrade]
            10
            [add upgrade rate]
            11 1.50 12 2.10
            [/add upgrade rate]
            [/group]
            [group]
            [index]
            4
            [need item]
            102 5 0 500000
            [/need item]
            [inherit ability]
            [/group]
            [upgrade item list]
            1 200 1 201 4 300 2 301 302
            [/upgrade item list]
            [fusion item list]
            1 200 201 3 201 202 203
            [/fusion item list]
            [prey weapon imprint item list]
            400 401
            [/prey weapon imprint item list]
            [prey weapon imprint need item]
            102 10 0 1000000
            [/prey weapon imprint need item]
            """;
        var catalog = new EquipmentUpgradeCatalog(table);
        var priceTable = new EquipmentUpgradeCatalog("""
            [group]
            [index]
            1
            [need item]
            100 96
            [/need item]
            [fusion need item]
            101 10
            [/fusion need item]
            [addition need item]
            0 1000000
            [/addition need item]
            [add upgrade rate]
            11 1.50 12 2.10 13 2.00 14 2.00
            [/add upgrade rate]
            [add separate rate]
            6 1.20 7 1.40 8 1.60
            [/add separate rate]
            [card enchant need item]
            3 0 1000000
            [/card enchant need item]
            [emblem enchant need item]
            3 0 1000000
            [/emblem enchant need item]
            [/group]
            """);
        var priceGroup = priceTable.Groups[1];
        int Gold(int level, int separate, int? card = null, int[]? emblems = null) =>
            EquipmentUpgradeCosts.Calculate(priceGroup, false, level, separate, card, emblems ?? [])
                .Single(cost => cost.ItemId == 0).Count;
        That(Gold(10, 5) == 1000000 && Gold(11, 0, 3, [3, 3]) == 4500000,
            "升级费用匹配基础与实收加十一预览，附魔徽章在倍率后追加");
        That(Gold(12, 0) == 3140000 && Gold(13, 0) == 6290000,
            "强化倍率逐档单精度截断并向下取整到万，不直接取最终档倍率");
        That(Gold(11, 8) == 2680000 && Gold(12, 8) == 3140000,
            "强化和锻造选择较贵的一支，不将两种倍率叠乘");
        var preyPriceGroup = priceGroup with
        {
            AdditionMaterials = [new(0, 1650000)],
            UpgradeRates = new Dictionary<int, decimal> { [11] = 1.21m }.ToFrozenDictionary(),
            CardMaterials = new Dictionary<int, ImmutableArray<ItemMaterialCost>> { [3] = [new(0, 700000)] }.ToFrozenDictionary()
        };
        That(EquipmentUpgradeCosts.Calculate(preyPriceGroup, false, 11, 0, 3, [3, 3])
            .Single(cost => cost.ItemId == 0).Count == 4690000,
            "普雷加十一、稀有附魔和两枚稀有徽章费用匹配四百六十九万原生预览");
        var fusionPrice = EquipmentUpgradeCosts.Calculate(priceGroup, true, 10, 0, null, []);
        That(fusionPrice.Any(cost => cost.ItemId == 101 && cost.Count == 10) &&
            fusionPrice.All(cost => cost.ItemId != 100), "刻印只收刻印材料，保留共用打造费用");
        That(catalog.Upgrades[200].Targets.SequenceEqual([201]) &&
            catalog.Upgrades[300].Targets.SequenceEqual([301, 302]), "升级映射按变长行读取且保留候选顺序");
        That(catalog.Fusions[200] is { Group: 1, UpgradedEquivalent: 201 } fusion &&
            fusion.Targets.SequenceEqual([201, 202, 203]) && !catalog.Fusions.ContainsKey(201),
            "刻印源与目标不互换，材质候选只来自资源白名单");
        That(catalog.Groups[1].UpgradeRates[11] == 1.50m && catalog.Groups[1].UpgradeRates[12] == 2.10m &&
            catalog.Groups[4].InheritAbility && catalog.Groups[4].Materials[1] is { ItemId: 0, Count: 500000 },
            "倍率保留小数，转换继承标记和金币费用保留原生含义");
        That(catalog.WeaponImprints[400] == 401 && catalog.WeaponImprintMaterials[1] is { ItemId: 0, Count: 1000000 },
            "权能融合使用独立武器映射与费用");
        foreach (string malformed in new[]
        {
            table.Replace("1 200 1 201", "1 200 2 201", StringComparison.Ordinal),
            table.Replace("1 200 201 3 201 202 203", "1 200 201 3 201 202 202", StringComparison.Ordinal),
            table.Replace("4 300 2 301 302", "8 300 2 301 302", StringComparison.Ordinal),
            table.Replace("400 401", "400", StringComparison.Ordinal),
            table.Replace("100 96 101 10", "100 -1 101 10", StringComparison.Ordinal)
        })
            Reject<InvalidDataException>(() => new EquipmentUpgradeCatalog(malformed), "拒绝截断、重复、未知组与负费用资源");

        var items = new ItemCatalog(null!, new Dictionary<int, string>(), new Dictionary<int, string>(), 0);
        items.PrefillTemplates(new[] { 300, 301, 302 }.ToDictionary(id => id,
            id => new ItemTemplate(id, ItemKind.Equipment, 16, 95, 50, 1, 0, 4, 3, "[trade]", ushort.MaxValue, 0, "fixture")));
        var service = new InventoryEquipmentUpgradeService(items, catalog,
            new EpicPieceCatalog("[piece list]\n50000 50001\n[/piece list]"));
        var core = new ItemCore(300, ItemKind.Equipment, 999999998)
        {
            Attr = 15, AmplifyType = 2, AmplifyValue = 45, EnchantCardId = 2000, EnchantUpgrade = 3,
            EmblemSocketCount = 2, Emblem1 = 3000, Emblem2 = 3001, GenuineUpgrade = 8,
            Extension114 = new(1, 12345), Extension121 = 2345, Durability = 40
        };
        var source = new InventoryItem(1, new(InventoryListType.Main, 26), core);
        var material = new InventoryItem(2, new(InventoryListType.Main, 30), new(102, ItemKind.Material, 10));
        var character = new Character(1, 0, "Upgrade", 0, new(1, 0, 10, 10), null)
        {
            Level = 95, Inventory = new([source, material], 24, 600000)
        };
        var converted = service.Convert(character, 26, 300, 301);
        That(converted.Accepted && converted.Plan!.After.Gold == 100000 &&
            converted.Plan.After.Find(material.Position)?.Core.Count == 5, "权能转换一次规划扣固定材料与金币");
        var target = converted.Plan!.After.Find(source.Position)!;
        That(target.Uid == source.Uid && target.Core with { ItemId = 300 } == core,
            "权能转换保留UID、品级和完整打造及扩展字段");
        foreach (var rejected in new[]
        {
            service.Convert(character, 26, 300, 300),
            service.Convert(character, 26, 300, 999),
            service.Convert(character, 26, 301, 302),
            service.Convert(character with { Inventory = character.Inventory with { Gold = 499999 } }, 26, 300, 301),
            service.Convert(character with { Inventory = character.Inventory with { Items = [source] } }, 26, 300, 301),
            service.Convert(character with { Inventory = character.Inventory with
                { Items = character.Inventory.Items.Replace(source, source with { Core = core with { EquipmentLockId = 1 } }) } }, 26, 300, 301),
            service.Convert(character with { Inventory = converted.Plan.After }, 26, 300, 301)
        })
            That(!rejected.Accepted && rejected.Plan is null, "无效目标、过期请求、缺材料金币或安全锁不产生资产规划");
        That(character.Inventory.Find(source.Position) == source && character.Inventory.Gold == 600000 &&
            character.Inventory.Find(material.Position)?.Core.Count == 10, "成功与失败规划都不修改输入快照");

        items.PrefillTemplates(new[] { 200, 201, 203, 400, 401, 2000, 3000, 3001 }.ToDictionary(id => id,
            id => new ItemTemplate(id, ItemKind.Equipment, 16, 95, 50, 1, 0, 4, 3, "[trade]", ushort.MaxValue, 0, "fixture")));
        var donor = source with { Core = core with { ItemId = 200, Attr = 11 } };
        var recipient = new InventoryItem(3, new(InventoryListType.Main, 27),
            new(203, ItemKind.Equipment, 123456) { Attr = 15, Durability = 20, Extension114 = new(2, 98765) });
        var imprintCharacter = character with { Inventory = character.Inventory with { Items = [donor, recipient] } };
        var imprint = service.Imprint(imprintCharacter, new(26, 200, 27, 203));
        var imprinted = imprint.Plan?.After.Find(recipient.Position);
        That(imprint.Accepted && imprint.Plan!.After.Find(donor.Position) is null &&
            imprinted?.Uid == recipient.Uid && imprinted.Core.Value == recipient.Core.Value &&
            imprinted.Core.Durability == 20 && imprinted.Core.Extension114 == recipient.Core.Extension114,
            "刻印消耗源实例，保留目标UID、品级、耐久和独立扩展");
        That(imprinted!.Core.Upgrade == 11 && imprinted.Core.AmplifyType == core.AmplifyType &&
            imprinted.Core.AmplifyValue == core.AmplifyValue && imprinted.Core.EnchantCardId == core.EnchantCardId &&
            imprinted.Core.EnchantUpgrade == core.EnchantUpgrade && imprinted.Core.GenuineUpgrade == core.GenuineUpgrade &&
            imprinted.Core.EmblemSocketCount == 2 && imprinted.Core.Emblem1 == core.Emblem1 && imprinted.Core.Emblem2 == core.Emblem2,
            "刻印将目标加十五覆盖为源加十一，继承源附魔、增幅、锻造与徽章");
        That(!service.Imprint(imprintCharacter, new(26, 200, 26, 200)).Accepted &&
            !service.Imprint(imprintCharacter, new(27, 203, 26, 200)).Accepted &&
            !service.Imprint(imprintCharacter with { Inventory = imprint.Plan!.After }, new(26, 200, 27, 203)).Accepted,
            "刻印拒绝同实例、反向映射与重复旧源请求");
        var upgradeCharacter = character with { Inventory = character.Inventory with
        {
            Items = [donor, new(4, new(InventoryListType.Main, 31), new(100, ItemKind.Material, 96)),
                new(5, new(InventoryListType.Main, 32), new(101, ItemKind.Material, 10))]
        } };
        var upgradeRequest = new EquipmentUpgradeRequest(26, 200, true, 11, 8, true, 201, true);
        var upgrade = service.Upgrade(upgradeCharacter, upgradeRequest);
        That(upgrade.Accepted && upgrade.Plan!.After.Find(donor.Position)?.Core == donor.Core with { ItemId = 201 } &&
            upgrade.Plan.After.Items.Length == 1, "升级使用精确目标映射并原子消耗全部材料，选择保留的打造不变");
        var stripped = service.Upgrade(upgradeCharacter,
            upgradeRequest with { Upgrade = 0, GenuineUpgrade = 0, InheritEnchant = false, InheritEmblems = false });
        That(stripped.Accepted && stripped.Plan!.After.Find(donor.Position)?.Core is
            { Upgrade: 0, GenuineUpgrade: 0, EnchantCardId: 0, EnchantUpgrade: 0, EmblemSocketCount: 0, Emblem1: 0, Emblem2: 0 },
            "升级未选择保留的打造清零，不暗中全继承");
        That(!service.Upgrade(upgradeCharacter, upgradeRequest with { Upgrade = 12 }).Accepted &&
            !service.Upgrade(upgradeCharacter, upgradeRequest with { TargetId = 203 }).Accepted &&
            !service.Upgrade(upgradeCharacter, upgradeRequest with { Amplify = false }).Accepted,
            "升级拒绝提高打造等级、伪造增幅状态和表外目标");
        var weaponDonor = donor with { Core = donor.Core with { ItemId = 400 } };
        var weaponRecipient = recipient with { Core = recipient.Core with { ItemId = 401 } };
        var weaponCharacter = character with { Inventory = character.Inventory with
            { Items = [weaponDonor, weaponRecipient, material] } };
        var weaponRequest = new EquipmentFusionRequest(26, 400, 27, 401);
        var weaponFusion = service.Imprint(weaponCharacter, weaponRequest, weaponFusion: true);
        That(!weaponFusion.Accepted && weaponFusion.Plan is null,
            "权能融合缺金币不能产生资产规划");
        weaponCharacter = weaponCharacter with { Inventory = weaponCharacter.Inventory with { Gold = 1600000 } };
        weaponFusion = service.Imprint(weaponCharacter, weaponRequest, weaponFusion: true);
        That(weaponFusion.Accepted && weaponFusion.Plan!.After.Gold == 600000 &&
            weaponFusion.Plan.After.Find(material.Position) is null && weaponFusion.Plan.After.Find(weaponDonor.Position) is null &&
            weaponFusion.Plan.After.Find(weaponRecipient.Position)?.Core.Upgrade == 11,
            "权能融合按独立表扣传说灵魂十个及金币一百万，源打造覆盖目标");
        That(!service.Imprint(weaponCharacter, weaponRequest).Accepted &&
            !service.Imprint(weaponCharacter, weaponRequest with { TargetId = 203 }, weaponFusion: true).Accepted,
            "普通刻印与权能融合各自受精确映射约束，不能混用费用");
    }
}
