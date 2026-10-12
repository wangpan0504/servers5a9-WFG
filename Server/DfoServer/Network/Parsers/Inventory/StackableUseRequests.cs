using System.Buffers.Binary;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Network.Parsers.Inventory;

internal sealed record StackableUseRequest(InventorySlot Position, uint Token, int ItemId);
internal static class StackableUseRequests
{
    public static StackableUseRequest? Parse(ReadOnlySpan<byte> body)
    {
        if (body.Length is not (10 or 15)) return null;
        byte list = body.Length == 15 ? body[2] : (byte)0;
        if (list != 0 || body.Length == 15 && BinaryPrimitives.ReadUInt32LittleEndian(body[11..]) != 0) return null;
        int at = body.Length == 15 ? 3 : 2;
        uint item = BinaryPrimitives.ReadUInt32LittleEndian(body[(at+4)..]);
        ushort slot = BinaryPrimitives.ReadUInt16LittleEndian(body);
        if (item is 0 or > int.MaxValue || slot > 351) return null;
        return new(new(InventoryListType.Main, checked((short)slot)), BinaryPrimitives.ReadUInt32LittleEndian(body[at..]), (int)item);
    }
}
