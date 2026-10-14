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

    public static byte[] EmptyCardReward(DungeonClearExperienceReward reward, uint monsterExperience,
        uint monsterGrowthContractExperience = 0)
    {
        // 所有动态名单和翻牌奖励为空。固定经验字段来自已经提交的同一次结算。
        // 本版固定尾部含八个独立bool，不能套用其他版本的210B正文。
        byte[] body = new byte[218];
        BinaryPrimitives.WriteUInt32LittleEndian(body, reward.BaseExperience);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), reward.RankExperience);
        // 02393C80分别投影到结果页两组“成长之契约”栏；不是组队或评分经验。
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(49), reward.GrowthContractExperience);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(85), monsterGrowthContractExperience);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(194), monsterExperience);
        return body;
    }

    public static byte[] CardRights(short fatigue = 1)
    {
        byte[] body = new byte[17];
        body[0] = 1;
        BinaryPrimitives.WriteInt16LittleEndian(body.AsSpan(1), fatigue);
        for (int index = 1; index < 8; index++)
            BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(1 + index * 2), ushort.MaxValue);
        return body;
    }

    public static byte[] CardReward(DungeonClearExperienceReward reward, uint monsterExperience, DungeonCardReward? card,
        DungeonCardOptions? options = null,DungeonCardReward? blackDiamond = null,
        uint monsterGrowthContractExperience = 0)
    {
        var empty = EmptyCardReward(reward, monsterExperience, monsterGrowthContractExperience);
        if (card is null) return empty;
        card.Validate();
        var free = Display(card);
        var bonusItems = blackDiamond is null ? [] : Display(blackDiamond);
        int count = free.Count;
        int bonusCount = bonusItems.Count;
        byte[] body = new byte[empty.Length + (count+bonusCount) * 8];
        empty.AsSpan(0,156).CopyTo(body);
        int at = 156;
        body[at++] = checked((byte)count);
        foreach (var item in free) Pair(item.Id, item.Count);
        at += 7; // 本人之外的七个免费席。
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(at),checked((uint)(options?.Price ?? 0)));
        at += 4 + 8; // 费用及未使用的额外组。
        body[at++] = checked((byte)bonusCount);
        foreach (var item in bonusItems) Pair(item.Id, item.Count);
        at += 7;
        empty.AsSpan(184).CopyTo(body.AsSpan(at));
        // 尾段先读全局u32和弃用u32，再读双倍位；它不是结果页显示开关。
        body[at + 8] = options?.DoublePaidItems == true ? (byte)1 : (byte)0;
        // 回归消费者仅有静态分析，八个成员标志保持零，不借旧本地回执赋予资格。
        return body;

        void Pair(uint item, uint amount)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(at), item);
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(at + 4), amount);
            at += 8;
        }
    }

    public static byte[] FreeCardSelected()
    {
        byte[] body = new byte[33];
        body[0] = 1;
        for (int index = 0; index < 8; index++)
        {
            body[1 + index * 4] = byte.MaxValue;
            body[2 + index * 4] = byte.MaxValue;
        }
        body[1] = 0;
        // 免费牌从NOTI35预载，ACK71只揭示本人免费席；未开放的付费席保持FF。
        return body;
    }

    public static byte[] CardSelected(DungeonCardView view)
    {
        if (view.Paid is null && view.FreeSelected) return FreeCardSelected();
        var paidItems = view.Paid is null ? [] : Display(view.Paid);
        int count = paidItems.Count;
        byte[] body = new byte[33+count*8];
        body[0] = 1;
        int at = 1;
        for (int slot = 0; slot < 8; slot++)
        {
            body[at++] = slot == 0 && view.FreeSelected ? (byte)0 : byte.MaxValue;
            body[at++] = slot == 0 && view.Paid is not null ? (byte)0 : byte.MaxValue;
            body[at++] = slot == 0 ? checked((byte)count) : (byte)0;
            if (slot == 0)
                foreach (var item in paidItems) Pair(item.Id, item.Count);
            body[at++] = 0;
        }
        return body;
        void Pair(uint item,uint amount)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(at),item);
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(at+4),amount);
            at += 8;
        }
    }

    public static byte[] ResultCommand(byte state, byte option) => [1, state, option];

    // 039F8B40/039F63E0 固定把首项作为金币，空装备也必须保留金币占位。
    static List<(uint Id, uint Count)> Display(DungeonCardReward reward)
    {
        reward.Validate();
        var result = new List<(uint, uint)> { (0, checked((uint)reward.Gold)) };
        result.AddRange(reward.Items().GroupBy(core => core.ItemId).Select(group =>
            (checked((uint)group.Key), checked((uint)group.Sum(core => (long)core.Count)))));
        return result;
    }
}
