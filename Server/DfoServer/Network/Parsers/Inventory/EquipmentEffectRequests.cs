using System.Buffers.Binary;

namespace DfoServer.Network.Parsers.Inventory;

internal readonly record struct EquipmentEffectRequest(short SourceSlot, short TargetSlot);

internal static class EquipmentEffectRequests
{
    public static EquipmentEffectRequest Read(ReadOnlySpan<byte> body)
    {
        // 029CBA71/74：21B正文的前13B是发送器未初始化的栈内容，不能作为可信身份。
        if (body.Length != 21) throw new InvalidDataException("装备特效请求长度无效");
        int target = BinaryPrimitives.ReadInt32LittleEndian(body[13..]);
        int source = BinaryPrimitives.ReadInt32LittleEndian(body[17..]);
        if (source is < 0 or > short.MaxValue || target is < 0 or > short.MaxValue || source == target)
            throw new InvalidDataException("装备特效槽位无效");
        return new((short)source, (short)target);
    }
}
