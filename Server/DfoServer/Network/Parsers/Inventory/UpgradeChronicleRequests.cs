using DfoServer.Game.Inventory.Models;
using System.Buffers.Binary;

namespace DfoServer.Network.Parsers.Inventory;

internal sealed record UpgradeChronicleRequest(InventorySlot Blueprint, int BlueprintItemId,
    InventorySlot Target, int TargetItemId);

// CMD271。实测正文 19 字节：[u16 设计图槽位][u32 设计图 id][u16 装备槽位][u32 装备 id][u8 装备 seed 分档 0..4][i32 -1][u16 0]。
// 本版客户端 0220DCE8 / 037F4E7C：分档来自装备实例，不是 ticket 类型；不信任该值决定产物或属性。
internal static class UpgradeChronicleRequests
{
    public const int BodyLength = 19;

    public static UpgradeChronicleRequest Upgrade(ReadOnlySpan<byte> body)
    {
        if (body.Length != BodyLength || body[12] > 4 || BinaryPrimitives.ReadInt32LittleEndian(body[13..]) != -1 ||
            BinaryPrimitives.ReadUInt16LittleEndian(body[17..]) != 0)
            throw new InvalidDataException("Client设计图升级请求不是已知的19字节形态");
        ushort blueprintSlot = BinaryPrimitives.ReadUInt16LittleEndian(body);
        int blueprintItemId = BinaryPrimitives.ReadInt32LittleEndian(body[2..]);
        ushort targetSlot = BinaryPrimitives.ReadUInt16LittleEndian(body[6..]);
        int targetItemId = BinaryPrimitives.ReadInt32LittleEndian(body[8..]);
        if (blueprintSlot > short.MaxValue || targetSlot > short.MaxValue || blueprintItemId <= 0 || targetItemId <= 0)
            throw new InvalidDataException("设计图升级槽号或模板无效");
        return new(new(InventoryListType.Main, (short)blueprintSlot), blueprintItemId,
            new(InventoryListType.Main, (short)targetSlot), targetItemId);
    }
}
