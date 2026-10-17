using System.Buffers.Binary;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Network.Parsers;

internal static class ExperienceConsumableRequests
{
    public static InventorySlot Read(ReadOnlySpan<byte> body)
    {
        if (body.Length != 2) throw new InvalidDataException("CMD30正文必须为一个背包槽位");
        return new(InventoryListType.Main, BinaryPrimitives.ReadInt16LittleEndian(body));
    }
}
