using System.Buffers.Binary;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;

namespace DfoServer.Network.Builders.Inventory;

internal static class CashShopPackets
{
    // C2S64 的当前客户端 ACK：success、保留字节、category=-1、商品号及三个保留 u32、奖励条数。
    public static byte[] Success(CashShopProduct product, IReadOnlyList<CashShopBonus>? bonuses = null)
    {
        bonuses ??= [];
        byte[] body = new byte[24 + checked(8 * bonuses.Count)];
        body[0] = 1;
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(2), -1);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(6), checked((uint)product.CommodityId));
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(22), checked((ushort)bonuses.Count));
        int offset = 24;
        foreach (var bonus in bonuses)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(offset), checked((uint)bonus.ItemId));
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(offset + 4), checked((uint)bonus.Count));
            offset += 8;
        }
        return body;
    }

    // 失败响应的 error 是 u16（分派器读 body[1..3]）。客户端 64 失败分支 02357D32 按 error 分流，
    // 只有 error==0（02357D71 显示 3EEh）与 error==0x6D（02357D8F 显示 15BDh"不能作为礼物赠送"）
    // 两条直接路径是安全的；其余任何 error（含商城码 0x82..0x89）都走 02357DAD→03A1F600→03A1F780，
    // 后者访问 "0000"(0x30303030) 空对象 → 01CBD7D0 c0000005（实机两次闪退：error=17 与 error=0x82）。
    // 故失败统一回 error=0（3EEh 通用提示），不区分具体失败原因，待客户端失败分支合同取证后细分。
    public static byte[] Failure() =>
    [
        0,          // success = 0
        0, 0,       // error u16 = 0
    ];

    // Noti 863 (0239CF20)：i32 条数 + 每条 (i32 restrictId, i32 expireTime, i32 remainingCount)。
    public static byte[] BuyRestrictions(InventorySnapshot inventory, CashShopCatalog shop, DateTimeOffset utcNow)
    {
        int today = ShopPurchaseCount.Today(utcNow);
        var entries = new List<(int RestrictId, int ExpireTime, int Remaining)>();
        foreach (var restriction in shop.BuyRestrictions.Values.OrderBy(row => row.RestrictId))
        {
            if (restriction.TryGetPeriod(out var period))
            {
                var key = new ShopPurchaseCount(0, restriction.CommodityId, restriction.AccountWide, false,
                    ShopPurchaseCount.PeriodDay(today, period), 0) { Period = period };
                var row = inventory.ShopPurchases.SingleOrDefault(key.SameKey);
                int used = row?.Effective(today) ?? 0;
                if (used <= 0) continue;
                int expireTime = (int)Math.Min(key.NextReset(today) ?? 0, ItemExpiration.MaximumUnixTime);
                entries.Add((restriction.RestrictId, expireTime, Math.Max(0, restriction.Maximum - used)));
            }
            else
            {
                var probe = new ShopPurchaseCount(0, restriction.CommodityId, restriction.AccountWide, false, today, 0)
                { Period = ItemPurchasePeriod.Daily };
                var row = inventory.ShopPurchases.SingleOrDefault(probe.SameKey);
                if (row is null || today < row.Day || today - row.Day >= restriction.PeriodCode || row.Count <= 0)
                    continue;
                long resetUnix = new DateTimeOffset(DateOnly.FromDayNumber(row.Day).AddDays(restriction.PeriodCode),
                    new TimeOnly(6, 0), TimeSpan.FromHours(8)).ToUnixTimeSeconds();
                entries.Add((restriction.RestrictId, (int)Math.Min(resetUnix, ItemExpiration.MaximumUnixTime),
                    Math.Max(0, restriction.Maximum - row.Count)));
            }
        }
        byte[] body = new byte[4 + checked(12 * entries.Count)];
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(0, 4), entries.Count);
        int offset = 4;
        foreach (var (restrictId, expireTime, remaining) in entries)
        {
            BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(offset, 4), restrictId);
            BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(offset + 4, 4), expireTime);
            BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(offset + 8, 4), remaining);
            offset += 12;
        }
        return body;
    }
}
