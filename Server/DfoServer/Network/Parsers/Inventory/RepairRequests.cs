using System.Buffers.Binary;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Network.Parsers.Inventory;

internal static class RepairRequests
{
    public static RepairEquipmentRequest Read(ReadOnlySpan<byte> body)
    {
        if (body.Length is not (7 or 8) || body.Length == 8 && body[7] > 1)
            throw new InvalidDataException("Client修理请求宽度或快速标志无效");
        return new(new((InventoryListType)body[0], BinaryPrimitives.ReadInt16LittleEndian(body[1..])),
            BinaryPrimitives.ReadInt16LittleEndian(body[3..]), BinaryPrimitives.ReadUInt16LittleEndian(body[5..]), body.Length == 8 && body[7] != 0);
    }
}
