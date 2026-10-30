using System.Buffers.Binary;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Network.Builders.Inventory;

internal static class MonsterCardBindPackets
{
    public static byte[] Success(MonsterCardBindReceipt receipt)
    {
        if (receipt.DeletedSlots.Length is < 1 or > 5 || receipt.Rewards.Length is < 1 or > 5)
            throw new InvalidDataException("怪物卡片合成成功回执删除或奖励数量无效");
        byte[] body = new byte[6 + receipt.DeletedSlots.Length * 2 + receipt.Rewards.Length * 11];
        body[0] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(1), checked((ushort)receipt.SynthesizerSlot));
        body[3] = checked((byte)receipt.DeletedSlots.Length);
        int at = 4;
        foreach (short slot in receipt.DeletedSlots)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(at), checked((ushort)slot));
            at += 2;
        }
        body[at++] = checked((byte)receipt.Rewards.Length);
        foreach (var reward in receipt.Rewards)
        {
            if (reward.ItemId <= 0 || reward.Value <= 0)
                throw new InvalidDataException("怪物卡片合成奖励编号或数量无效");
            BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(at), checked((ushort)reward.Slot));
            BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(at + 2), reward.ItemId);
            BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(at + 6), reward.Value);
            body[at + 10] = reward.Extra;
            at += 11;
        }
        // 014409FA 在奖励表后还读取一字节结果标志；具体业务语义未闭合，保留默认关闭。
        body[at++] = 0;
        if (at != body.Length)
            throw new InvalidDataException("怪物卡片合成成功回执宽度变化");
        return body;
    }

    public static byte[] Failure(ushort error = 22)
    {
        if (error is < 4 or > 23)
            throw new InvalidDataException("怪物卡片合成失败码超出客户端分支");
        byte[] body = new byte[3];
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(1), error);
        return body;
    }
}
