using System.Buffers.Binary;

namespace DfoServer.Network.Parsers.Inventory;

internal static class CreatureSkinRequests
{
    public static (short SourceSlot, ushort Target) Read(ReadOnlySpan<byte> body)
    {
        if (body.Length != 4)
            throw new InvalidDataException("宠物幻化开栏请求必须包含两个16位字段");
        return (BinaryPrimitives.ReadInt16LittleEndian(body), BinaryPrimitives.ReadUInt16LittleEndian(body[2..]));
    }
}
