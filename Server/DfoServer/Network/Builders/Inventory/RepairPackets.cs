using DfoServer.Game.Inventory.Models;

namespace DfoServer.Network.Builders.Inventory;

internal static class RepairPackets
{
    public static byte[] Ack(RepairEquipmentRequest request, InventoryResult result)
    {
        if (!result.Accepted)
            return CargoPackets.Ack(result);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)1);
        writer.Write(result.Plan!.After.Gold);
        writer.Write((byte)request.Position.List);
        writer.Write(request.Position.Index);
        writer.Write((ushort)0); // 普通完整修理；02355DC8必读u16，零值使本地恢复模板耐久。
        return stream.ToArray();
    }
}
