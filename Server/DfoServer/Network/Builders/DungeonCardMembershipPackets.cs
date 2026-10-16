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
        // 期限和次数均来自已提交账本，不从客户端上传的快照取得资格。
        if (CardMembership.DevilContractService0Enabled && membership.GoldCardActive(now))
        {
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(9), checked((uint)Math.Min(membership.GoldCardExpires, int.MaxValue)));
            body[13] = checked((byte)membership.GoldCardUsed);
        }
        if (!membership.DevilServiceExpiries.IsDefaultOrEmpty)
        {
            if (membership.DevilServiceExpiries.Length != 8) throw new InvalidDataException("魔王子服务期限必须为八项");
            for (int slot = 1; slot < 8; slot++)
                if (membership.DevilServiceExpiries[slot] > now)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(9 + slot * 9),
                        checked((uint)Math.Min(membership.DevilServiceExpiries[slot], int.MaxValue)));
                    if (!membership.DevilServiceUsed.IsDefaultOrEmpty)
                    {
                        if (membership.DevilServiceUsed.Length != 8) throw new InvalidDataException("魔王子服务次数必须为八项");
                        body[13 + slot * 9] = membership.DevilServiceUsed[slot];
                    }
                }
        }
        return body;
    }
}
