using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;

namespace DfoServer.Network.Builders.Inventory;

internal static class InventoryPackets
{
    public static byte[] SpecialFull(InventorySnapshot inventory, InventoryListType list, long? utcSeconds = null)
    {
        if (list is not (InventoryListType.Equipment or InventoryListType.Avatar or InventoryListType.Pet or InventoryListType.GuildMedal or InventoryListType.Talisman))
            throw new ArgumentOutOfRangeException(nameof(list));
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        var items = inventory.Items.Where(item => item.Position.List == list).OrderBy(item => item.Position.Index).ToArray();
        writer.Write((byte)list);
        if (list == InventoryListType.Avatar)
            // 028529D2 将该扩展值加105后设置容器容量。
            writer.Write(inventory.AvatarExpansion);
        writer.Write(checked((ushort)items.Length));
        long now = utcSeconds ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (var item in items)
            ItemListProtocolWriter.Write(writer, item.Position, item, now);
        return stream.ToArray();
    }

    public static byte[] Creatures(InventorySnapshot inventory)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        var items = inventory.Items.Where(item => item.Creature is not null).OrderBy(item => item.Uid).ToArray();
        writer.Write(checked((byte)items.Length));
        foreach (var item in items)
        {
            var detail = item.Creature!;
            writer.Write(checked((int)item.Uid));
            writer.Write(detail.Satiety);
            writer.Write(detail.Mode);
            writer.Write(detail.Experience);
            if (detail.Mode == 1)
            {
                writer.Write(detail.Mode1Field0);
                writer.Write(detail.Mode1Field1);
            }
            writer.Write(detail.Level);
            PacketEncoding.WriteString(writer, detail.Name);
            writer.Write(detail.TailFlag);
        }
        return stream.ToArray();
    }
    public static byte[] Full(InventorySnapshot inventory, long? utcSeconds = null)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        var items = inventory.Items.Where(item => item.Position.List == InventoryListType.Main).ToDictionary(item => item.Position.Index);
        writer.Write((byte)InventoryListType.Main);
        writer.Write(inventory.Expansion); // 023BDF17 读扩展格数:u16，023BDF38 右移3位得到0–3档。
        var slots = items.Keys.Concat(CurrencyPackets.VirtualSlots).Order().ToArray();
        writer.Write(checked((ushort)slots.Length));
        long now = utcSeconds ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (short slot in slots)
            if (!CurrencyPackets.WriteVirtual(writer, inventory, slot))
            {
                var item = items[slot];
                if (item.Core is { IsRental: true, Kind: ItemKind.Equipment })
                    ItemListProtocolWriter.Write(writer, item.Position, item, now);
                else
                    ItemListProtocolWriter.Write(writer, slot, item.Core);
            }
        return stream.ToArray();
    }

    public static byte[] Delta(InventorySnapshot inventory, InventoryListType list, InventorySlot[] slots, long? utcSeconds = null,
        InventoryIndex? index = null)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)list);
        var changed = slots.Where(slot => slot.List == list).Distinct().OrderBy(slot => slot.Index).ToArray();
        writer.Write(checked((ushort)changed.Length));
        long now = utcSeconds ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (var slot in changed)
            if (list != InventoryListType.Main || !CurrencyPackets.WriteVirtual(writer, inventory, slot.Index))
                ItemListProtocolWriter.Write(writer, slot, index is null ? inventory.Find(slot) : index.Find(slot), now);
        return stream.ToArray();
    }

    public static byte[] Move(InventoryMoveRequest request, InventoryResult result)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(result.Accepted ? (byte)1 : (byte)0);
        if (!result.Accepted)
        {
            writer.Write(result.Error);
            writer.Write((byte)request.Source.List);
            writer.Write((byte)request.Destination.List);
        }
        else
        {
            // 自动选格回复计划的实际槽位；02372E81 的穿脱分支仍由计划保留原请求顺序。
            var source = result.MoveSource ?? request.Source;
            var destination = result.MoveDestination ?? request.Destination;
            writer.Write((byte)source.List);
            writer.Write(source.Index);
            writer.Write(result.MoveCount);
            writer.Write((byte)destination.List);
            writer.Write(destination.Index);
        }
        return stream.ToArray();
    }

    public static byte[] Sort(InventorySortRequest request, InventoryResult result) => Protobuf(result.Accepted, writer =>
    {
        Scalar(writer, 1, (byte)request.List);
        Scalar(writer, 2, result.Error);
    });

    public static byte[] Delete(InventoryDeleteRequest request, InventoryResult result) => Protobuf(result.Accepted, writer =>
    {
        Scalar(writer, 1, (byte)request.List);
        if (result.Accepted)
        {
            var plan = result.Plan ?? throw new InvalidOperationException("删除应答缺少库存规划");
            var current = result.Receipt?.Index ?? plan.AfterIndex;
            foreach (var item in request.Items)
            {
                using var stream = new MemoryStream();
                using var child = new BinaryWriter(stream);
                Scalar(child, 1, checked((uint)item.Slot));
                // 材料足额消费回本次数量；普通丢弃回归一后的实际删除量。
                var slot = new InventorySlot(request.List,item.Slot);
                int removed = item.Type is 2 or 3 ? (plan.PreserveMaterials ? 0 : item.Count) : (plan.BeforeIndex.Find(slot)?.Core.Count ?? 0) -
                    (current.Find(slot)?.Core.Count ?? 0);
                Scalar(child, 2, checked((uint)removed));
                Scalar(child, 3, item.Type);
                writer.Write((byte)18);
                Varint(writer, checked((uint)stream.Length));
                writer.Write(stream.ToArray());
            }
        }
        Scalar(writer, 3, result.Error);
    });

    static byte[] Protobuf(bool accepted, Action<BinaryWriter> write)
    {
        using var payload = new MemoryStream();
        using var fields = new BinaryWriter(payload);
        write(fields);
        using var body = new MemoryStream();
        using var writer = new BinaryWriter(body);
        writer.Write(accepted ? (byte)1 : (byte)0);
        // 028F8645: ACK18/20失败码在protobuf里，公共分派不再读u16。
        writer.Write(checked((uint)payload.Length));
        writer.Write(payload.ToArray());
        return body.ToArray();
    }
    static void Scalar(BinaryWriter writer, uint field, uint value)
    {
        Varint(writer, field << 3);
        Varint(writer, value);
    }
    static void Varint(BinaryWriter writer, uint value)
    {
        while (value >= 128)
        {
            writer.Write((byte)((value & 127) | 128));
            value >>= 7;
        }
        writer.Write((byte)value);
    }
}
