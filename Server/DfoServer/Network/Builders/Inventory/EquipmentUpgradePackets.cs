using DfoServer.Game.Inventory.Models;

namespace DfoServer.Network.Builders.Inventory;

internal static class EquipmentUpgradePackets
{
    public static byte[] Upgrade(InventoryResult result, short slot, int resultId, byte additional)
    {
        if (!result.Accepted) return CargoPackets.Ack(result);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)1);
        writer.Write(slot);
        // 0234C290 把该编号交给0402D110判定结果窗（升级367/刻印284）；提交后的装备编号才算命中，源编号不能命中。
        writer.Write(resultId);
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
