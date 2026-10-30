using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;

namespace DfoServer.Network.Builders.Inventory;

internal static class EquipmentProtocolWriter
{
    public static void Runes(BinaryWriter writer, InventorySnapshot inventory)
    {
        var ids = new int[6];
        foreach (var item in inventory.Items.Where(item => item.Position.List == InventoryListType.Talisman &&
                     item.Position.Index is >= 210 and <= 215))
            ids[item.Position.Index - 210] = item.Core.ItemId;
        bool populated = ids.Any(id => id != 0);
        writer.Write(populated ? (byte)6 : (byte)0);
        if (populated)
            foreach (int id in ids)
                writer.Write(id);
    }

    static InventoryItem[] Worn(InventorySnapshot inventory) => inventory.Items
        .Where(item => item.Position.List == InventoryListType.Equipment && item.Position.Index != 30).OrderBy(item => item.Position.Index).ToArray();

    public static void Appearance(BinaryWriter writer, InventorySnapshot inventory) => Appearance(writer, inventory.Items);

    public static void Appearance(BinaryWriter writer, IEnumerable<InventoryItem> equipment)
    {
        var items = equipment.Where(item => item.Position.List == InventoryListType.Equipment && item.Position.Index != 30).OrderBy(item => item.Position.Index).ToArray();
        writer.Write(checked((byte)items.Length));
        foreach (var item in items)
            AppearanceEntry(writer, checked((byte)item.Position.Index), item.Core.ItemId, item);
    }

    public static void RosterAppearance(BinaryWriter writer, IEnumerable<InventoryItem> equipment, int weaponSkin, ItemCatalog? catalog)
    {
        var items = equipment.Where(item => item.Position.List == InventoryListType.Equipment && item.Position.Index != 30)
            .OrderBy(item => item.Position.Index).ToArray();
        var weapon = items.SingleOrDefault(item => item.Position.Index == 12);
        var avatar = items.SingleOrDefault(item => item.Position.Index == 10);
        int visibleId = weaponSkin > 0 ? weaponSkin : avatar?.Avatar is { ClearAvatarId: > 0 } clear
            ? clear.ClearAvatarId : avatar is not null && (catalog is null ||
                catalog.TryGet(avatar.Core.ItemId, out var template) && !template.CloneAvatar)
                ? avatar.Core.ItemId : 0;
        if (visibleId == 0)
        {
            Appearance(writer, items);
            return;
        }
        // 选角替代动画02AB6760会重新读槽12模板，忽略独立幻化字段及克隆来源。
        // 只投影视图编号，不构造虚假库存实例，也不改真实装备或城镇Actor。
        // 02ABF52E遇到槽10会跳过槽12，导致特效读取的Actor+C8C未安装。
        // 已解析的武器显示只输出槽12，让02ABA5D0同时安装强化和增幅。
        writer.Write(checked((byte)(items.Length - (avatar is null ? 0 : 1) + (weapon is null ? 1 : 0))));
        var source = weaponSkin > 0 ? weapon : avatar;
        // 外观编号及染色来自显示层；强化等级和增幅标志始终来自真实武器。
        byte weaponEffect = PackEffect(weapon?.Core);
        foreach (var item in items)
            if (item.Position.Index == 12)
                AppearanceEntry(writer, 12, visibleId, source, resolved: true, packedEffect: weaponEffect);
            else if (item.Position.Index != 10)
                AppearanceEntry(writer, checked((byte)item.Position.Index), item.Core.ItemId, item);
        if (weapon is null)
            AppearanceEntry(writer, 12, visibleId, source, resolved: true, packedEffect: weaponEffect);
    }

    static byte PackEffect(ItemCore? core) => (byte)(((core?.Attr ?? 0) << 1) | (core is { AmplifyType: not 0 } ? 1 : 0));

    static void AppearanceEntry(BinaryWriter writer, byte slot, int itemId, InventoryItem? item, bool resolved = false,
        byte? packedEffect = null)
    {
        var core = item?.Core;
        writer.Write(slot);
        writer.Write(itemId);
        // 02675DE0 -> 028F2A80 必读一个4B资源扩展，普通装备初始化为零。
        ItemListProtocolWriter.WriteAvatarColors(writer, item?.Avatar);
        writer.Write(packedEffect ?? PackEffect(core));
        writer.Write(core?.GenuineUpgrade ?? (byte)0);
        writer.Write(core?.EmancipateLevel ?? (byte)0);
        writer.Write(resolved ? 0 : item?.Avatar?.ClearAvatarId ?? core?.Marker16 ?? 0);
        if (slot == 9 && item?.Avatar is { ClearAvatarId: > 0 })
            ItemListProtocolWriter.WriteAvatarSockets(writer, item.Avatar);
        writer.Write(core?.EnchantCardId ?? 0);
        writer.Write(core?.EnchantUpgrade ?? (byte)0);
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
            writer.Write(core.Extension121); // 023C3BFA/023C3C08合并为u16，进入普通核心+195。
            writer.Write(item.Avatar is { } avatar ? avatar.ClearAvatarId : (int)core.SealFlag);
            if (item.Position.Index == 9 && item.Avatar is { ClearAvatarId: > 0 })
                ItemListProtocolWriter.WriteAvatarSockets(writer, item.Avatar);
            writer.Write(core.EnchantCardId);
            writer.Write(core.EnchantUpgrade);
            ItemListProtocolWriter.WriteAmplification(writer, core);
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
            writer.Write(item.Core.IsRental ? item.RemainingSeconds(now) : ItemExpiration.ForClient(core.ExpireTime));
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
            ItemListProtocolWriter.WriteTail(writer, core with { SortLockFlag = 0 });
            // 023C4064..023C40AD: 五个长度前缀块，中间额外读一个u8。
            Raw(writer, extension => extension.Write(core.Extension97));
            Raw(writer, extension => extension.Write(core.GuardianGemKeys));
            writer.Write(core.Extension109);
            Raw(writer, extension => { extension.Write(core.Extension114.Value); extension.Write(core.Extension114.Flag); });
            // 023C40A1目标仅为1B；多写会覆盖前面的来源编号临时变量。
            Raw(writer, extension => extension.Write(core.Extension119));
            Raw(writer, extension => extension.Write((int)core.Extension120));
        }
    }

    // 当前D95：0267D630/0267BC60各读取8B，绑定名称装饰专用槽30。
    // 该槽单独投影，不能同时放入普通穿戴列表重复创建客户端对象。
    public static void NameTag(BinaryWriter writer, InventorySnapshot inventory, long? utcSeconds = null)
    {
        var item = inventory.Find(new(InventoryListType.Equipment, 30));
        bool active = item is not null && !item.IsExpired(utcSeconds ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        writer.Write(active ? item!.Core.ItemId : 0);
        writer.Write(active ? ItemExpiration.ForClient(item!.ExpireTime) : 0);
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
