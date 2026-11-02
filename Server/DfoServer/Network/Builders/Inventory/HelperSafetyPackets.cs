using System.Globalization;
using DfoServer.Game.Inventory.Services;

namespace DfoServer.Network.Builders.Inventory;

internal static class HelperSafetyPackets
{
    internal static string EntryWithWearability(HelperSafetyPolicy.Candidate row) => Entry(row) + (row.WearableEpic ? ":1" : ":0");

    // 与客户端已完整收到的123B普通物品记录共用指纹，槽位复用或属性变化不再匹配旧资格。
    internal static string Entry(HelperSafetyPolicy.Candidate row)
    {
        using var stream = new MemoryStream(ItemListProtocolWriter.EntrySize);
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
            ItemListProtocolWriter.Write(writer, row.Item.Position.Index, row.Item.Core);
        ulong hash = 14695981039346656037UL;
        foreach (byte value in stream.GetBuffer().AsSpan(0, checked((int)stream.Length)))
            hash = unchecked((hash ^ value) * 1099511628211UL);
        if (hash == 0) hash = 1;
        return string.Join(':', row.Item.Position.Index.ToString(CultureInfo.InvariantCulture),
            hash.ToString("x16", CultureInfo.InvariantCulture), row.Operations.ToString(CultureInfo.InvariantCulture));
    }
}
