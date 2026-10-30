using System.Buffers.Binary;
using System.Collections.Immutable;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Network.Parsers.Inventory;

internal static class AvatarEmblemRequests
{
    public static AvatarEmblemRequest Read(ReadOnlySpan<byte> body)
    {
        // 039591B9：装扮请求在槽位之前另写容器，随后每项固定7字节，孔号为0至4。
        if (body.Length < 8)
            throw new InvalidDataException("装扮徽章请求正文不足8字节");
        var list = (InventoryListType)body[0];
        short slot = BinaryPrimitives.ReadInt16LittleEndian(body[1..]);
        int itemId = BinaryPrimitives.ReadInt32LittleEndian(body[3..]);
        byte count = body[7];
        if (list is not (InventoryListType.Avatar or InventoryListType.Equipment) || slot < 0 || itemId <= 0 ||
            count is < 1 or > 5 || body.Length != 8 + count * 7)
            throw new InvalidDataException("装扮徽章目标、数量或宽度无效");
        var entries = ImmutableArray.CreateBuilder<AvatarEmblemEntry>(count);
        for (int index = 0, offset = 8; index < count; index++, offset += 7)
        {
            short materialSlot = BinaryPrimitives.ReadInt16LittleEndian(body[offset..]);
            int materialId = BinaryPrimitives.ReadInt32LittleEndian(body[(offset + 2)..]);
            byte socket = body[offset + 6];
            if (materialSlot < 0 || materialId <= 0 || socket > 4)
                throw new InvalidDataException("装扮徽章材料或孔号无效");
            entries.Add(new(materialSlot, materialId, socket));
        }
        return new(new(list, slot), itemId, entries.MoveToImmutable());
    }
}
