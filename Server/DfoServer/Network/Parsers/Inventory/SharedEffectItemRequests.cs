using System.Buffers.Binary;

namespace DfoServer.Network.Parsers.Inventory;

internal readonly record struct SharedEffectItemRequest(int ItemId, ushort Slot);
internal static class SharedEffectItemRequests
{
    public static SharedEffectItemRequest Read(ReadOnlySpan<byte> body)
    {
        if (body.Length != 6) throw new InvalidDataException("共享特效物品请求必须为6字节");
        return new(BinaryPrimitives.ReadInt32LittleEndian(body), BinaryPrimitives.ReadUInt16LittleEndian(body[4..]));
    }
}
