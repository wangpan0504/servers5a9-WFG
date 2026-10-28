using System.Buffers.Binary;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Network.Parsers.Inventory;

internal static class EquipmentTransmissionRequests
{
    public static EquipmentTransmissionRequest Read(ReadOnlySpan<byte> body)
    {
        // 本版实收包含13字节公共前缀；前缀不是容器、角色或实例授权。
        if (body.Length != 25) throw new InvalidDataException("装备传承请求长度无效");
        short target = BinaryPrimitives.ReadInt16LittleEndian(body[13..]);
        int targetId = BinaryPrimitives.ReadInt32LittleEndian(body[15..]);
        short donor = BinaryPrimitives.ReadInt16LittleEndian(body[19..]);
        int donorId = BinaryPrimitives.ReadInt32LittleEndian(body[21..]);
        if (donor < 0 || target < 0 || donor == target || donorId <= 0 || targetId <= 0)
            throw new InvalidDataException("装备传承请求字段无效");
        return new(target, targetId, donor, donorId);
    }
}
