using System.Buffers.Binary;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Network.Parsers.Inventory;

internal static class MagicBoxRequests
{
    public static MagicBoxOpenRequest Single(ReadOnlySpan<byte> body)
    {
        if (body.Length is not (3 or 5) || !ValidList(body[0]))
            throw new InvalidDataException("单次开箱请求长度或容器无效");
        short material = body.Length == 5 ? BinaryPrimitives.ReadInt16LittleEndian(body[3..]) : (short)-1;
        return new(body[0], MainSlot(body[1..]), 0, new(InventoryListType.Main, material), 0, 1);
    }

    public static MagicBoxOpenRequest Expand(ReadOnlySpan<byte> body)
    {
        if (body.Length != 15 || !ValidList(body[0]))
            throw new InvalidDataException("批量开箱请求长度或容器无效");
        int count = BinaryPrimitives.ReadUInt16LittleEndian(body[13..]);
        int boxItem = BinaryPrimitives.ReadInt32LittleEndian(body[3..]);
        short materialSlot = BinaryPrimitives.ReadInt16LittleEndian(body[7..]);
        int materialItem = materialSlot < 0 ? 0 : BinaryPrimitives.ReadInt32LittleEndian(body[9..]);
        if (count <= 0 || boxItem <= 0 || materialSlot >= 0 && materialItem <= 0)
            throw new InvalidDataException("批量开箱请求物品或数量无效");
        return new(body[0], MainSlot(body[1..]), boxItem,
            new(InventoryListType.Main, materialSlot), materialItem, count);
    }

    static InventorySlot MainSlot(ReadOnlySpan<byte> bytes) => new(InventoryListType.Main, BinaryPrimitives.ReadInt16LittleEndian(bytes));
    static bool ValidList(byte value) => value is 0 or 4;
}
