using System.Buffers.Binary;
using DfoServer.Game.Dungeons;

namespace DfoServer.Network.Builders;

internal static class DungeonCardMembershipPackets
{
    // ACK905子型1由0233D48A读取74B并安装魔王owner；子型4不是静默魔王同步。
    public static byte[] DevilService0(CardMembership membership, long now)
    {
        if (membership.GoldCardUsed is < 0 or > CardMembership.GoldCardDailyLimit)
            throw new InvalidDataException("魔王黄金牌已用次数无效");
        byte[] body = new byte[77];
        body[0] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(1), 1);
        // 74B内有2B前缀及8个9B记录；服务0绝对到期在+6，次数是+10的u8。
        // 其他七项服务不伪造到期或次数，不从客户端上传的快照取得资格。
        if (CardMembership.DevilContractService0Enabled && membership.GoldCardActive(now))
        {
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(9), checked((uint)Math.Min(membership.GoldCardExpires, int.MaxValue)));
            body[13] = checked((byte)membership.GoldCardUsed);
        }
        return body;
    }
}
