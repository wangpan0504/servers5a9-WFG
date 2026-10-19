using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;

namespace DfoServer.Network.Builders.Inventory;

internal static class AmplificationPackets
{
    public static byte[] Ack(InventoryResult result, AmplificationReceipt? receipt)
    {
        if (!result.Accepted) return CargoPackets.Ack(result);
        if (receipt is null) throw new InvalidOperationException("异次元属性成功缺少实际提交结果");
        bool invest = receipt.Operation <= AmplificationOperation.GoldInvest;
        using var stream = new MemoryStream(14);
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)1);
        if (invest) writer.Write((byte)receipt.Operation);
        writer.Write(receipt.MaterialSlot);
        writer.Write(receipt.MaterialRemaining);
        writer.Write(receipt.TargetSlot);
        writer.Write(receipt.Type);
        writer.Write(receipt.Value);
        if (receipt.Operation == AmplificationOperation.GoldInvest) writer.Write(receipt.Level);
        return stream.ToArray();
    }
}
