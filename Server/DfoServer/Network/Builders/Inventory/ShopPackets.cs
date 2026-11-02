using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;

namespace DfoServer.Network.Builders.Inventory;

internal static class ShopPackets
{
    public static byte[] PurchaseCounts(ShopPurchaseCount[]? rows)
    {
        if (rows is null)
            return CargoPackets.Ack(InventoryResult.Reject(17));
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)1);
        var legacy = rows.Where(row => row.Period == ItemPurchasePeriod.Legacy).ToArray();
        var periodic = rows.Where(row => row.Period != ItemPurchasePeriod.Legacy).ToArray();
        writer.Write(legacy.Length);
        foreach (var row in legacy)
        {
            writer.Write(row.ItemId);
            writer.Write(row.Count);
        }
        // 02350220的第1/3组均经01FD4740写购买缓存062CEC30；第2/4组为销售缓存062CEC44。
        // purchase limit与daily purchase limit共用购买缓存，不能按新旧声明误投到销售组。
        writer.Write(0);
        writer.Write(periodic.Length);
        foreach (var row in periodic)
        {
            writer.Write(row.ItemId);
            writer.Write(row.Count);
        }
        writer.Write(0); // 当前没有销售计数查询投影。
        return stream.ToArray();
    }
    public static byte[] Bought(InventoryResult result, ShopPurchaseReceipt? receipt, InventorySnapshot? committed = null)
    {
        if (!result.Accepted)
            return CargoPackets.Ack(result);
        if (receipt is null)
            throw new InvalidDataException("购买提交缺少回执");
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)1);
        writer.Write(receipt.Paid.Gold);
        writer.Write(receipt.Paid.WinPoints);
        writer.Write(0); // 普通NPC不使用的特殊商店点数。
        writer.Write(receipt.Paid.AccountWallet.Get(CurrencyKind.Cera));
        if (receipt.Core is { } core)
        {
            // 时装/宠物的预检核心尚无 UID；只从本次已提交位置取详情及持久身份。
            if (core.Kind is DfoServer.GameWorld.Items.ItemKind.Avatar or DfoServer.GameWorld.Items.ItemKind.Creature)
            {
                var received = committed?.Find(receipt.Position) ?? throw new InvalidDataException("特殊商品缺少已提交详情");
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                core = received.Core with
                {
                    Value = received.Avatar is null ? received.Core.Value : received.RemainingSeconds(now),
                    Marker16 = received.Creature is null ? received.Core.Marker16 : received.RemainingSeconds(now)
                };
            }
            // 同一数量还在0236BF62..0236BF8F更新购买计数；跨堆也必须写本次完整数量。
            ItemListProtocolWriter.Write(writer, receipt.Position.Index, core.IsStackable ? core with { Value = receipt.Count } : core);
        }
        else
        {
            var kind = CurrencyWallet.RewardKind(receipt.ItemId) ?? throw new InvalidDataException("购买回执缺少物品");
            var increment = CurrencyWallet.Set(receipt.Paid, kind, receipt.Count);
            if (!CurrencyPackets.WriteVirtual(writer, increment, receipt.Position.Index))
                throw new InvalidDataException("购买回执的虚拟槽无效");
        }
        writer.Write(checked((byte)receipt.Materials.Length));
        foreach (var material in receipt.Materials)
        {
            writer.Write(material.ItemId);
            writer.Write(checked((uint)material.Remaining));
        }
        return stream.ToArray();
    }

    public static byte[] Sold(InventoryResult result, SellItemRequest request)
    {
        if (!result.Accepted)
            return CargoPackets.Ack(result);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)1);
        writer.Write(result.Plan!.After.Gold);
        writer.Write((byte)request.Position.List);
        writer.Write(request.Position.Index);
        writer.Write(result.MoveCount); // Client 为i32删除数量，不是参考版本的i16或剩余量。
        return stream.ToArray();
    }
}
