using System.Buffers.Binary;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Network.Parsers.Inventory;

internal static class CashShopRequests
{
    const int HeaderSize = 4;
    const int ItemStride = 15;
    const int MaxItems = 64;

    public static CashShopPurchaseRequest Buy(ReadOnlySpan<byte> body)
    {
        if (body.Length < HeaderSize + ItemStride)
            throw new InvalidDataException("商城购买请求长度不足");
        int count = body[2] == 0 ? 1 : body[2];
        if (count > MaxItems || body.Length < HeaderSize + count * ItemStride)
            throw new InvalidDataException("商城购买请求商品数量无效");
        var items = new CashShopPurchaseItem[count];
        for (int index = 0; index < count; index++)
        {
            int offset = HeaderSize + index * ItemStride;
            uint commodity = BinaryPrimitives.ReadUInt32LittleEndian(body[(offset + 3)..]);
            if (commodity == 0 || commodity > int.MaxValue)
                throw new InvalidDataException("商城商品号无效");
            items[index] = new((int)commodity, body[offset + 1]);
        }
        return new(body[4], items);
    }
}
