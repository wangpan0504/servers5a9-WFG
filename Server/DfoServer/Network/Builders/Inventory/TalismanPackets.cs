namespace DfoServer.Network.Builders.Inventory;

internal static class TalismanPackets
{
    public static byte[] Equipped(ushort target, int itemId) => Build(writer =>
    {
        writer.Write((byte)1);
        writer.Write(target);
        writer.Write(itemId);
    });

    public static byte[] Removed(ushort target) => Build(writer =>
    {
        writer.Write((byte)1);
        writer.Write(target);
    });

    public static byte[] Refined(int itemId, uint target) => Build(writer =>
    {
        writer.Write((byte)1);
        writer.Write(itemId);
        writer.Write(target);
        writer.Write(0u);
    });

    public static byte[] Compounded(ushort target, int itemId, bool upgraded) => Build(writer =>
    {
        writer.Write((byte)1);
        writer.Write(target);
        writer.Write(itemId);
        writer.Write(upgraded ? (byte)1 : (byte)0);
    });

    public static byte[] Failure(ushort error) => Build(writer =>
    {
        writer.Write((byte)0);
        writer.Write(error);
    });

    static byte[] Build(Action<BinaryWriter> write)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        write(writer);
        return stream.ToArray();
    }
}
