using System.Buffers.Binary;
using DfoServer.Game.Dungeons;

namespace DfoServer.Network.Builders;

internal static class DungeonEnterCountPackets
{
    public static byte[] Ancient(uint energy)
    {
        byte[] body = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(body, energy);
        return body;
    }
    // 95客户端012450F0：基本已入、额外剩余、当周隐藏最高奖励剩余次数。
    public static byte[] Dimension(uint basicUsed, uint extraRemaining, byte hiddenRemaining)
    {
        byte[] body = new byte[9];
        BinaryPrimitives.WriteUInt32LittleEndian(body, basicUsed);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), extraRemaining);
        body[8] = hiddenRemaining;
        return body;
    }
    // 023A0200逐次读取u32、u8、u8；一包只有一个地下城，没有条数或尾随字段。
    public static byte[] Remaining(DungeonEnterCount count)
    {
        if (count.Dungeon == 0) throw new InvalidDataException("副本次数通知缺少地下城编号");
        return Info(count.Dungeon, count.Daily, count.Weekly);
    }

    // 023CD590 注册 0x226 → 023A0200；两字节分别写角色 +0x6C68 / +0x6C7C。
    public static byte[] Info(uint dungeon, byte remaining, byte second)
    {
        var body = new byte[6];
        BinaryPrimitives.WriteUInt32LittleEndian(body, dungeon);
        body[4] = remaining;
        body[5] = second;
        return body;
    }
}
