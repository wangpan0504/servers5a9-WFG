using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;

namespace DfoServer.Network.Builders.Inventory;

internal static class EpicBookPackets
{
    public static byte[] Info(EpicPieceBook book, EpicPieceCatalog catalog, IReadOnlyList<int>? changed = null)
    {
        book.Validate(catalog);
        var indexes = changed ?? catalog.Entries.Where(entry => book.Get(entry.Index) != 0).Select(entry => entry.Index).ToArray();
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(changed is null ? (byte)0 : (byte)1);
        writer.Write(indexes.Count);
        foreach (int index in indexes)
        {
            writer.Write(catalog.Entries[index].PieceId);
            writer.Write(book.Get(index)); // 0239E7C4→017E3590覆盖余额，差量包也必须发送总量，包括耗尽后的0。
        }
        return stream.ToArray();
    }

    public static byte[] Crafted(InventoryResult result)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(result.Accepted ? (byte)1 : (byte)0);
        if (!result.Accepted)
            writer.Write(result.Error);
        return stream.ToArray();
    }
}
