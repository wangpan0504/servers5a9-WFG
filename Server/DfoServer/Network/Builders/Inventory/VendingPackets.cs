using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Currency;

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
            var kind = CurrencyWallet.RewardKind(reward.ItemId) ?? throw new InvalidOperationException("售货机货币回执类型无效");
            writer.Write(checked((int)CurrencyWallet.Get(committed.Character.Inventory, kind)));
            writer.Write(new byte[ItemListProtocolWriter.EntrySize - 10]);
        }
        else foreach (var row in reward.Items)
        {
            var item = committed.Index.Find(row.Slot) ?? throw new InvalidOperationException("售货机奖励槽未提交");
            if (item.Core.ItemId != reward.ItemId) throw new InvalidOperationException("售货机奖励槽物品不匹配");
            writer.Write((byte)row.Slot.List);
            // 0372BEF6比较提交后总数与旧堆叠，0372BF85相减后填奖励弹窗及获得提示。
            ItemListProtocolWriter.Write(writer, row.Slot.Index, item.Core);
        }
        return stream.ToArray();
    }
}
