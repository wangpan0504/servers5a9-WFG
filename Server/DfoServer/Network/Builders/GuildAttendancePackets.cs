using DfoServer.Game.Guild;

namespace DfoServer.Network.Builders;

internal static class GuildAttendancePackets
{
    public static byte[] Names(GuildAttendanceView view)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)1);
        writer.Write(checked((ushort)view.Members.Length));
        foreach (var member in view.Members) PacketEncoding.WriteString(writer, member.Name);
        if (stream.Length > 65_520) throw new InvalidDataException("Guild attendance list exceeds native frame limit");
        return stream.ToArray();
    }
    public static byte[] Failure(ushort error) => [0, (byte)error, (byte)(error >> 8)];
}
