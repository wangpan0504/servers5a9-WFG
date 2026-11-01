using System.Buffers.Binary;
using DfoServer.GameWorld.Channels;

namespace DfoServer.Network.Builders;

internal static class RaidCharacterPackets
{
    // 02682740依次调用2681110/267D360；两段正文与既有Actor/公开属性同形。
    public static byte[] Information(ChannelIdentity channel, ushort character, ReadOnlySpan<byte> actor, ReadOnlySpan<byte> player)
    {
        if (actor.Length < 123 || player.Length < 16 || actor[0] != 0 || player[0] != 3 ||
            BinaryPrimitives.ReadUInt16LittleEndian(actor[1..]) != 1 || BinaryPrimitives.ReadUInt16LittleEndian(player[1..]) != 1 ||
            actor[3] != channel.ServerIndex || actor[4] != channel.Number || player[3] != channel.ServerIndex || player[4] != channel.Number ||
            BinaryPrimitives.ReadUInt16LittleEndian(actor[121..]) != character || BinaryPrimitives.ReadUInt16LittleEndian(player[14..]) != character)
            throw new InvalidDataException("跨频道详情必须来自同一目标的单条Actor和公开属性");
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(channel.Number); writer.Write(character);
        // 去掉NOTI2各自的subtype/u16 count，保留两个server/channel定位头。
        writer.Write(actor[3..]); writer.Write(player[3..]);
        return stream.ToArray();
    }
}
