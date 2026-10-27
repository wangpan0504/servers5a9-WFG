namespace DfoServer.Network.Builders.Inventory;

internal static class ServerItemUsagePackets
{
    // 023A4EE0依次读三个DWORD；该消费者对旧袖珍罐显示max-used-1。
    public static byte[] Changed(int itemId, int maximum, int used)
    {
        using var stream = new MemoryStream(12); using var writer = new BinaryWriter(stream);
        writer.Write(itemId); writer.Write(maximum); writer.Write(Math.Max(0, used - 1));
        return stream.ToArray();
    }
}
