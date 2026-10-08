using System.Buffers.Binary;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Dungeons;

namespace DfoServer.Network.Builders;

internal static class DungeonSettlementPackets
{
    public static byte[] BossAccepted(ushort bossKey) => [1, 1, (byte)bossKey, (byte)(bossKey >> 8)];

    public static byte[] PlayResult(ushort actorId, DungeonSettlementPresentation presentation)
    {
        byte[] body = new byte[16];
        body[0] = presentation.RankGrade;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(1), presentation.ClearTimeMilliseconds);
        body[6] = presentation.RankPoint;
        body[7] = 1; // 正式ClearFact仅在本次完整路线清完后成立。
        body[8] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(9), actorId);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(11), presentation.ClearTimeMilliseconds);
        return body;
    }

    public static byte[] EmptyCardReward(DungeonClearExperienceReward reward, uint monsterExperience)
    {
        // 所有动态名单和翻牌奖励为空。固定经验字段来自已经提交的同一次结算。
        // 本版固定尾部含八个独立bool，不能套用其他版本的210B正文。
        byte[] body = new byte[218];
        BinaryPrimitives.WriteUInt32LittleEndian(body, reward.BaseExperience);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), reward.RankExperience);
        body[192] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(194), monsterExperience);
        return body;
    }

    public static byte[] CardRights()
    {
        byte[] body = new byte[17];
        body[0] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(1), 1);
        for (int index = 1; index < 8; index++)
            BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(1 + index * 2), ushort.MaxValue);
        return body;
    }

    public static byte[] EmptyCardSelected()
    {
        byte[] body = new byte[33];
        body[0] = 1;
        for (int index = 0; index < 8; index++)
        {
            body[1 + index * 4] = byte.MaxValue;
            body[2 + index * 4] = byte.MaxValue;
        }
        body[1] = 0;
        body[2] = 0;
        return body;
    }

    public static byte[] ResultCommand(byte state, byte option) => [1, state, option];
}
