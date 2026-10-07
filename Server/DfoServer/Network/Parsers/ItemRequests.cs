using System.Buffers.Binary;

namespace DfoServer.Network.Parsers;

internal sealed record ItemMoveEndpoint(byte Space, ushort Slot, uint Value, uint Reserved);
internal sealed record MoveItemRequest(ItemMoveEndpoint First, ItemMoveEndpoint Second, int ContextIndex, byte Tail0, byte Tail1);
internal sealed record BuyItemRequest(uint DefinitionValue, uint QuantityValue, uint Context0, uint Context1);
internal sealed record SellItemRequest(byte Space, ushort Slot, uint Quantity, uint Context0, uint WindowState, uint Check);

// 只解析已闭合出口的固定读宽。未知字段原样保留，状态、owner 和业务资格由后续领域处理。
internal static class ItemRequests
{
    public static MoveItemRequest ParseMove(ReadOnlySpan<byte> body)
    {
        RequireWidth(body, 28, "MOVE_ITEMSPACE");
        return new MoveItemRequest(
            Endpoint(body, 0),
            Endpoint(body, 11),
            BinaryPrimitives.ReadInt32LittleEndian(body[22..]),
            body[26],
            body[27]);
    }

    public static BuyItemRequest ParseBuy(ReadOnlySpan<byte> body)
    {
        RequireWidth(body, 16, "BUY_ITEM 默认出口");
        return new BuyItemRequest(UInt32(body, 0), UInt32(body, 4), UInt32(body, 8), UInt32(body, 12));
    }

    public static SellItemRequest ParseSell(ReadOnlySpan<byte> body)
    {
        RequireWidth(body, 19, "SELL_ITEM");
        return new SellItemRequest(body[0], UInt16(body, 1), UInt32(body, 3), UInt32(body, 7), UInt32(body, 11), UInt32(body, 15));
    }

    private static ItemMoveEndpoint Endpoint(ReadOnlySpan<byte> body, int offset) => new(
        body[offset], UInt16(body, offset + 1), UInt32(body, offset + 3), UInt32(body, offset + 7));

    private static ushort UInt16(ReadOnlySpan<byte> body, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(body[offset..]);
    private static uint UInt32(ReadOnlySpan<byte> body, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(body[offset..]);

    private static void RequireWidth(ReadOnlySpan<byte> body, int width, string command)
    {
        if (body.Length != width)
            throw new InvalidDataException(command + " 请求正文长度不匹配");
    }
}
