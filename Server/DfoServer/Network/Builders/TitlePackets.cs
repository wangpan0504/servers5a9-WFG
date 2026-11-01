using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Titles;
using DfoServer.GameWorld.Quests;
using DfoServer.Network.Builders.Inventory;

namespace DfoServer.Network.Builders;

internal static class TitlePackets
{
    public static byte[] CloneTitle(uint itemId)
    {
        byte[] body = new byte[5];
        body[0] = 1;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(1), itemId);
        return body;
    }

    public static byte[] CloneTitleFailure(ushort error) => [0, (byte)error, (byte)(error >> 8)];

    public static byte[] Book(InventorySnapshot inventory, int category, ushort owner = 0, bool show = false)
    {
        if (category is < 0 or > 4) throw new InvalidDataException("称号分类不在客户端五种范围");
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write(show ? (byte)1 : (byte)0); writer.Write(owner); writer.Write(category);
        BookItems(writer, inventory, category);
        return stream.ToArray();
    }
    // 023DEA00：定位头后批量读取五类，类别为u8，物品行与358相同。
    public static byte[] RaidBook(InventorySnapshot inventory, byte server, byte channel, ushort owner)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write(channel); writer.Write(server); writer.Write(owner);
        writer.Write((byte)1); writer.Write((byte)5);
        for (int category = 0; category < 5; category++)
        {
            writer.Write((byte)category);
            BookItems(writer, inventory, category);
        }
        return stream.ToArray();
    }
    static void BookItems(BinaryWriter writer, InventorySnapshot inventory, int category)
    {
        var rows = inventory.Items.Where(item => item.Position.List == (InventoryListType)(18 + category)).OrderBy(item => item.Position.Index).ToArray();
        writer.Write(rows.Length);
        foreach (var item in rows)
        {
            var core = item.Core;
            writer.Write(item.Position.Index); writer.Write(core.ItemId); writer.Write(core.Value); writer.Write(core.Attr);
            writer.Write(core.Durability); writer.Write(core.SealFlag); writer.Write(core.EnchantCardId); writer.Write(core.EnchantUpgrade);
            ItemListProtocolWriter.WriteAmplification(writer, core);
            // 023C32C6的RAW块写入两个u16；随后023C32D5读取独立i32。
            writer.Write(4u); writer.Write(core.Extension97); writer.Write(core.Marker16);
        }
    }
    public static byte[] Progress(Character character)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write(character.TitleProgress.Count);
        foreach (var (id, progress) in character.TitleProgress.OrderBy(pair => pair.Key))
        {
            writer.Write(id); Counters(writer, progress.Remaining);
        }
        var recent = character.TitleProgress.Where(pair => pair.Value.Remaining.Complete).OrderByDescending(pair => pair.Value.CompletedAtRevision)
            .ThenByDescending(pair => pair.Key).Take(4).Select(pair => pair.Key).ToArray();
        // 023AA76B的固定16B尾部是最近四个完成ID，不是章节奖励或心情状态。
        for (int at = 0; at < 4; at++) writer.Write(at < recent.Length ? recent[at] : 0);
        return stream.ToArray();
    }
    public static byte[] Trigger(TitleAchievementResult result)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write(result.Accepted ? (byte)1 : (byte)0);
        if (!result.Accepted) writer.Write(result.Error);
        writer.Write(result.QuestId);
        if (result.Accepted) Counters(writer, result.Remaining);
        return stream.ToArray();
    }
    public static byte[] Complete(int questId, TitleCatalog catalog, InventorySnapshot inventory)
    {
        var slot = catalog.Slots.FirstOrDefault(pair => pair.Value.QuestId == questId);
        var item = slot.Value is null ? null : inventory.Find(TitleBookService.Slot(slot.Key.Category, slot.Key.Index));
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write(questId); writer.Write(item is null ? -1 : slot.Key.Category); writer.Write(item is null ? -1 : slot.Key.Index);
        writer.Write(item?.Core.ItemId ?? 0); writer.Write(item?.Core.Durability ?? (ushort)0);
        return stream.ToArray();
    }
    public static byte[] BookAck(TitleBookRequest request, InventoryResult result)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write(result.Accepted ? (byte)1 : (byte)0);
        if (!result.Accepted) writer.Write(result.Error);
        writer.Write(request.List);
        if (result.Accepted) writer.Write(request.Slot);
        writer.Write(request.Category);
        if (result.Accepted) writer.Write(request.Index);
        return stream.ToArray();
    }
    static void Counters(BinaryWriter writer, TitleCounters counters)
    {
        writer.Write(counters.First); writer.Write(counters.Second); writer.Write(counters.Third);
    }
}
