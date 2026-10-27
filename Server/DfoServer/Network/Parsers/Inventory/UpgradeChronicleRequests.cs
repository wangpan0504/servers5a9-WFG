using DfoServer.Game.Inventory.Models;
using System.Buffers.Binary;

namespace DfoServer.Network.Parsers.Inventory;

internal sealed record UpgradeChronicleRequest(InventorySlot Blueprint, int BlueprintItemId,
    InventorySlot Target, int TargetItemId, InventorySlot? Material = null, int MaterialItemId = 0);

// CMD271：19字节。末六字节为费用材料首行的槽位u16和IDu32；空表写FFFF/0000FFFF。
// 本版客户端 0220DCE8 / 037F4E7C：分档来自装备实例，不是 ticket 类型；不信任该值决定产物或属性。
internal static class UpgradeChronicleRequests
{
    public const int BodyLength = 19;

    public static UpgradeChronicleRequest Upgrade(ReadOnlySpan<byte> body)
    {
        if (body.Length != BodyLength || body[12] > 4)
            throw new InvalidDataException("Client设计图升级请求不是已知的19字节形态");
        ushort blueprintSlot = BinaryPrimitives.ReadUInt16LittleEndian(body);
        int blueprintItemId = BinaryPrimitives.ReadInt32LittleEndian(body[2..]);
        ushort targetSlot = BinaryPrimitives.ReadUInt16LittleEndian(body[6..]);
        int targetItemId = BinaryPrimitives.ReadInt32LittleEndian(body[8..]);
        ushort materialSlot = BinaryPrimitives.ReadUInt16LittleEndian(body[13..]);
        int materialId = BinaryPrimitives.ReadInt32LittleEndian(body[15..]);
        bool empty = materialSlot == ushort.MaxValue && materialId == ushort.MaxValue;
        if (blueprintSlot > short.MaxValue || targetSlot > short.MaxValue || blueprintItemId <= 0 || targetItemId <= 0)
            throw new InvalidDataException("设计图升级槽号或模板无效");
        if (!empty && (materialSlot > short.MaxValue || materialId <= 0))
            throw new InvalidDataException("设计图费用材料槽号或模板无效");
        return new(new(InventoryListType.Main, (short)blueprintSlot), blueprintItemId,
            new(InventoryListType.Main, (short)targetSlot), targetItemId,
            empty ? null : new(InventoryListType.Main, (short)materialSlot), empty ? 0 : materialId);
    }
}
