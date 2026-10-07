using DfoServer.Game.Inventory.Models;

namespace DfoServer.Network.Builders.Inventory;

internal static class RefinementPackets
{
    public static byte[] Ack(InventoryResult result, RefinementReceipt? receipt)
    {
        if (!result.Accepted)
            return CargoPackets.Ack(result);
        if (receipt is null)
            throw new InvalidOperationException("锻造成功提交缺少结果");
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)1);
        writer.Write(receipt.MaterialSlot);
        writer.Write(receipt.MaterialRemaining);
        writer.Write(receipt.OldLevel);
        // ACK成功表示尝试已提交；结果0为锻造成功、1为失败保级，不能反用该字段。
        writer.Write(receipt.Succeeded ? (byte)0 : (byte)1);
        writer.Write(receipt.NewLevel);
        writer.Write((byte)receipt.Position.List);
        writer.Write(receipt.Position.Index);
        return stream.ToArray();
    }
}
