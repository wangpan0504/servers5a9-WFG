using System.Buffers.Binary;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Network.Parsers;

internal static class LevelUpTicketRequests
{
    // ARAD 037867A0：只写u16槽号和u8容器，不沿用515动作正文。
    public static bool TryRead(ReadOnlySpan<byte> body, out InventorySlot position)
    {
        position = default;
        if (body.Length != 3) return false;
        position = new((InventoryListType)body[2], BinaryPrimitives.ReadInt16LittleEndian(body));
        return true;
    }
}
