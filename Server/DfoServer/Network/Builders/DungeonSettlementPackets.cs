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

    public static byte[] CardRights(bool paid = false)
    {
        byte[] body = new byte[17];
        body[0] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(1), 1);
        for (int index = 1; index < 8; index++)
            BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(1 + index * 2), ushort.MaxValue);
        if (paid) BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(9),1);
        return body;
    }

    public static byte[] CardReward(DungeonClearExperienceReward reward, uint monsterExperience, DungeonCardReward? card,
        DungeonCardOptions? options = null,DungeonCardReward? blackDiamond = null)
    {
        var empty = EmptyCardReward(reward, monsterExperience);
        if (card is null) return empty;
        card.Validate();
        int count = (card.Gold > 0 ? 1 : 0) + (card.Item is null ? 0 : 1);
        int bonusCount = blackDiamond is null ? 0 : (blackDiamond.Gold > 0 ? 1 : 0) + (blackDiamond.Item is null ? 0 : 1);
        byte[] body = new byte[empty.Length + (count+bonusCount) * 8];
        empty.AsSpan(0,156).CopyTo(body);
        int at = 156;
        body[at++] = checked((byte)count);
        if (card.Gold > 0) Pair(0, checked((uint)card.Gold));
        if (card.Item is { } core) Pair(checked((uint)core.ItemId), checked((uint)core.Count));
        at += 7; // 本人之外的七个免费席。
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(at),checked((uint)(options?.Price ?? 0)));
        at += 4 + 8; // 费用及未使用的额外组。
        body[at++] = checked((byte)bonusCount);
        if (blackDiamond is { } bonus)
        {
            if (bonus.Gold > 0) Pair(0,checked((uint)bonus.Gold));
            if (bonus.Item is { } bonusItem) Pair(checked((uint)bonusItem.ItemId),checked((uint)bonusItem.Count));
        }
        at += 7;
        empty.AsSpan(184).CopyTo(body.AsSpan(at));
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
        int count = view.Paid is null ? 0 : (view.Paid.Gold > 0 ? 1 : 0)+(view.Paid.Item is null ? 0 : 1);
        byte[] body = new byte[33+count*8];
        body[0] = 1;
        int at = 1;
        for (int slot = 0; slot < 8; slot++)
        {
            body[at++] = slot == 0 && view.FreeSelected ? (byte)0 : byte.MaxValue;
            body[at++] = slot == 0 && view.Paid is not null ? (byte)0 : byte.MaxValue;
            body[at++] = slot == 0 ? checked((byte)count) : (byte)0;
            if (slot == 0 && view.Paid is { } paid)
            {
                if (paid.Gold > 0) Pair(0,checked((uint)paid.Gold));
                if (paid.Item is { } item) Pair(checked((uint)item.ItemId),checked((uint)item.Count));
            }
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
}
