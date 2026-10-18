using System.Buffers.Binary;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Network.Parsers.Inventory;

internal static class LimitCubeRequests
{
    public const int BodySize = 8;

    public static LimitCubeRequest Read(ReadOnlySpan<byte> body)
    {
        if (body.Length != BodySize)
            throw new InvalidDataException("变更券请求正文必须为8字节");
        ushort targetSlot = BinaryPrimitives.ReadUInt16LittleEndian(body);
        uint targetItemId = BinaryPrimitives.ReadUInt32LittleEndian(body[2..]);
        ushort ticketSlot = BinaryPrimitives.ReadUInt16LittleEndian(body[6..]);
        if (targetSlot > short.MaxValue || ticketSlot > short.MaxValue ||
            targetItemId is 0 or > int.MaxValue || targetSlot == ticketSlot)
            throw new InvalidDataException("变更券请求字段无效");
        return new((short)targetSlot, (int)targetItemId, (short)ticketSlot);
    }
}
