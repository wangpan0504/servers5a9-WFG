using DfoServer.Game.Inventory.Models;

namespace DfoServer.Network.Builders.Inventory;

internal static class LotteryPackets
{
    public static byte[] Opened(InventoryResult result, LotteryReceipt? receipt)
    {
        if (!result.Accepted)
            return CargoPackets.Ack(result);
        if (receipt is null || result.Receipt is null)
            throw new InvalidOperationException("袖珍罐成功应答缺少已提交回执");
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)1);
        writer.Write(receipt.SourceSlot);
        if (receipt.ItemId == 0)
        {
            if (receipt.Count <= 0 || result.Plan is null ||
                result.Plan.After.Gold != result.Receipt.Character.Inventory.Gold ||
                result.Plan.After.Gold - (long)result.Plan.Before.Gold != receipt.Count)
                throw new InvalidOperationException("袖珍罐金币奖励未提交");
            // 023766AF：零编号与零数量走末尾u32；不能伪造普通奖励槽或重复增加金币。
            writer.Write(new byte[ItemListProtocolWriter.EntrySize]);
            writer.Write(receipt.Count);
            return stream.ToArray();
        }
        var item = result.Receipt.Index.Find(receipt.RewardSlot) ??
            throw new InvalidOperationException("袖珍罐奖励槽位未提交");
        if (item.Core.ItemId != receipt.ItemId)
            throw new InvalidOperationException("袖珍罐奖励与已提交槽位不一致");
        // 02375F56的消费者对已有堆叠做增量相加；不能把合并后的总数量再加一次。
        ItemListProtocolWriter.Write(writer, receipt.RewardSlot.Index,
            item.Core.IsStackable ? item.Core with { Value = receipt.Count } : item.Core);
        // 023766AF仅在记录的物品号和数量均为0时使用此备选数量；实体奖励不进入该分支。
        writer.Write(0);
        return stream.ToArray();
    }
}
