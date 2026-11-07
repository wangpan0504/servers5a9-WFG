using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;

namespace DfoServer.Network.Builders.Inventory;

// 原生日历读取44/45，商店按材料声明选择其一；45与实体公共物品共演同一50格容器，不覆盖持久实例。
internal static class ActivityCalendarItemPackets
{
    public const byte RaidList = 44, CommonList = 45;

    public static byte[] Full(byte list, AccountWallet wallet, IReadOnlyList<int> itemIds,
        InventorySnapshot? inventory = null, long? utcSeconds = null)
    {
        var physical = PhysicalItems(list, inventory);
        short[] virtualSlots = VirtualSlots(list, itemIds, physical.Keys);
        long now = utcSeconds ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var entries = new SortedDictionary<short, Action<BinaryWriter>>();
        foreach (var (slot, item) in physical)
            entries[slot] = writer => ItemListProtocolWriter.Write(writer, item.Position, item, now);
        for (int index = 0; index < itemIds.Count; index++)
        {
            int id = itemIds[index], count = wallet.Material(id).Balance;
            if (count <= 0) continue;
            short slot = virtualSlots[index];
            entries[slot] = writer => ItemListProtocolWriter.Write(writer, slot, new ItemCore(id, ItemKind.Material, count));
        }
        return Encode(list, entries.Values);
    }

    // 正向变化使用14播报真实增量，减少或实体槽迁移使用1681静默更新；行内始终是绝对余额。
    public static IEnumerable<(byte[] Body, bool Obtained)> Changes(byte list, AccountWallet before, AccountWallet wallet,
        IReadOnlyList<int> itemIds, InventorySnapshot? beforeInventory = null, InventorySnapshot? afterInventory = null)
    {
        var currentInventory = afterInventory ?? beforeInventory;
        var priorInventory = beforeInventory ?? afterInventory;
        var beforePhysical = PhysicalItems(list, priorInventory);
        var afterPhysical = PhysicalItems(list, currentInventory);
        short[] beforeSlots = VirtualSlots(list, itemIds, beforePhysical.Keys);
        short[] afterSlots = VirtualSlots(list, itemIds, afterPhysical.Keys);
        var activeAfterVirtualSlots = new HashSet<short>();
        for (int index = 0; index < itemIds.Count; index++)
            if (wallet.Material(itemIds[index]).Balance > 0)
                activeAfterVirtualSlots.Add(afterSlots[index]);

        var silent = new SortedDictionary<short, ItemCore?>();
        var obtained = new SortedDictionary<short, ItemCore?>();
        for (int index = 0; index < itemIds.Count; index++)
        {
            int id = itemIds[index];
            int oldCount = before.Material(id).Balance;
            int newCount = wallet.Material(id).Balance;
            short oldSlot = beforeSlots[index];
            short newSlot = afterSlots[index];
            if (oldCount > 0 && (newCount <= 0 || oldSlot != newSlot) &&
                !afterPhysical.ContainsKey(oldSlot) && !activeAfterVirtualSlots.Contains(oldSlot))
                silent[oldSlot] = null;
            if (newCount > oldCount)
                obtained[newSlot] = new ItemCore(id, ItemKind.Material, newCount);
            else if (newCount > 0 && (newCount < oldCount || oldSlot != newSlot))
                silent[newSlot] = new ItemCore(id, ItemKind.Material, newCount);
        }
        if (silent.Count != 0)
            yield return (Encode(list, silent.Select<KeyValuePair<short, ItemCore?>, Action<BinaryWriter>>(
                pair => writer => ItemListProtocolWriter.Write(writer, pair.Key, pair.Value))), false);
        if (obtained.Count != 0)
            yield return (Encode(list, obtained.Select<KeyValuePair<short, ItemCore?>, Action<BinaryWriter>>(
                pair => writer => ItemListProtocolWriter.Write(writer, pair.Key, pair.Value))), true);
    }

    static Dictionary<short, InventoryItem> PhysicalItems(byte list, InventorySnapshot? inventory)
    {
        var result = new Dictionary<short, InventoryItem>();
        if (list != CommonList || inventory is null) return result;
        foreach (var item in inventory.Items.Where(item => item.Position.List == InventoryListType.AccountCommon))
        {
            if (item.Position.Index is < 0 or >= 50 || !result.TryAdd(item.Position.Index, item))
                throw new InvalidDataException("公共物品容器45实体槽越界或重复");
        }
        return result;
    }

    static short[] VirtualSlots(byte list, IReadOnlyList<int> itemIds, IReadOnlyCollection<short> occupied)
    {
        int capacity = list switch { RaidList => 64, CommonList => 50, _ => throw new InvalidDataException("活动账号材料容器无效") };
        if (occupied.Count + itemIds.Count > capacity || itemIds.Any(id => id <= 0) || itemIds.Distinct().Count() != itemIds.Count)
            throw new InvalidDataException("活动账号材料超出原生容器容量或模板登记无效");
        var slots = new short[itemIds.Count];
        int index = 0;
        for (short slot = 0; slot < capacity && index < slots.Length; slot++)
            if (!occupied.Contains(slot))
                slots[index++] = slot;
        return slots;
    }

    static byte[] Encode(byte list, IEnumerable<Action<BinaryWriter>> entries)
    {
        var rows = entries.ToArray();
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(list);
        writer.Write(checked((ushort)rows.Length));
        foreach (var write in rows) write(writer);
        return stream.ToArray();
    }
}
