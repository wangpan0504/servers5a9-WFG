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
    // 单人仅成员0参与，额外分量数显式为0；没有普通物品的recipient/slot/tail字段。
    public static byte[] SinglePlayerGoldNotification(uint groundObjectKey, ushort pickerKey, uint amount)
    {
        if (amount == 0)
            throw new ArgumentOutOfRangeException(nameof(amount), "金币拾取通知必须有实际已提交增量");
        byte[] body = new byte[47];
        BinaryPrimitives.WriteUInt32LittleEndian(body, groundObjectKey);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(4), pickerKey);
        body[6] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(7), amount);
        // offset11为本人成员附加分量数量0，其余七成员为完整的标记0/金额0。
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
