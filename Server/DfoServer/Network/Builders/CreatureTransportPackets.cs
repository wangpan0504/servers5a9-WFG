using DfoServer.Game.Creatures;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Network.Builders;

internal static class CreatureTransportPackets
{
    public static byte[] Ack(CreatureTransportRequest request, InventoryResult result)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(result.Accepted ? (byte)1 : (byte)0);
        if (!result.Accepted) writer.Write(result.Error);
        else
        {
            // 024C8250依次读四个u16及模式。第三项为源绝对剩余量；mode1本身不删除Pet目标。
            writer.Write(request.StoneSlot);
            writer.Write(request.PetSlot);
            int count = request.Mode == 2 ? 0 : result.Receipt!.Character.Inventory.Find(new(InventoryListType.Main, (short)request.StoneSlot))?.Core.Count ?? 0;
            writer.Write((ushort)Math.Min(ushort.MaxValue, count));
            var target = request.Mode == 0 ? result.Plan!.BeforeIndex.Find(new(InventoryListType.Main, (short)request.PetSlot)) : null;
            writer.Write(target is null ? (ushort)0 : checked((ushort)result.Receipt!.Character.Inventory.Items.Single(item => item.Uid == target.Uid).Position.Index));
            writer.Write(request.Mode);
        }
        return stream.ToArray();
    }
}
