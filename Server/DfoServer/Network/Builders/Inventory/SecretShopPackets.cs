using DfoServer.Game.Inventory.Models;

namespace DfoServer.Network.Builders.Inventory;

internal static class SecretShopPackets
{
    public static byte[] Merchant(int merchant) => BitConverter.GetBytes(merchant);

    // NOTI280: 0238C950逐项读u32/u8/四u32/u8；+0x0C为购买后更新的剩余库存。
    public static byte[] Offers(SecretShopState state)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(state.Offers.Length);
        foreach (var offer in state.Offers)
        {
            writer.Write(offer.ItemId);
            writer.Write(offer.Option);
            writer.Write(offer.UnitGold);
            writer.Write(offer.Remaining);
            writer.Write(0);
            writer.Write(0);
            writer.Write((byte)0);
        }
        return stream.ToArray();
    }

    public static byte[] Bought(InventoryResult result, SecretShopPurchaseReceipt? purchase)
    {
        if (!result.Accepted) return CargoPackets.Ack(result);
        if (purchase is null || result.Receipt is not { } committed)
            throw new InvalidDataException("神秘商店成功缺少库存提交回执");
        var received = committed.Character.Inventory.Find(purchase.Position) ??
            throw new InvalidDataException("神秘商店成功缺少已提交商品");
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)1);
        writer.Write(committed.Character.Inventory.Gold);
        writer.Write(checked((ushort)purchase.Position.Index));
        writer.Write(received.Core.ItemId);
        writer.Write(received.Core.Value);
        writer.Write(received.Core.Attr);
        writer.Write(received.Core.Durability);
        writer.Write(-1); // 当前PVF全部为金币支付，不消耗额外背包材料。
        writer.Write(0);
        writer.Write(purchase.Remaining);
        return stream.ToArray();
    }
}
