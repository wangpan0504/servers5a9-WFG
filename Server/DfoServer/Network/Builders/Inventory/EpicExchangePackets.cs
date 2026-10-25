using DfoServer.Game.Inventory.Models;

namespace DfoServer.Network.Builders.Inventory;

internal static class EpicExchangePackets
{
    // S2C 0x621：success:u8，失败再补 code:u16（客户端注册点 0x013E2198）。
    public static byte[] Exchanged(InventoryResult result)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(result.Accepted ? (byte)1 : (byte)0);
        if (!result.Accepted)
            writer.Write(result.Error);
        return stream.ToArray();
    }
}
