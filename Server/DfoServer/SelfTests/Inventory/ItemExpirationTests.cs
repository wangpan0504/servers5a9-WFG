using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;
using DfoServer.Network.Builders;
using DfoServer.Network.Builders.Inventory;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing.Inventory;

internal static class ItemExpirationTests
{
    public static void RunCore()
    {
        int checks = Count;
        var maximum = new DateTimeOffset(2037, 12, 31, 6, 0, 0, TimeSpan.FromHours(8));
        int limit = checked((int)maximum.ToUnixTimeSeconds());
        That(limit == 2145823200 && ItemExpiration.MaximumUnixTime == limit,
            "客户端安全上限精确绑定UTC+8的2037-12-31六点");
        foreach (int delta in new[] { -1, 0, 1, 86400 })
        {
            int requested = limit + delta;
            int expected = Math.Min(requested, limit);
            That(new ItemExpiration(AbsoluteTime: requested).Resolve(maximum.AddDays(-2)) == expected &&
                ItemExpiration.ForClient(requested) == expected, "绝对期限在上限前、恰好及上限后采用钳位，不报错或永久化");
        }
        foreach (int days in new[] { 1, 2, int.MaxValue })
            That(new ItemExpiration(Days: days).Resolve(maximum.AddDays(-1)) == limit,
                "相对期限限制的是最终截止而非持续秒数，跨越安全日期继续按上限创建");
        That(new ItemExpiration(DailyDelete: 1).Resolve(maximum.AddSeconds(-1)) == limit &&
            new ItemExpiration(DailyDelete: 1).Resolve(maximum) == limit &&
            new ItemExpiration(DailyDelete: 1).Resolve(DateTimeOffset.MaxValue) == limit,
            "每日期限越界直接钳位，不因后续日历加法抛异常");
        long now = maximum.AddDays(-1).ToUnixTimeSeconds();
        foreach (var kind in new[] { ItemKind.Equipment, ItemKind.Consumable, ItemKind.Avatar, ItemKind.Creature })
        {
            var core = new ItemCore(100, kind, 1) { ExpireTime = int.MaxValue, Attr = 3, EnchantCardId = 123 };
            var item = new InventoryItem(1, new(InventoryListType.Main, 9), core);
            if (kind == ItemKind.Avatar)
                item = item with { Position = new(InventoryListType.Avatar, 0), Core = core with { ExpireTime = 0 },
                    Avatar = new() { ExpireTime = int.MaxValue } };
            if (kind == ItemKind.Creature)
                item = item with { Position = new(InventoryListType.Pet, 0), Core = core with { ExpireTime = 0 },
                    Creature = new() { ExpireTime = int.MaxValue } };
            That(item.RemainingSeconds(now) == 86400 && item.RemainingSeconds(limit - 1) == 1 &&
                item.RemainingSeconds(limit) == 0 && item.ExpireTime == int.MaxValue,
                "旧实例的相对下发截止受限，恰到上限归零但不改写原库存详情 " + kind);
            byte[] row = Build(writer => ItemListProtocolWriter.Write(writer, item.Position, item, now));
            int offset = kind == ItemKind.Avatar ? 6 : kind == ItemKind.Creature ? 22 : 56;
            That(BitConverter.ToInt32(row, offset) == (kind is ItemKind.Avatar or ItemKind.Creature ? 86400 : limit),
                "完整物品列表按实际期限字段钳位，不改变UID和属性字段 " + kind);
            if (kind == ItemKind.Consumable) continue;
            var worn = item with { Position = new(InventoryListType.Equipment,
                kind == ItemKind.Avatar ? (short)3 : kind == ItemKind.Creature ? (short)26 : (short)12) };
            byte[] player = Build(writer => EquipmentProtocolWriter.Player(writer, new([worn]), now));
            int playerOffset = kind == ItemKind.Avatar ? 6 : kind == ItemKind.Creature ? 27 : 28;
            That(BitConverter.ToInt32(player, playerOffset) == (kind == ItemKind.Equipment ? limit : 86400),
                "Player的装扮、宠物相对字段及普通装备绝对字段遵守同一截止 " + kind);
        }
        var permanent = new InventoryItem(2, new(InventoryListType.Main, 9), new(101, ItemKind.Equipment, 0));
        That(permanent.RemainingSeconds(now) == 0 && ItemExpiration.ForClient(0) == 0 &&
            new ItemExpiration().Resolve(maximum, true, true) == 0, "永久物品仍为0，不制造安全截止");
        var legacy = permanent with { Core = permanent.Core with { ExpireTime = int.MaxValue } };
        byte[] direct = Build(writer => ItemListProtocolWriter.Write(writer, legacy.Position.Index, legacy.Core));
        byte[] quest = QuestPackets.FixedRewardRows([new(legacy, 1)]);
        byte[] nameTag = Build(writer => EquipmentProtocolWriter.NameTag(writer,
            new([legacy with { Position = new(InventoryListType.Equipment, 30) }]), now));
        That(BitConverter.ToInt32(direct, 56) == limit && BitConverter.ToInt32(quest, 14) == limit &&
            BitConverter.ToInt32(nameTag, 4) == limit && legacy.Core.ExpireTime == int.MaxValue,
            "直接核心、任务奖励短行和名称装饰均限制旧值，只读构包不回写实例");
        var rented = legacy with { Core = legacy.Core with { IsRental = true } };
        byte[] rental = Build(writer => ItemListProtocolWriter.Write(writer, rented.Position, rented, now));
        That(BitConverter.ToInt32(rental, 56) == 86400, "租赁剩余秒也按最终安全截止换算");
        Console.WriteLine($"PASS {Count - checks} 2037期限上限、相对截止及旧实例投影断言");
    }
}
