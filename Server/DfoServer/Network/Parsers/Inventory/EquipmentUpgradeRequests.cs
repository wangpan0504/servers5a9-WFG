using System.Buffers.Binary;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Network.Parsers.Inventory;

internal static class EquipmentUpgradeRequests
{
    public static EquipmentUpgradeRequest ReadUpgrade(ReadOnlySpan<byte> body)
    {
        if (body.Length != 15) throw new InvalidDataException("装备升级请求长度无效");
        short slot = BinaryPrimitives.ReadInt16LittleEndian(body);
        int item = BinaryPrimitives.ReadInt32LittleEndian(body[2..]);
        int target = BinaryPrimitives.ReadInt32LittleEndian(body[10..]);
        if (slot < 0 || item <= 0 || target <= 0 || body[6] > 1 || body[7] > 31 || body[8] > 8 ||
            body[9] > 1 || body[14] > 1)
            throw new InvalidDataException("装备升级请求字段无效");
        return new(slot, item, body[6] != 0, body[7], body[8], body[9] != 0, target, body[14] != 0);
    }

    public static EquipmentFusionRequest ReadFusion(ReadOnlySpan<byte> body)
    {
        if (body.Length != 12) throw new InvalidDataException("装备刻印或权能融合请求长度无效");
        short source = BinaryPrimitives.ReadInt16LittleEndian(body);
        int sourceId = BinaryPrimitives.ReadInt32LittleEndian(body[2..]);
        short target = BinaryPrimitives.ReadInt16LittleEndian(body[6..]);
        int targetId = BinaryPrimitives.ReadInt32LittleEndian(body[8..]);
        if (source < 0 || target < 0 || source == target || sourceId <= 0 || targetId <= 0)
            throw new InvalidDataException("装备刻印或权能融合请求字段无效");
        return new(source, sourceId, target, targetId);
    }

    public static DarkRevelationRequest ReadDarkRevelation(ReadOnlySpan<byte> body)
    {
        if (body.Length != 8) throw new InvalidDataException("黑暗诉求请求长度无效");
        short slot = BinaryPrimitives.ReadInt16LittleEndian(body);
        int item = BinaryPrimitives.ReadInt32LittleEndian(body[2..]);
        if (slot < 0 || item <= 0) throw new InvalidDataException("黑暗诉求请求字段无效");
        return new(slot, item, body[6], body[7]);
    }
}
