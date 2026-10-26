using System.Buffers.Binary;

namespace DfoServer.Network.Builders.Inventory;

internal static class AvatarSocketPackets
{
    public static byte[] Success(short targetSlot, short materialSlot)
    {
        // 02356890仅消费两个u16，不能在中间插入目标模板编号。
        byte[] body = [1, 0, 0, 0, 0];
        BinaryPrimitives.WriteInt16LittleEndian(body.AsSpan(1), targetSlot);
        BinaryPrimitives.WriteInt16LittleEndian(body.AsSpan(3), materialSlot);
        return body;
    }

    public static byte[] Failure() => [0, 4, 0];
}
