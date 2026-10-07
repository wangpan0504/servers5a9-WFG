namespace DfoServer.Network.Builders;

internal static class SessionPackets
{
    public static byte[] Exit() => [1];
    public static byte[] ReturnToSelection() => [1];
}
