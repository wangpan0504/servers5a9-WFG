using DfoServer.Game.Inventory.Models;

namespace DfoServer.Network.Builders.Inventory;

internal static class EquipmentTransmissionPackets
{
    public static byte[] Ack(InventoryResult result, short targetSlot, int targetId)
    {
        if (!result.Accepted) return CargoPackets.Ack(result);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)1);
        // 02353B50先读10字节结果结构，结果0才向0x63F成功窗传递槽位和物品编号。
        writer.Write(0);
        writer.Write(targetSlot);
        writer.Write(targetId);
        return stream.ToArray();
    }
}
