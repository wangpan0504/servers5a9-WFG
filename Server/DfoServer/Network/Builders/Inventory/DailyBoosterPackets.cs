namespace DfoServer.Network.Builders.Inventory;

internal static class DailyBoosterPackets
{
    // 三组是连续的独立列表；本服目前只有第一组真源，value 为已用次数。
    public static byte[] Used(IReadOnlyList<(int ItemId, byte Used)> rows)
    {
        if (rows.Count > byte.MaxValue) throw new ArgumentOutOfRangeException(nameof(rows));
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)rows.Count);
        foreach (var row in rows)
        {
            writer.Write(row.ItemId);
            writer.Write(row.Used);
        }
        writer.Write((byte)0);
        writer.Write((byte)0);
        return stream.ToArray();
    }
}
