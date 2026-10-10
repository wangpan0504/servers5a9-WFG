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

    public static byte[] Failure() =>
    [
        0, 4,
        0xff, 0xff, 0xff, 0xff,
        0, 0, 0, 0,
        0, 0, 0, 0,
        0, 0, 0, 0,
        0, 0, 0, 0
    ];
}
