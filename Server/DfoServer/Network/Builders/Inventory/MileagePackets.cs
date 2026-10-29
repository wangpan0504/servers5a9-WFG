using DfoServer.Game.Currency;

namespace DfoServer.Network.Builders.Inventory;

internal static class MileagePackets
{
    // 第二计数由原生购买预览与limit mileage by cash比较，独立于可消费积分余额。
    public static byte[] State(AccountWallet wallet)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)0);
        writer.Write(wallet.Mileage);
        writer.Write(wallet.MileageShop.CashEarned);
        return stream.ToArray();
    }

    // 023B1890：来源、变更前、变更后和第二计数；来源0走35331普通积分获得提示。
    public static byte[] Added(AccountWallet before, AccountWallet after)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)0);
        writer.Write(before.Mileage);
        writer.Write(after.Mileage);
        writer.Write(after.MileageShop.CashEarned);
        return stream.ToArray();
    }

    // 0239E6D0：u8重置类别、u8条数，然后逐条读取商品号和已购份数两个i32。
    public static byte[] Limits(AccountWallet wallet, DfoServer.GameWorld.Items.MileageShopCatalog catalog)
    {
        var rows = catalog.Limits.Values.Where(row => row.Supported).OrderBy(row => row.CommodityId).ToArray();
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)0);
        writer.Write(checked((byte)rows.Length));
        foreach (var row in rows)
        {
            writer.Write(row.CommodityId);
            writer.Write(wallet.MileageShop.Purchases.GetValueOrDefault(row.CommodityId));
        }
        return stream.ToArray();
    }
}
