using DfoServer.Game.Inventory.Models;

namespace DfoServer.Network.Builders.Inventory;

internal static class EpicPieceShopPackets
{
    public static byte[] Result(InventoryResult result) => result.Accepted ? [1] : [0, (byte)result.Error, (byte)(result.Error >> 8)];
    public static byte[] Materials(IReadOnlyList<int> ids, int count)
    {
        if (ids.Count is < 1 or > 5 || count <= 0 || ids.Any(id => id <= 0) || ids.Distinct().Count() != ids.Count)
            throw new InvalidDataException("NOTI748必须是最多五种不同碎片及正数量");
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)ids.Count);
        foreach (int id in ids) { writer.Write(id); writer.Write(count); }
        return stream.ToArray();
    }
}
