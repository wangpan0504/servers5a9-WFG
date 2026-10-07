using DfoServer.Game.Inventory.Models;

namespace DfoServer.Network.Builders.Inventory;

internal static class EquipmentProtocolWriter
{
    static InventoryItem[] Worn(InventorySnapshot inventory) => inventory.Items
        .Where(item => item.Position.List == InventoryListType.Equipment).OrderBy(item => item.Position.Index).ToArray();

    public static void Appearance(BinaryWriter writer, InventorySnapshot inventory) => Appearance(writer, inventory.Items);

    public static void Appearance(BinaryWriter writer, IEnumerable<InventoryItem> equipment)
    {
        var items = equipment.Where(item => item.Position.List == InventoryListType.Equipment).OrderBy(item => item.Position.Index).ToArray();
        writer.Write(checked((byte)items.Length));
        foreach (var item in items)
        {
            var core = item.Core;
            writer.Write(checked((byte)item.Position.Index));
            writer.Write(core.ItemId);
            // 02675DE0 -> 028F2A80 必读一个4B资源扩展，普通装备初始化为零。
            ItemListProtocolWriter.WriteAvatarColors(writer, item.Avatar);
            writer.Write((byte)((core.Attr << 1) | (core.AmplifyType == 0 ? 0 : 1)));
            writer.Write(core.GenuineUpgrade);
            writer.Write(core.EmancipateLevel);
            writer.Write(item.Avatar?.ClearAvatarId ?? core.Marker16);
            if (item.Position.Index == 9 && item.Avatar is { ClearAvatarId: > 0 })
                ItemListProtocolWriter.WriteAvatarSockets(writer, item.Avatar);
            writer.Write(core.EnchantCardId);
            writer.Write(core.EnchantUpgrade);
        }
    }

    public static void Player(BinaryWriter writer, InventorySnapshot inventory, long? utcSeconds = null)
    {
        long now = utcSeconds ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var items = Worn(inventory);
        writer.Write(checked((byte)items.Length));
        foreach (var item in items)
        {
            var core = item.Core;
            writer.Write(checked((byte)item.Position.Index));
            writer.Write(core.ItemId);
            writer.Write(item.Avatar is null ? core.Value : item.RemainingSeconds(now));
            writer.Write(core.Attr);
            writer.Write(core.Durability);
            writer.Write((byte)0);
            writer.Write((byte)0); // 023C3BFA/023C3C08 的两项资源属性尚未命名。
            writer.Write(item.Avatar is { ClearAvatarId: > 0 } avatar ? avatar.ClearAvatarId : (int)core.SealFlag);
            if (item.Position.Index == 9 && item.Avatar is { ClearAvatarId: > 0 })
                ItemListProtocolWriter.WriteAvatarSockets(writer, item.Avatar);
            writer.Write(core.EnchantCardId);
            writer.Write(core.EnchantUpgrade);
            writer.Write(core.AmplifyType);
            writer.Write(core.AmplifyValue);
            if (item.Avatar is not null)
            {
                ItemListProtocolWriter.WriteAvatarSockets(writer, item.Avatar);
                ItemListProtocolWriter.WriteAvatarColors(writer, item.Avatar);
            }
            if (item.Creature is not null)
                writer.Write(item.RemainingSeconds(now));
            var chronicle = ItemListProtocolWriter.Chronicles(core);
            writer.Write((byte)chronicle.Length);
            foreach (var option in chronicle)
            {
                writer.Write(option.Id);
                writer.Write(option.Job);
                writer.Write(option.GrowType);
                writer.Write(option.EquipmentType);
                writer.Write(option.Number);
            }
            writer.Write(item.Core.IsRental ? item.RemainingSeconds(now) : core.ExpireTime);
            writer.Write(core.EmblemSocketCount);
            if (core.EmblemSocketCount > 0)
                writer.Write(core.Emblem1);
            if (core.EmblemSocketCount > 1)
                writer.Write(core.Emblem2);
            // 023C3F7C -> 024B2D60 读 Rune:u16；023C3F88 -> 022913F0 读随机属性，零数量也必须占1B。
            writer.Write(core.Rune);
            var random = new[] { core.Random0, core.Random1, core.Random2 }.Where(option => option.Type != 0).ToArray();
            writer.Write((byte)random.Length);
            foreach (var option in random)
            {
                writer.Write(option.Type);
                writer.Write(option.Value1);
                writer.Write(option.Value2);
            }
            if (random.Length != 0)
            {
                writer.Write(core.RandomState);
                writer.Write(core.RandomChangedIndex);
                if (core.RandomChangedIndex != byte.MaxValue)
                {
                    writer.Write(core.RandomChangeState);
                    writer.Write(core.RandomChange.Type);
                    writer.Write(core.RandomChange.Value1);
                    writer.Write(core.RandomChange.Value2);
                }
            }
            ItemListProtocolWriter.WriteTail(writer, core);
            // 023C4064..023C40AD: 五个长度前缀块，中间额外读一个u8。
            Raw(writer, extension => extension.Write(core.Extension97));
            Raw(writer, extension => extension.Write(core.GuardianGemKeys));
            writer.Write(core.Extension109);
            Raw(writer, extension => extension.Write(core.Extension110));
            Raw(writer, extension => { extension.Write(core.Extension114.Value); extension.Write(core.Extension114.Flag); });
            Raw(writer, extension => { extension.Write(core.Extension119); extension.Write(core.Extension120); extension.Write(core.Extension121); });
        }
    }

    static void Raw(BinaryWriter writer, Action<BinaryWriter> write)
    {
        using var stream = new MemoryStream();
        using var extension = new BinaryWriter(stream);
        write(extension);
        writer.Write(checked((uint)stream.Length));
        writer.Write(stream.ToArray());
    }
}
