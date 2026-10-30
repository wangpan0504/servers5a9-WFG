using DfoServer.Game.Inventory.Models;

namespace DfoServer.Network.Builders.Inventory;

internal static class ClearAvatarPackets
{
    public static byte[] Sources(InventorySnapshot inventory, InventoryIndex? index = null)
    {
        index ??= new(inventory);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        // 028697E0逐行读取u8部位、u16来源格；写成u32会使后续部位全部错位。
        writer.Write((byte)11);
        for (byte slot = 0; slot < 11; slot++)
        {
            var detail = index.Find(new(InventoryListType.Equipment, slot))?.Avatar;
            var source = detail is { ClearAvatarId: > 0, ClearAvatarSourceUid: > 0 }
                ? index.FindUid(detail.ClearAvatarSourceUid) : null;
            bool valid = source is { Avatar: not null, Position.List: InventoryListType.Avatar } &&
                source.Core.ItemId == detail!.ClearAvatarId;
            writer.Write(slot);
            writer.Write(valid ? checked((ushort)(source!.Position.Index + 12)) : (ushort)0);
        }
        return stream.ToArray();
    }
}
