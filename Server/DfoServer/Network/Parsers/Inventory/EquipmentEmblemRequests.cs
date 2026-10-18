using System.Buffers.Binary;
using System.Collections.Immutable;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Network.Parsers.Inventory;

internal static class EquipmentEmblemRequests
{
    const int HeaderSize = 7;
    const int EntrySize = 7;

    public static EquipmentEmblemRequest Use(ReadOnlySpan<byte> body)
    {
        // 03958E80：C915依次写目标槽、目标模板、数量，再写1至2组徽章槽、模板及孔位。
        if (body.Length < HeaderSize)
            throw new InvalidDataException("普通装备徽章请求正文不足7字节");
        short targetSlot = BinaryPrimitives.ReadInt16LittleEndian(body);
        int targetItemId = BinaryPrimitives.ReadInt32LittleEndian(body[2..]);
        byte count = body[6];
        if (targetSlot < 0 || targetItemId <= 0 || count is < 1 or > 2 ||
            body.Length != HeaderSize + EntrySize * count)
            throw new InvalidDataException("普通装备徽章请求字段或宽度无效");

        var entries = ImmutableArray.CreateBuilder<EquipmentEmblemEntry>(count);
        int offset = HeaderSize;
        for (int index = 0; index < count; index++, offset += EntrySize)
        {
            short materialSlot = BinaryPrimitives.ReadInt16LittleEndian(body[offset..]);
            int materialItemId = BinaryPrimitives.ReadInt32LittleEndian(body[(offset + 2)..]);
            byte socketIndex = body[offset + 6];
            if (materialSlot < 0 || materialItemId <= 0 || socketIndex > 1)
                throw new InvalidDataException("普通装备徽章材料或孔位无效");
            entries.Add(new(materialSlot, materialItemId, socketIndex));
        }
        return new(targetSlot, targetItemId, entries.MoveToImmutable());
    }

    public static EquipmentSocketRequest Open(ReadOnlySpan<byte> body)
    {
        // 0278E2F0：C916固定写目标槽、目标模板和开孔材料槽。
        if (body.Length != 8)
            throw new InvalidDataException("普通装备开孔请求正文必须为8字节");
        short targetSlot = BinaryPrimitives.ReadInt16LittleEndian(body);
        int targetItemId = BinaryPrimitives.ReadInt32LittleEndian(body[2..]);
        short materialSlot = BinaryPrimitives.ReadInt16LittleEndian(body[6..]);
        if (targetSlot < 0 || targetItemId <= 0 || materialSlot < 0)
            throw new InvalidDataException("普通装备开孔请求字段无效");
        return new(targetSlot, targetItemId, materialSlot);
    }
}
