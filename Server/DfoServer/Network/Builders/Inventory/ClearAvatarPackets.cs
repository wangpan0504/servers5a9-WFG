using DfoServer.Game.Inventory.Models;

namespace DfoServer.Network.Builders.Inventory;

internal static class ClearAvatarPackets
{
    public static byte[] Sources(InventorySnapshot inventory, InventoryIndex? index = null)
    {
        index ??= new(inventory);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        // 028697E0在count=11时先清旧表，02869DB0将来源格减12后查本人装扮包裹。
        writer.Write((byte)11);
        for (byte slot = 0; slot < 11; slot++)
        {
            var detail = index.Find(new(InventoryListType.Equipment, slot))?.Avatar;
            var source = detail is { ClearAvatarId: > 0, ClearAvatarSourceUid: > 0 }
                ? index.FindUid(detail.ClearAvatarSourceUid) : null;
            bool valid = source is { Avatar: not null, Position.List: InventoryListType.Avatar } &&
                source.Core.ItemId == detail!.ClearAvatarId;
            writer.Write(slot);
            writer.Write(valid ? checked(source!.Position.Index + 12) : 0);
        }
        return stream.ToArray();
    }
}
