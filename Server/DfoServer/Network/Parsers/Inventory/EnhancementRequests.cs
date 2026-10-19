using System.Buffers.Binary;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;

namespace DfoServer.Network.Parsers.Inventory;

internal static class EnhancementRequests
{
    public static EnhancementRequest Read(ReadOnlySpan<byte> body)
    {
        if (body.Length < 20)
            throw new InvalidDataException("强化/增幅请求缺少完整名称及末尾槽字段");
        int count = BinaryPrimitives.ReadInt32LittleEndian(body[12..]);
        ushort target = BinaryPrimitives.ReadUInt16LittleEndian(body[2..]);
        int item = BinaryPrimitives.ReadInt32LittleEndian(body[4..]);
        if (body[0] is not (0 or 1 or 5) || target > short.MaxValue || item <= 0 || count < 0 || count > 256 || body.Length != 20 + count)
            throw new InvalidDataException("强化/增幅请求模式、槽位或名称宽度无效");
        return new((EnhancementMode)body[0], new((InventoryListType)body[1], (short)target), item,
            BinaryPrimitives.ReadInt16LittleEndian(body[8..]), BinaryPrimitives.ReadInt16LittleEndian(body[10..]),
            body.Slice(16, count).ToArray(), BinaryPrimitives.ReadInt16LittleEndian(body[(16 + count)..]),
            BinaryPrimitives.ReadInt16LittleEndian(body[(18 + count)..]));
    }
}
