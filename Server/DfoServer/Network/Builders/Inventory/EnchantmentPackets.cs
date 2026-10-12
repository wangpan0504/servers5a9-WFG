using DfoServer.Game.Inventory.Models;

namespace DfoServer.Network.Builders.Inventory;

internal static class EnchantmentPackets
{
    public static byte[] Ack(InventoryResult result, EnchantmentRequest request)
    {
        if (!result.Accepted)
            return CargoPackets.Ack(result);
        using var stream = new MemoryStream(4);
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)1);
        // 0234CF6D/0234CF76只定位目标实例；源宝珠扣除由提交后的库存差量同步。
        writer.Write((byte)request.Target.List);
        writer.Write(checked((ushort)request.Target.Index));
        return stream.ToArray();
    }
}
