using DfoServer.Game.Inventory.Models;

namespace DfoServer.Network.Builders.Inventory;

internal static class EquipmentEffectPackets
{
    public static byte[] Ack(InventoryResult result, short targetSlot)
    {
        if (!result.Accepted) return CargoPackets.Ack(result);
        var item = result.Receipt?.Character.Inventory.Find(new(InventoryListType.Main, targetSlot)) ??
            result.Plan?.After.Find(new(InventoryListType.Main, targetSlot)) ??
            throw new InvalidOperationException("装备特效成功回执缺少实际目标");
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)1);
        // 0233E580读16B，+4交024B7EF0核对模板及末尾目标槽，+8作为附加/解除结果显示。
        writer.Write(0);
        writer.Write(item.Core.ItemId);
        writer.Write((int)item.Core.Rune);
        writer.Write((int)targetSlot);
        return stream.ToArray();
    }
}
