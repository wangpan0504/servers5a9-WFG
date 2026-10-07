using DfoServer.Game.Inventory.Models;

namespace DfoServer.Network.Builders.Inventory;

// Client NOTI13/14 的普通物品定长部分为123B；附加资源块只属于已核实的特殊物品分支。
internal static class ItemListProtocolWriter
{
    public const int EntrySize = 123;
    public static void Write(BinaryWriter writer, InventorySlot slot, InventoryItem? item, long utcSeconds)
    {
        var core = item?.Core;
        if (item?.Avatar is not null)
            core = core! with { Value = item.RemainingSeconds(utcSeconds) };
        else if (item?.Creature is not null)
            core = core! with { Marker16 = item.RemainingSeconds(utcSeconds) };
        Write(writer, slot.Index, core);
        // list=1 的清空条目也消费两个 RAW；穿戴表仅有实际时装时消费。
        if (slot.List == InventoryListType.Avatar || item?.Avatar is not null)
        {
            WriteAvatarSockets(writer, item?.Avatar);
            WriteAvatarColors(writer, item?.Avatar);
        }
    }

    public static void WriteAvatarSockets(BinaryWriter writer, AvatarDetail? detail)
    {
        writer.Write(30);
        detail ??= new();
        foreach (var socket in new[] { detail.Socket0, detail.Socket1, detail.Socket2, detail.Socket3, detail.Socket4 })
        {
            writer.Write(socket.Type);
            writer.Write(socket.EmblemId);
        }
    }

    public static void WriteAvatarColors(BinaryWriter writer, AvatarDetail? detail)
    {
        writer.Write(4);
        writer.Write(detail?.Color1 ?? 0);
        writer.Write(detail?.Color2 ?? 0);
    }
    public static void Write(BinaryWriter writer, short slot, ItemCore? core)
    {
        long begin = writer.BaseStream.Position;
        writer.Write(slot);
        if (core is null)
        {
            writer.Write(-1); // 023BF0D7: -1 表示清空该槽。
            writer.Write(new byte[EntrySize - 6]);
            return;
        }
        core.Validate();
        writer.Write(core.ItemId);
        writer.Write(core.Value);
        writer.Write(core.Attr);
        writer.Write(core.Durability);
        writer.Write(core.SealFlag);
        writer.Write(core.EnchantCardId);
        writer.Write(core.EnchantUpgrade);
        writer.Write(core.AmplifyType);
        writer.Write(core.AmplifyValue);
        writer.Write(core.Marker16);
        writer.Write(core.Extension26);
        writer.Write(core.Extension27);
        writer.Write(core.Extension31);
        writer.Write(core.Extension35);
        var chronicle = Chronicles(core);
        writer.Write((byte)chronicle.Length);
        writer.Write(core.Chronicle0.Id);
        writer.Write(core.Chronicle1.Id);
        writer.Write(core.Chronicle0.Job);
        writer.Write(core.Chronicle1.Job);
        writer.Write(core.Chronicle0.GrowType);
        writer.Write(core.Chronicle1.GrowType);
        writer.Write(core.Chronicle0.EquipmentType);
        writer.Write(core.Chronicle1.EquipmentType);
        writer.Write(core.Chronicle0.Number);
        writer.Write(core.Chronicle1.Number);
        writer.Write(core.ExpireTime);
        writer.Write(core.EmblemSocketCount);
        writer.Write(core.Emblem1);
        writer.Write(core.Emblem2);
        writer.Write(core.Rune);
        var random = new[] { core.Random0, core.Random1, core.Random2 };
        writer.Write((byte)random.Count(option => option.Type != 0));
        foreach (var option in random)
            writer.Write(option.Type);
        foreach (var option in random)
            writer.Write(option.Value1);
        foreach (var option in random)
            writer.Write(option.Value2);
        writer.Write(core.RandomState);
        writer.Write(core.RandomChangedIndex);
        writer.Write(core.RandomChangeState);
        writer.Write(core.RandomChange.Type);
        writer.Write(core.RandomChange.Value1);
        writer.Write(core.RandomChange.Value2);
        WriteTail(writer, core);
        writer.Write(core.Extension97);
        writer.Write(core.GuardianGemKeys);
        writer.Write(core.Extension109);
        writer.Write(core.Extension110);
        writer.Write(core.Extension114.Value); // 03DAAB70 初始化 u32 后接 u8。
        writer.Write(core.Extension114.Flag);
        writer.Write(core.Extension119);
        writer.Write(core.Extension120);
        writer.Write(core.Extension121);
        if (writer.BaseStream.Position - begin != EntrySize)
            throw new InvalidDataException("Client物品条目宽度变化");
    }

    public static ChronicleOption[] Chronicles(ItemCore core) => new[] { core.Chronicle0, core.Chronicle1 }.Where(option => option.Id != 0).ToArray();
    public static void WriteTail(BinaryWriter writer, ItemCore core)
    {
        writer.Write(core.GenuineUpgrade);
        writer.Write(core.EmancipateLevel);
        writer.Write(core.TradeRestriction);
        writer.Write(core.Extension90);
        writer.Write(core.Extension92);
        writer.Write(core.Extension93);
        writer.Write(core.RemainUseCount);
        writer.Write(core.SortLockFlag);
        writer.Write(core.EquipmentLockId);
    }
}
