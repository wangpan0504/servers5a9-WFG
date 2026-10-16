using System.Buffers.Binary;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Network.Parsers.Inventory;

internal static class EquipmentQualityRequests
{
    public static EquipmentQualityRequest Read(ReadOnlySpan<byte> body, bool randomOptions)
    {
        if (body.Length != (randomOptions ? 6 : 10)) throw new InvalidDataException("品级调整请求长度无效");
        short target = BinaryPrimitives.ReadInt16LittleEndian(body);
        int id = randomOptions ? 0 : BinaryPrimitives.ReadInt32LittleEndian(body[2..]);
        short material = BinaryPrimitives.ReadInt16LittleEndian(body[(randomOptions ? 2 : 6)..]);
        ushort state = BinaryPrimitives.ReadUInt16LittleEndian(body[^2..]);
        if (target < 0 || material < 0 || target == material || !randomOptions && id <= 0)
            throw new InvalidDataException("品级调整槽位或操作类型无效");
        // 末尾保留原生窗口状态；业务按实际箱子的PVF策略决定权限，不信任此字段选择效果。
        return new(target, id, material, state, randomOptions);
    }
}
