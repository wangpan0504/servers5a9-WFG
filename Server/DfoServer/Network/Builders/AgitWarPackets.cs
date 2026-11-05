namespace DfoServer.Network.Builders;

internal static class AgitWarPackets
{
    public static byte[] TownInformation() => [1];

    public static byte[] Information(uint mode, uint guildId)
    {
        if (mode == 2) return TownInformation();
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)1);
        writer.Write(mode);
        writer.Write(guildId);
        writer.Write((byte)0);
        writer.Write(0u);
        return stream.ToArray();
    }
}
