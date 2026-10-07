using System.Buffers.Binary;
using DfoServer.Game.Inventory.Models;
using BuyRequest = DfoServer.Game.Inventory.Models.BuyItemRequest;
using SellRequest = DfoServer.Game.Inventory.Models.SellItemRequest;

namespace DfoServer.Network.Parsers.Inventory;

internal static class ShopRequests
{
    public static int PurchaseCounts(ReadOnlySpan<byte> body)
    {
        if (body.Length != 4)
            throw new InvalidDataException("Client商店限购查询宽度");
        return BinaryPrimitives.ReadInt32LittleEndian(body);
    }
    public static BuyRequest Buy(ReadOnlySpan<byte> body)
    {
        if (body.Length != 16)
            throw new InvalidDataException("Client商店购买请求宽度");
        return new(BinaryPrimitives.ReadInt32LittleEndian(body), BinaryPrimitives.ReadInt32LittleEndian(body[4..]),
            BinaryPrimitives.ReadInt32LittleEndian(body[8..]), BinaryPrimitives.ReadInt32LittleEndian(body[12..]));
    }
    public static SellRequest Sell(ReadOnlySpan<byte> body)
    {
        if (body.Length != 19)
            throw new InvalidDataException("Client商店出售请求宽度");
        return new(new((InventoryListType)body[0], BinaryPrimitives.ReadInt16LittleEndian(body[1..])),
            BinaryPrimitives.ReadInt32LittleEndian(body[3..]), BinaryPrimitives.ReadInt32LittleEndian(body[7..]),
            BinaryPrimitives.ReadInt32LittleEndian(body[11..]), BinaryPrimitives.ReadUInt32LittleEndian(body[15..]));
    }
}
