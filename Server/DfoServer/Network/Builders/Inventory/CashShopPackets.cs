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
}
