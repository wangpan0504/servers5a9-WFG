namespace DfoServer.Network.Builders;

internal static class GuildAppearancePackets
{
    public static byte[] MarkResult(ushort error) => error == 0 ? [1] : [0, (byte)error, (byte)(error >> 8)];
}
