using DfoServer.Game.Inventory.Models;

namespace DfoServer.Network.Builders.Inventory;

internal static class ConsumableUsePackets
{
    public static byte[] Ack(ConsumableUseRequest request, bool accepted)
    {
        using var bytes = new MemoryStream(); using var writer = new BinaryWriter(bytes);
        writer.Write(accepted ? (byte)1 : (byte)0);
        // 成功读源slot，失败读error；后继list/check/itemId两条分支都必读。
        writer.Write(accepted ? checked((ushort)request.Position.Index) : (ushort)123);
        writer.Write((byte)request.Position.List); writer.Write(request.OpaqueCheck); writer.Write(request.ItemId);
        return bytes.ToArray();
    }
}
