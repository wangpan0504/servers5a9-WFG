using DfoServer.Game.Inventory.Models;

namespace DfoServer.Network.Builders.Inventory;

internal static class ChronicleEnchantPackets
{
    public static byte[] Ack(InventoryResult result, ChronicleEnchantReceipt? receipt)
    {
        if (!result.Accepted) return CargoPackets.Ack(result);
        if (receipt is null) throw new InvalidOperationException("气息成功缺少实际提交结果");
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)1);
        writer.Write(receipt.MaterialSlot);
        writer.Write(receipt.MaterialRemaining);
        writer.Write(receipt.Succeeded ? (byte)1 : (byte)0);
        writer.Write((byte)InventoryListType.Main);
        writer.Write(receipt.TargetSlot);
        writer.Write(checked((byte)receipt.Refunds.Length));
        foreach (var refund in receipt.Refunds)
        { writer.Write(refund.Slot); writer.Write(refund.ItemId); writer.Write(refund.Count); }
        return stream.ToArray();
    }
}
