using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing.Inventory;

internal static class EquipmentTransmissionTests
{
    public static void RunCore()
    {
        byte[] wire = Convert.FromHexString("00A5765000B81D430CF81A006F320085A2F8050C0062A2F805");
        That(EquipmentTransmissionRequests.Read(wire) == new EquipmentTransmissionRequest(50, 100180613, 12, 100180578),
            "1688实收25字节请求首件为接收装备，第二件为材料装备");
        foreach (int length in new[] { 0, 12, 24, 26 })
            Reject<InvalidDataException>(() => EquipmentTransmissionRequests.Read(new byte[length]), "传承拒绝截断与尾部数据");
        foreach (byte[] invalid in new[]
        {
            wire[..13].Concat(Convert.FromHexString("FFFF85A2F8050C0062A2F805")).ToArray(),
            wire[..13].Concat(Convert.FromHexString("320085A2F805320062A2F805")).ToArray(),
            wire[..13].Concat(Convert.FromHexString("3200000000000C0062A2F805")).ToArray()
        })
            Reject<InvalidDataException>(() => EquipmentTransmissionRequests.Read(invalid), "传承拒绝负槽、同槽与空编号");
        That(EquipmentTransmissionPackets.Ack(new(true, 0), 50, 100180613)
            .SequenceEqual(Convert.FromHexString("0100000000320085A2F805")), "成功回执含结果0及接收槽和编号，共11字节");
        That(EquipmentTransmissionPackets.Ack(InventoryResult.Reject(0xD6), 50, 100180613)
            .SequenceEqual(new byte[] { 0, 0xD6, 0 }), "失败沿用本版失败头，不伪造业务成功");

        const string table = "[material item]\n0 300 400\n1 301 9\n2 302 8\n[/material item]\n" +
            "[need item rarity]\n4 5\n[/need item rarity]\n[target item list]\n100\n[/target item list]";
        var catalog = new EquipmentTransmissionCatalog(table);
        That(catalog.Targets.SetEquals([100]) && catalog.DonorRarities.SetEquals([4, 5]) &&
            catalog.Materials.SequenceEqual(new ItemMaterialCost[] { new(300, 400) }),
            "传承独立解析白名单、材料稀有度和条件0费用，不收未选择的条件费用");
        foreach (string invalid in new[]
        {
            table.Replace("0 300 400", "0 300", StringComparison.Ordinal),
            table.Replace("0 300 400", "3 300 400", StringComparison.Ordinal),
            table.Replace("0 300 400", "0 300 -1", StringComparison.Ordinal),
            table.Replace("4 5", "4 4", StringComparison.Ordinal),
            table.Replace("\n100\n", "\n100 100\n", StringComparison.Ordinal),
            table.Replace("4 5", "4 invalid", StringComparison.Ordinal),
            table.Replace("[target item list]", "[unknown]", StringComparison.Ordinal),
            table + "[target item list] 101 [/target item list]"
        })
            Reject<InvalidDataException>(() => new EquipmentTransmissionCatalog(invalid), "资源截断、非法数值与重复声明拒绝装载");

        var items = new ItemCatalog(null!, new Dictionary<int, string>(), new Dictionary<int, string>(), 0);
        var templates = new Dictionary<int, ItemTemplate>
        {
            [100] = Template(100, 6), [200] = Template(200, 4) with { AttachType = "`[trade]` " }, [201] = Template(201, 5),
            [202] = Template(202, 6), [203] = Template(203, 4) with { AttachType = "[sealing]" },
            [204] = Template(204, 4) with { EquipmentSlot = 15 },
            [205] = Template(205, 4) with { AttachType = "`[sealing]` " }
        };
        items.PrefillTemplates(templates);
        var clock = new TransmissionClock { Now = DateTimeOffset.FromUnixTimeSeconds(1000) };
        var service = new InventoryEquipmentTransmissionService(items, catalog,
            new EpicPieceCatalog("[piece list]\n50000 50001\n[/piece list]"), clock);
        var target = new InventoryItem(1, Bag(50), new(100, ItemKind.Equipment, 123456)
        {
            Attr = 0xAF, AmplifyType = 3, AmplifyValue = 78, GenuineUpgrade = 8, Durability = 24, Rune = 1,
            EnchantCardId = 900, EnchantUpgrade = 3, EmblemSocketCount = 2, Emblem1 = 901, Emblem2 = 902,
            Random0 = new(1, 2, 3), Extension114 = new(2, 23456), Extension121 = 3456,
            EpicExchangeOrigin = new(1, 999), SortLockFlag = 1, ExpireTime = 1200
        });
        var donor = new InventoryItem(2, Bag(12), new(200, ItemKind.Equipment, 999999998)
        {
            Attr = 11, AmplifyType = 1, AmplifyValue = 33, GenuineUpgrade = 5, Durability = 17,
            EnchantCardId = 800, EnchantUpgrade = 2, EmblemSocketCount = 1, Emblem1 = 801,
            Extension114 = new(1, 999), ExpireTime = 1300, Rune = 2
        });
        var material = new InventoryItem(3, Bag(121), new(300, ItemKind.Material, 450) { ExpireTime = 1100 });
        var character = new Character(1, 0, "Transmission", 0, new(1, 0, 10, 10), null)
        {
            Level = 95, Inventory = new([target, donor, material], 24, 1234567)
        };
        var request = new EquipmentTransmissionRequest(50, 100, 12, 200);
        var result = service.Transmit(character, request);
        var expected = target.Core with
        {
            EnchantCardId = 800, EnchantUpgrade = 2, EmblemSocketCount = 1, Emblem1 = 801, Emblem2 = 0, Rune = 0
        };
        That(result.Accepted && result.Plan!.After.Find(Bag(50)) == target with { Core = expected } &&
            result.Plan.After.Find(Bag(12)) is null && result.Plan.After.Find(Bag(121))?.Core.Count == 50 &&
            result.Plan.After.Gold == 1234567, "传承清除特效且不继承供体特效，覆盖附魔和徽章孔，保留目标其他核心及金币");
        That(character.Inventory.Items.SequenceEqual([target, donor, material]), "规划不修改输入快照");
        That(result.Plan!.CharacterPrecondition!.Matches(character) &&
            !result.Plan.CharacterPrecondition.Matches(character with { Revision = character.Revision + 1 }),
            "传承计划绑定原角色版本，不能套用到更新后的角色");
        clock.Now = DateTimeOffset.FromUnixTimeSeconds(1100);
        That(result.Plan.CharacterPrecondition.IsExpired() && !service.Transmit(character, request).Accepted,
            "实际消费材料的到期边界在规划和提交时均有效");
        clock.Now = DateTimeOffset.FromUnixTimeSeconds(1000);

        foreach (var bad in new[]
        {
            request with { DonorSlot = 50, DonorId = 100 }, request with { TargetSlot = 12, TargetId = 200, DonorSlot = 50, DonorId = 100 },
            request with { DonorId = 201 }, request with { TargetSlot = 51 }, request with { TargetId = 200 }
        })
            That(!service.Transmit(character, bad).Accepted, "拒绝同实例、反向、编号错配和不存在槽位");
        foreach (var badDonor in new[]
        {
            donor with { Uid = 0 }, donor with { Uid = 1 }, donor with { Position = new(InventoryListType.Equipment, 12) },
            donor with { Core = donor.Core with { EquipmentLockId = 1 } },
            donor with { Core = donor.Core with { IsRental = true } },
            donor with { Core = donor.Core with { ExpireTime = 1000 } },
            donor with { Core = donor.Core with { EnchantCardId = 0, EmblemSocketCount = 0, Emblem1 = 0 } },
            donor with { Core = donor.Core with { SealFlag = 1 } },
            donor with { Core = donor.Core with { ItemId = 202 } },
            donor with { Core = donor.Core with { ItemId = 203 } },
            donor with { Core = donor.Core with { ItemId = 204 } },
            donor with { Core = donor.Core with { ItemId = 205 } }
        })
        {
            var changed = character with { Inventory = character.Inventory with { Items = [target, badDonor, material] } };
            That(!service.Transmit(changed, request with { DonorId = badDonor.Core.ItemId }).Accepted,
                "无效UID、穿戴容器、锁、租赁、到期、空打造、封装来源、稀有度或部位拒绝且无计划");
        }
        foreach (var changed in new[]
        {
            character with { Inventory = character.Inventory with { Items = [target, donor, material with { Core = material.Core with { Value = 399 } }] } },
            character with { Inventory = character.Inventory with { Items = [target with { Core = target.Core with { EquipmentLockId = 1 } }, donor, material] } },
            character with { Inventory = result.Plan.After }
        })
            That(!service.Transmit(changed, request).Accepted, "材料不足、目标锁定和消耗后重放拒绝");
        var noEnchant = donor with { Core = donor.Core with { ItemId = 201, EnchantCardId = 0, EnchantUpgrade = 0 } };
        var onlyEmblem = service.Transmit(character with { Inventory = character.Inventory with { Items = [target, noEnchant, material] } },
            request with { DonorId = 201 });
        That(onlyEmblem.Accepted && onlyEmblem.Plan!.After.Find(Bag(50))!.Core.EnchantCardId == 0,
            "勇者材料可传承徽章，并清除接收装备旧附魔，不取两件较高值");
        var noSockets = donor with { Core = donor.Core with { EmblemSocketCount = 0, Emblem1 = 0, Emblem2 = 0 } };
        var onlyEnchant = service.Transmit(character with { Inventory = character.Inventory with { Items = [target, noSockets, material] } }, request);
        That(onlyEnchant.Accepted && onlyEnchant.Plan!.After.Find(Bag(50))!.Core is { EmblemSocketCount: 0, Emblem1: 0, Emblem2: 0 },
            "仅附魔供体覆盖并清除目标旧徽章及孔信息");
        var unchanged = target with
        {
            Core = expected with { ExpireTime = 1050 }
        };
        var noCoreChange = service.Transmit(character with { Inventory = character.Inventory with { Items = [unchanged, donor, material] } }, request);
        clock.Now = DateTimeOffset.FromUnixTimeSeconds(1050);
        That(noCoreChange.Accepted && noCoreChange.Plan!.CharacterPrecondition!.IsExpired(),
            "目标附魔徽章已相同时也保留目标资格期限，不能因无目标差量漏掉到期守卫");
    }

    static InventorySlot Bag(short slot) => new(InventoryListType.Main, slot);
    static ItemTemplate Template(int id, int rarity) => new(id, ItemKind.Equipment, 16, 95, 40, 1, 0,
        rarity, 3, "[trade]", ushort.MaxValue, 0, "fixture");
    sealed class TransmissionClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; }
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
