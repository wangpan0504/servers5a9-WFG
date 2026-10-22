using DfoServer.Game.Currency;

namespace DfoServer.Network.Builders.Inventory;

internal static class MileagePackets
{
    // ARAD 023A6440读u8+i32+i32；第二计数不是礼盒余额，本轮未接入其来源，保持0。
    public static byte[] State(AccountWallet wallet)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)0);
        writer.Write(wallet.Mileage);
        writer.Write(0);
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
        writer.Write(0);
        return stream.ToArray();
    }
}
