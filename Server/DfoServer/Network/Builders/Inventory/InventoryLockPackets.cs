using DfoServer.Game.Inventory.Models;

namespace DfoServer.Network.Builders.Inventory;

internal static class InventoryLockPackets
{
    public static byte[] Ack(InventorySlot slot, InventoryLockOperation operation, InventoryResult result)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(result.Accepted ? (byte)1 : (byte)0);
        if (result.Accepted)
        {
            writer.Write((byte)slot.List);
            writer.Write(slot.Index);
        }
        else
        {
            writer.Write(result.Error); // 公共 ACK 分派先消费 u16 错误码。
            if (operation is InventoryLockOperation.SortLock or InventoryLockOperation.SortUnlock)
                writer.Write((byte)0); // 0233DBAE / 0233DC0B 另消费一个字节。
        }
        if (operation == InventoryLockOperation.EquipmentUnlock)
            writer.Write(0); // 0235B620 成功/失败都消费 i32 剩余秒数；立即解锁为 0。
        return stream.ToArray();
    }

    public static byte[] Locked(IEnumerable<InventorySlot> slots)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        var entries = slots.Distinct().ToArray();
        writer.Write(checked((ushort)entries.Length));
        foreach (var slot in entries)
        {
            writer.Write((byte)slot.List);
            writer.Write(slot.Index);
            writer.Write((byte)1); // 状态 1 无剩余时间字段，状态 2 才跟 i32。
        }
        return stream.ToArray();
    }

    public static byte[] Unlocked(InventorySlot slot) => [(byte)slot.List, (byte)slot.Index, (byte)(slot.Index >> 8)];
}
