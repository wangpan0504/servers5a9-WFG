using System.Buffers.Binary;

namespace DfoServer.Network.Parsers.Inventory;

internal static class EpicBookRequests
{
    public static int Craft(ReadOnlySpan<byte> body)
    {
        if (body.Length != 4)
            throw new InvalidDataException("Client史诗制作请求必须是4字节装备编号");
        return BinaryPrimitives.ReadInt32LittleEndian(body);
    }
}
