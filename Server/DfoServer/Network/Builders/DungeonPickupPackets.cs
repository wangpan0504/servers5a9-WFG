using System.Buffers.Binary;

namespace DfoServer.Network.Builders;

internal static class DungeonPickupPackets
{
    // 普通NOTI39由023C8E00读取19B，并使用既有地面物品对象入包；不适用于资源0/1199特殊分支。
    // flags8与tailFlag的业务来源尚未恢复，调用方必须完整提供，不在Builder补零或推断含义。
    public static byte[] OrdinaryNotification(uint groundObjectKey, ushort firstPickerKey, ReadOnlySpan<byte> flags8,
        ushort recipientMemberKey, ushort destinationSlot, byte tailFlag)
    {
        if (flags8.Length != 8)
            throw new ArgumentException("普通拾取通知必须显式提供完整8字节flags", nameof(flags8));

        byte[] body = new byte[19];
        BinaryPrimitives.WriteUInt32LittleEndian(body, groundObjectKey);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(4), firstPickerKey);
        flags8.CopyTo(body.AsSpan(6, 8));
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(14), recipientMemberKey);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(16), destinationSlot);
        body[18] = tailFlag;
        return body;
    }

    // 资源0的金币分支：023C97F6..023C9A78读取八成员，每成员必须有u8标记和u32金额。
    // 单人仅成员0参与；零附加金币也保留扩展段，使消费者携带地面对象触发拾取表现。
    public static byte[] SinglePlayerGoldNotification(uint groundObjectKey, ushort pickerKey, uint amount) =>
        IndependentGoldNotification(groundObjectKey, pickerKey, amount, 0);

    // 独立金币仅激活领取者的实际队伍槽位，其余成员金额为零，不向队员分摊。
    public static byte[] IndependentGoldNotification(uint groundObjectKey, ushort pickerKey, uint amount, byte memberSlot)
    {
        if (amount == 0 || memberSlot >= 8)
            throw new ArgumentOutOfRangeException(nameof(amount), "金币拾取通知必须有实际已提交增量");
        byte[] body = new byte[55];
        BinaryPrimitives.WriteUInt32LittleEndian(body, groundObjectKey);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(4), pickerKey);
        int at = 6 + memberSlot * 5;
        body[at] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(at + 1), amount);
        body[at + 5] = 1;
        // 扩展中的附加金币与公会基金均为0；其余七成员为完整标记0/金额0。
        return body;
    }

    // ACK43成功只消费公共success，不代替NOTI39入包；没有额外NOTI42空包。
    public static byte[] Accepted() => [1];

    // 错误码由未来业务按本版合同选择；这里仅编码公共0+u16，不映射背包内部错误。
    public static byte[] Rejected(ushort error)
    {
        byte[] body = new byte[3];
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(1), error);
        return body;
    }
}
