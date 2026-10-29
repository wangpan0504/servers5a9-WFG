using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;

namespace DfoServer.Network.Builders.Inventory;

// Client NOTI13/14 的普通物品定长部分为123B；附加资源块只属于已核实的特殊物品分支。
internal static class ItemListProtocolWriter
{
    public const int EntrySize = 123;
    // 地面金币沿相同123B普通记录，itemId=0与金币增量不进入实体ItemCore业务模型。
    public static void WriteGroundGold(BinaryWriter writer, short recordSlot, int amount)
    {
        if (amount <= 0)
            throw new InvalidDataException("地面金币记录必须携带正增量");
        writer.Write(recordSlot);
        writer.Write(0);
        writer.Write(amount);
        writer.Write(new byte[EntrySize - 10]);
    }
    public static void Write(BinaryWriter writer, InventorySlot slot, InventoryItem? item, long utcSeconds)
    {
        var core = item?.Core;
        // 排序锁仍随实例保存，穿戴栏不参与整理，也不展示背包的蓝锁。
        if (slot.List == InventoryListType.Equipment && core is { SortLockFlag: not 0 })
            core = core with { SortLockFlag = 0 };
        if (item?.Avatar is not null)
            core = core! with { Value = item.RemainingSeconds(utcSeconds) };
        else if (item?.Creature is not null)
            core = core! with { Marker16 = item.RemainingSeconds(utcSeconds) };
        else if (item?.Core.IsRental == true)
            // 租赁详情消费者读取 +0x38 为剩余秒；S2C988 同样投影剩余秒，数据库保留 Unix 绝对截止。
            core = core! with { ExpireTime = item.RemainingSeconds(utcSeconds) };
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
            // 02973D10 仅将 0xFFEF 识别为多色孔；模板与既有存档的掩码239不能原样下发。
            writer.Write(socket.Type == 239 ? (ushort)0xFFEF : socket.Type);
            writer.Write(socket.EmblemId);
        }
    }

    public static void WriteAvatarColors(BinaryWriter writer, AvatarDetail? detail)
    {
        writer.Write(4);
        writer.Write(detail?.Color1 ?? 0);
        writer.Write(detail?.Color2 ?? 0);
    }

    public static void WriteAmplification(BinaryWriter writer, ItemCore? core)
    {
        byte type = core?.AmplifyType ?? 0;
        bool unidentified = (type & 0x80) != 0;
        // 存档保留隐藏的类型/数值；客户端只识别独立的128标记，不能把0x81–0x84当作属性索引。
        writer.Write(unidentified ? (byte)0x80 : type);
        writer.Write(type == 0 || unidentified ? (ushort)0 : core!.AmplifyValue);
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
        WriteAmplification(writer, core);
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
        writer.Write(ItemExpiration.ForClient(core.ExpireTime));
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
