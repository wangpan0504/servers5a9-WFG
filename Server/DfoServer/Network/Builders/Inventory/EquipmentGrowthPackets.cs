using System.Buffers.Binary;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Network.Builders.Inventory;

internal static class EquipmentGrowthPackets
{
    public static byte[] Experience(InventoryItem item, ushort gain)
    {
        byte[] body = new byte[8];
        body[0] = (byte)item.Position.List;
        BinaryPrimitives.WriteInt16LittleEndian(body.AsSpan(1), item.Position.Index);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(3), gain);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(5), item.Core.Extension90);
        body[7] = item.Core.Extension92;
        return body;
    }

    public static byte[] State(IReadOnlyList<InventoryItem> items, bool changedTemplate)
    {
        if (items.Count > byte.MaxValue) throw new InvalidDataException("成长装备通知过多");
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(changedTemplate ? (byte)1 : (byte)2);
        writer.Write((byte)items.Count);
        foreach (var item in items)
        {
            writer.Write((byte)item.Position.List);
            writer.Write(item.Position.Index);
            writer.Write(item.Core.ItemId);
            writer.Write(item.Core.Extension90);
            writer.Write(item.Core.Extension92);
        }
        return stream.ToArray();
    }
}
