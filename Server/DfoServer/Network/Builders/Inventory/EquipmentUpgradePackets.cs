using DfoServer.Game.Inventory.Models;

namespace DfoServer.Network.Builders.Inventory;

internal static class EquipmentUpgradePackets
{
    public static byte[] Upgrade(InventoryResult result, short slot, int sourceId, byte additional)
    {
        if (!result.Accepted) return CargoPackets.Ack(result);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)1);
        writer.Write(slot);
        writer.Write(sourceId);
        writer.Write(additional);
        return stream.ToArray();
    }

    public static byte[] Imprint(InventoryResult result, short targetSlot, int targetId)
    {
        if (!result.Accepted) return CargoPackets.Ack(result);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)1);
        writer.Write(targetSlot);
        // 0234C710 经0402D3A0查询升级等价物或刻印目标集合；源装备编号不能命中。
        writer.Write(targetId);
        return stream.ToArray();
    }
}
