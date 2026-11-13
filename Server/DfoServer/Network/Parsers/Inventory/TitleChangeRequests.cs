using System.Buffers.Binary;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Network.Parsers.Inventory;

internal static class TitleChangeRequests
{
    public static TitleChangeRequest Read(ReadOnlySpan<byte> body)
    {
        if (body.Length != 4)
            throw new InvalidDataException("称号变更请求长度无效");
        short threadSlot = BinaryPrimitives.ReadInt16LittleEndian(body[..2]);
        short titleSlot = BinaryPrimitives.ReadInt16LittleEndian(body[2..4]);
        if (threadSlot < 0 || titleSlot < 0 || threadSlot == titleSlot)
            throw new InvalidDataException("称号变更槽位无效");
        return new(threadSlot, titleSlot);
    }
}
