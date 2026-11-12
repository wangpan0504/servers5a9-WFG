using System.Buffers.Binary;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Network.Parsers.Inventory;

internal static class SecretShopRequests
{
    // 037C5740的神秘商店分支只写商品ID、数量两个i32；不带NPC号或客户端报价。
    public static SecretShopPurchase Buy(ReadOnlySpan<byte> body)
    {
        if (body.Length != 8) throw new InvalidDataException("神秘商店购买请求宽度");
        return new(BinaryPrimitives.ReadInt32LittleEndian(body), BinaryPrimitives.ReadInt32LittleEndian(body[4..]));
    }

    public static bool Open(ReadOnlySpan<byte> body)
    {
        if (body.Length != 1 || body[0] > 1) throw new InvalidDataException("神秘商店开关请求宽度或状态");
        return body[0] == 1;
    }
}
