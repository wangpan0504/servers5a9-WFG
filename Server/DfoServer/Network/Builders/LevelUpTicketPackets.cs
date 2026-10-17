using System.Buffers.Binary;
using DfoServer.Game.Characters;

namespace DfoServer.Network.Builders;

internal static class LevelUpTicketPackets
{
    // ARAD 0235BFF0：ACK先消费源并增加经验/SP/TP；绝对成长通知必须在其后。
    public static byte[] Used(LevelUpTicketSource source)
    {
        byte[] body = new byte[12];
        body[0] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(1), checked((ushort)source.Item.Position.Index));
        body[3] = (byte)source.Item.Position.List;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), checked((uint)source.Growth.GainedExperience));
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(8), checked((ushort)source.Growth.LevelSpDelta));
        int tp = source.Growth.Learning.Trees.Single(tree=>tree.Context==0).Tp -
            source.Growth.Before.Trees.Single(tree=>tree.Context==0).Tp;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(10), checked((ushort)tp));
        return body;
    }

    public static byte[] Rejected() => [0,99,0];
}
