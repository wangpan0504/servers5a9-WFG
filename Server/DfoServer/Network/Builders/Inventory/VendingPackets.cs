using DfoServer.Game.Inventory.Models;

namespace DfoServer.Network.Builders.Inventory;

internal static class VendingPackets
{
    public static byte[] Opened(InventoryResult result, VendingReceipt? reward)
    {
        if (!result.Accepted) return [0, result.Error == 4 ? (byte)4 : (byte)1, 0];
        if (reward is null || result.Receipt is not { } committed)
            throw new InvalidOperationException("售货机成功响应缺少提交回执");
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)1);
        writer.Write(reward.CoinSlot);
        writer.Write(reward.RemainingCoins);
        writer.Write(reward.ItemId);
        writer.Write(reward.Count);
        writer.Write(0);
        writer.Write(0);
        writer.Write(checked((ushort)(reward.Contract || reward.CurrencySlot is not null ? 1 : reward.Items.Length)));
        if (reward.Contract)
        {
            writer.Write((byte)6);
            writer.Write(reward.ItemId);
            writer.Write(reward.Count);
        }
        else if (reward.CurrencySlot is { } currencySlot)
        {
            writer.Write((byte)InventoryListType.Main);
            writer.Write(currencySlot);
            writer.Write(reward.ItemId);
            writer.Write(reward.Count);
            writer.Write(new byte[ItemListProtocolWriter.EntrySize - 10]);
        }
        else foreach (var row in reward.Items)
        {
            var item = committed.Index.Find(row.Slot) ?? throw new InvalidOperationException("售货机奖励槽未提交");
            if (item.Core.ItemId != reward.ItemId) throw new InvalidOperationException("售货机奖励槽物品不匹配");
            writer.Write((byte)row.Slot.List);
            // 0237AF41读取123B记录，已有堆叠按本次获得数量增加，完整总数随后由NOTI14同步。
            ItemListProtocolWriter.Write(writer, row.Slot.Index, item.Core.IsStackable ? item.Core with { Value = row.Count } : item.Core);
        }
        return stream.ToArray();
    }
}
