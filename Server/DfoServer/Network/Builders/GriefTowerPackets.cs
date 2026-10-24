using System.Buffers.Binary;
using DfoServer.Game.Dungeons;

namespace DfoServer.Network.Builders;

internal static class GriefTowerPackets
{
    // 0239EE50：0x34选择悲叹管理器，第二字节经01FC8AC0保存为通关标志。
    public static byte[] ClearState(bool cleared) => [0x34,cleared ? (byte)1 : (byte)0];

    // 匹配01FCB980：耗时u32、已通关层u16、条数u8、逐项物品u32与数量u32。
    public static byte[] ClearReward(uint milliseconds,int clearedLayer,GriefTowerReward reward)
    {
        if (clearedLayer is <1 or >100 || reward.Items.IsDefault || reward.Items.Length>10 ||
            reward.Items.Any(item=>item.ItemId<=0 || item.Count<=0))
            throw new InvalidDataException("悲叹通关通知的楼层或物品非法");
        byte[] body=new byte[7+reward.Items.Length*8];
        BinaryPrimitives.WriteUInt32LittleEndian(body,milliseconds);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(4),checked((ushort)clearedLayer));
        body[6]=checked((byte)reward.Items.Length);
        for (int i=0;i<reward.Items.Length;i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(7+i*8),checked((uint)reward.Items[i].ItemId));
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(11+i*8),checked((uint)reward.Items[i].Count));
        }
        return body;
    }
}
