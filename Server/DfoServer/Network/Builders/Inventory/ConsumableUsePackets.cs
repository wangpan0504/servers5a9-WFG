using DfoServer.Game.Inventory.Models;

namespace DfoServer.Network.Builders.Inventory;

internal static class ConsumableUsePackets
{
    public static byte[] Ack(ConsumableUseRequest request, bool accepted, bool noConsumption = false)
        => Body(request, accepted && !noConsumption,
            accepted ? noConsumption ? (ushort)0 : checked((ushort)request.Position.Index) : (ushort)123);

    public static byte[] Rejected(ConsumableUseRequest request, ClientUseItemError error)
        => Body(request, false, ClientFailurePackets.Code(error));

    static byte[] Body(ConsumableUseRequest request, bool accepted, ushort value)
    {
        using var bytes = new MemoryStream(); using var writer = new BinaryWriter(bytes);
        writer.Write(accepted ? (byte)1 : (byte)0);
        // 原生免扣以0/0结束pending；本版错误码为u16，不能照搬旧端的u8。
        writer.Write(value);
        writer.Write((byte)request.Position.List); writer.Write(request.OpaqueCheck); writer.Write(request.ItemId);
        return bytes.ToArray();
    }
}
