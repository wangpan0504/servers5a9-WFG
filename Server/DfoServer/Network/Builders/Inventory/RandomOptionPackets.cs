namespace DfoServer.Network.Builders.Inventory;

internal static class RandomOptionPackets
{
    public static byte[] Status(bool accepted) => [accepted ? (byte)1 : (byte)0];
}
