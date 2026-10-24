using System.Buffers.Binary;

namespace DfoServer.Network.Parsers.Inventory;

internal static class InventoryExpansionRequests
{
    public static ushort Read(ReadOnlySpan<byte> body)
    {
        // 02870F10仅写u16源槽；没有目标档位、数量或客户端nonce。
        if (body.Length != 2)
            throw new InvalidDataException("Client背包扩展请求宽度");
        return BinaryPrimitives.ReadUInt16LittleEndian(body);
    }
}
