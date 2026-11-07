using System.Buffers.Binary;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing.Inventory;

internal static class EquipmentEffectTests
{
    internal static byte[] Request(int source, int target)
    {
        var body = Convert.FromHexString("C841334DF4F719006C00000000090000006C000000");
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(13), target);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(17), source);
        return body;
    }

    public static void RunCore()
    {
        int start = Count;
        That(EquipmentEffectRequests.Read(Request(108, 9)) == new EquipmentEffectRequest(108, 9),
            "953按真实21B请求末尾读取目标和源槽，不把13B栈内容当帧头或身份");
        var opaque = Request(108, 9); Array.Fill(opaque, (byte)0xFF, 0, 13);
        That(EquipmentEffectRequests.Read(opaque) == new EquipmentEffectRequest(108, 9), "未定义前缀不参与目标选择");
        foreach (var bad in new[] { Request(108, 9)[..^1], Request(108, 9).Concat(new byte[1]).ToArray(),
            Request(-1, 9), Request(65536, 9), Request(108, -1), Request(108, 108) })
            Reject<InvalidDataException>(() => EquipmentEffectRequests.Read(bad));

        var ticket = new ItemTemplate(2682369, ItemKind.Consumable, 0, 1, 0, 1000, 1, 1, 1, "[trade]", ushort.MaxValue, 0, "桃花");
        var weapon = new ItemTemplate(9001, ItemKind.Equipment, 12, 95, 50, 1, 1, 5, 95, "[trade]", ushort.MaxValue, 0, "武器");
        var strings = new[] { "[stackable type]", "[equipment effect]", "[int data]", "[/int data]" };
        PvfToken[] Tokens(int effect) => [new(3, 0), new(6, 1), new(0, 0), new(3, 2), new(0, effect), new(3, 3)];
        EquipmentEffectDefinition? Parse(PvfToken[] tokens) => EquipmentEffectDefinition.Parse(ticket, tokens, index => strings[index], effect => effect == 1);
        var definition = Parse(Tokens(1));
        That(definition is { Effect: 1 } && Parse(Tokens(0)) is { Effect: 0 }, "附加与解除按int data实际编号分流");
        That(Parse(Tokens(-1)) is null && Parse(Tokens(65536)) is null && Parse(Tokens(2)) is null &&
            Parse([..Tokens(1), new(3, 2), new(0, 1), new(3, 3)]) is null,
            "负数、溢出、未声明特效及重复声明拒绝，不默认改成桃花");

        var source = new InventoryItem(1, new(InventoryListType.Main, 108), new(ticket.Id, ItemKind.Consumable, 2));
        var target = new InventoryItem(2, new(InventoryListType.Main, 9), new(weapon.Id, ItemKind.Equipment, 123456)
            { Attr = 15, AmplifyType = 2, AmplifyValue = 150, GenuineUpgrade = 8, EnchantCardId = 3175,
                EmblemSocketCount = 2, Emblem1 = 2510044, Emblem2 = 2510044, Extension114 = new(1, 12345), SortLockFlag = 1 });
        var role = new Character(1, 0, "Effect", 0, new(1, 0, 1, 1), null) { Level = 95, Inventory = new([source, target]) };
        var clock = new ConsumableUseSessionTests.FakeClock();
        InventoryResult Plan(Character value, EquipmentEffectDefinition? effect = null, ItemTemplate? gear = null) =>
            InventoryEquipmentEffectService.Plan(value, 108, 9, effect ?? definition, gear ?? weapon, clock);
        var attached = Plan(role);
        That(attached.Accepted && attached.Plan!.After.Find(source.Position)?.Core.Count == 1 &&
            attached.Plan.After.Find(target.Position) == target with { Core = target.Core with { Rune = 1 } },
            "扣一张与附加特效同计划，完整保留武器UID、品级、打造和未知扩展");
        var after = role with { Inventory = attached.Plan!.After };
        That(!Plan(after).Accepted, "重复请求相同特效不再扣符文");
        var removed = Plan(after, definition! with { Effect = 0 });
        That(removed.Accepted && removed.Plan!.After.Find(source.Position) is null &&
            removed.Plan.After.Find(target.Position) == target, "解除只扣一张，恢复特效零值并保留完整实例");
        That(!Plan(role, definition! with { Effect = 0 }).Accepted, "无特效不扣解除符文");
        Bytes(EquipmentEffectPackets.Ack(attached, 9),
            [1, 0, 0, 0, 0, 0x29, 0x23, 0, 0, 1, 0, 0, 0, 9, 0, 0, 0], "953成功含完整16B结果，编号和目标槽匹配实际实例");
        Bytes(EquipmentEffectPackets.Ack(InventoryResult.Reject(19), 9), [0, 19, 0], "拒绝回执结束客户端等待");

        foreach (var core in new[] { target.Core with { SealFlag = 1 }, target.Core with { EquipmentLockId = 1 },
            target.Core with { IsRental = true }, target.Core with { ExpireTime = 1899999999 } })
            That(!Plan(role with { Inventory = new([source, target with { Core = core }]) }).Accepted,
                "封装、锁定、租赁和过期武器不扣道具");
        That(!Plan(role, gear: weapon with { EquipmentSlot = 14 }).Accepted &&
            !Plan(role, gear: weapon with { Rarity = 1 }).Accepted &&
            !Plan(role with { Inventory = new([source with { Core = source.Core with { EquipmentLockId = 1 } }, target]) }).Accepted &&
            !Plan(role with { Inventory = new([source, target with { Position = new(InventoryListType.Equipment, 12) }]) }).Accepted,
            "非武器、低稀有度、锁定源及已穿戴目标不被源前缀绕过");
        Console.WriteLine($"PASS {Count - start} 装备特效资源、请求、规划及回执断言");
    }
}
