using System.Buffers.Binary;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Network.Builders.Inventory;

internal static class EmblemCompoundPackets
{
    public static byte[] Success(EmblemCompoundReceipt receipt)
    {
        if (receipt.RewardItemId <= 0 || receipt.RewardCount <= 0)
            throw new InvalidDataException("徽章合成成功回执奖励无效");
        byte[] body = new byte[10];
        body[0] = 1;
        body[1] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(2), checked((uint)receipt.RewardItemId));
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(6), checked((uint)receipt.RewardCount));
        return body;
    }

    public static byte[] Failure(ushort error = 22)
    {
        if (error == 0)
            throw new InvalidDataException("徽章合成失败码不能为零");
        byte[] body = new byte[3];
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(1), error);
        return body;
    }
}
