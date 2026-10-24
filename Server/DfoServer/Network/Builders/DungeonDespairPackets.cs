using System.Buffers.Binary;
using DfoServer.Game.Dungeons;

namespace DfoServer.Network.Builders;

internal static class DungeonDespairPackets
{
    // 01FCA990读取三个字符串、三字节角色信息、pvp:u32、26个装备u32、宠物u32和尾字节。
    // 非零层且姓名为空时01FCAC48→01FC9800从本机当前角色填充整份资料；使用客户端现有单人回退。
    public static byte[] LocalPlayerOpponent(int layer)
    {
        if (layer is < 10 or > 90 || layer % 10 != 0) throw new ArgumentOutOfRangeException(nameof(layer));
        byte[] body = new byte[129];
        body[0] = checked((byte)layer);
        return body;
    }

    // V31 01FCB4B0：u32计时、u16层、u8数量，随后完整的(itemId:u32,addInfo:u32)列表。
    public static byte[] Clear(uint milliseconds, DungeonDespairRewardPlan reward)
    {
        reward.Validate();
        byte[] body = new byte[7 + reward.Cards.Length * 8];
        BinaryPrimitives.WriteUInt32LittleEndian(body, milliseconds);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(4), checked((ushort)reward.Layer));
        body[6] = checked((byte)reward.Cards.Length);
        for (int at = 0; at < reward.Cards.Length; at++)
        {
            var item = reward.Cards[at];
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(7 + at * 8), item is null ? uint.MaxValue : checked((uint)item.Core.ItemId));
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(11 + at * 8), item is null ? 0u : checked((uint)item.Core.Value));
        }
        return body;
    }
}
