using DfoServer.Game.Inventory.Models;

namespace DfoServer.Network.Builders.Inventory;

internal static class EnhancementPackets
{
    public static byte[] Ack(InventoryResult result, EnhancementReceipt? receipt)
    {
        if (!result.Accepted) return CargoPackets.Ack(result);
        if (receipt is null) throw new InvalidOperationException("强化/增幅成功缺少实际提交结果");
        using var stream = new MemoryStream(27);
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)1);
        writer.Write((byte)receipt.Mode);
        writer.Write(receipt.MaterialSlot);
        writer.Write(receipt.MaterialRemaining);
        writer.Write((short)-1); // 附加券槽未启用；自动毁装保护使用后面的独立末端槽。
        writer.Write(receipt.Path);
        writer.Write(receipt.OldLevel);
        byte outcome = receipt.OutcomeCode ?? (receipt.Succeeded ? (byte)0 : (byte)1);
        writer.Write(outcome);
        writer.Write(receipt.NewLevel);
        writer.Write((byte)receipt.Target.List);
        writer.Write(receipt.Target.Index);
        writer.Write((short)-1);
        if (outcome == 3)
        {
            writer.Write(checked((byte)receipt.Refunds.Length));
            foreach (var row in receipt.Refunds)
            {
                writer.Write(row.Slot); writer.Write(row.ItemId); writer.Write(row.Count);
            }
        }
        // 02374932/0237493E必读额外库存槽及绝对余量；不虚构第二次消费。
        writer.Write(receipt.TerminalSlot);
        writer.Write(receipt.TerminalRemaining);
        writer.Write((byte)0); // 023749A4/023749B2继续读取两项状态，不沿旧25B合同截断。
        writer.Write((byte)0);
        return stream.ToArray();
    }
}
