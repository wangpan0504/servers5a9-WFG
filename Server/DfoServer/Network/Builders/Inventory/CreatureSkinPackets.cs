using System.Buffers.Binary;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Network.Builders.Inventory;

internal static class CreatureSkinPackets
{
    public static byte[] Open(ushort target, InventoryResult result)
    {
        // 023628E0的两条分支都读取target；失败不能省略公共error之后的两字节。
        var body = new byte[result.Accepted ? 3 : 5];
        body[0] = result.Accepted ? (byte)1 : (byte)0;
        if (!result.Accepted)
            BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(1), result.Error);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(body.Length - 2), target);
        return body;
    }
}
