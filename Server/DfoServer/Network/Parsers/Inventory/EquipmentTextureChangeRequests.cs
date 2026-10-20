using System.Buffers.Binary;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Network.Parsers.Inventory;

internal static class EquipmentTextureChangeRequests
{
    public static EquipmentTextureChangeRequest Read(ReadOnlySpan<byte> body)
    {
        if (body.Length != 3) throw new InvalidDataException("材质转换请求长度无效");
        short slot = BinaryPrimitives.ReadInt16LittleEndian(body);
        byte material = body[2];
        // 目标材质只有 5 种；越界字节不是本版客户端会产生的输入，按畸形请求拒绝。
        if (slot < 0 || material > 4) throw new InvalidDataException("材质转换槽位或目标材质无效");
        return new(slot, material);
    }
}
