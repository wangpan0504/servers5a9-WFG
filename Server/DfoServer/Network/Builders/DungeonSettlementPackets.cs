using System.Buffers.Binary;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;

namespace DfoServer.Network.Builders;

internal static class DungeonSettlementPackets
{
    // NOTI255在023802F1/0238031F读u32和u8；时间由客户端除10，尾标志不触发附加奖励。
    public static byte[] PracticeCleared(uint elapsedMilliseconds)
    {
        byte[] body = new byte[5];
        BinaryPrimitives.WriteUInt32LittleEndian(body, elapsedMilliseconds);
        return body;
    }

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

    // 魂·黄龙/青龙大会（#259，实验性，实机未验证）：NOTI388 TOURNAMENT_CLEAR_REWARD。
    // 依据参考 Go 项目 dungeon_tournament_rewards.go 对 DNF.exe 的逆向注释：大会副本通关后客户端等待 388（而非普通 34/35）才显示宝箱并发 CMD457/458。
    // 格式：完成轮数 u8、经验 u32、0 u8，然后 2 行，每行 count u8 + count×(itemId i32、数量 i32、u16 0)；itemId=-1 表示空卡。
    public const int TournamentCardRows = 2;
    public const int TournamentCardsPerRow = 2;
    public const byte TournamentUnselected = 255;
    public static byte[] TournamentReward(byte completedRounds, uint experience)
    {
        var body = new List<byte> { completedRounds };
        body.AddRange(BitConverter.GetBytes(experience));
        body.Add(0);
        for (int row = 0; row < TournamentCardRows; row++)
        {
            body.Add(TournamentCardsPerRow);
            for (int card = 0; card < TournamentCardsPerRow; card++)
            {
                body.AddRange(BitConverter.GetBytes(-1)); // 空卡：本最小实现不发放卡牌奖励
                body.AddRange(BitConverter.GetBytes(0));
                body.AddRange(new byte[2]);
            }
        }
        return [.. body];
    }

    // CMD457 回复：1 + 每行 4 个队伍位（本人所在位=1，其余 0xFF）。单人只占位 0。
    public static byte[] TournamentSelectState(byte slot)
    {
        byte[] body = new byte[9];
        body[0] = 1;
        for (int row = 0; row < TournamentCardRows; row++)
        {
            for (int member = 0; member < 4; member++) body[1 + row * 4 + member] = byte.MaxValue;
            body[1 + row * 4 + slot] = 1;
        }
        return body;
    }

    // CMD458 回复：1 + 每行 count + count×归属位（未选 0xFF）。selected[row] 为该行已选卡序号或 255。
    public static byte[] TournamentSelection(byte slot, ReadOnlySpan<byte> selected)
    {
        var body = new List<byte> { 1 };
        for (int row = 0; row < TournamentCardRows; row++)
        {
            body.Add(TournamentCardsPerRow);
            for (int card = 0; card < TournamentCardsPerRow; card++)
                body.Add(selected[row] == card ? slot : TournamentUnselected);
        }
        return [.. body];
    }

    // 结果页加成分项按实机哨兵探针（2026-10-22）逐行投影：基础块+8为通关组队、+12为扮装；
    // 22个加成槽中1秘药、2黑钻、3网吧、5宠物、8通关成长契约、9通关疲劳燃烧、11通关公会、
    // 15杀怪疲劳燃烧、16疲劳Buff、17杀怪成长契约、19装备、20杀怪公会；槽7与14由客户端合并成
    // “归来的勇士”，槽0/4/6/10/12/13/18/21当前无对应到账来源。显示值只取本次已到账的同一份分项。
    public static byte[] EmptyCardReward(DungeonClearExperienceReward reward, uint monsterExperience,
        uint monsterGrowthContractExperience = 0, uint monsterEquipmentExperience = 0,
        IReadOnlyList<DungeonObjectExperience>? objectExperience = null, uint monsterBossExperience = 0)
    {
        // 所有翻牌奖励为空。固定经验字段和杀怪明细来自已经提交的同一次结算。
        // 本版固定尾部含八个独立bool，不能套用其他版本的210B正文。
        // 对象明细是变长列表（4B 条数 + 每条 8B），插在固定头和卡牌区之间；其后所有偏移整体平移 8N。
        var entries = objectExperience ?? [];
        int shift = checked(entries.Count * 8);
        byte[] body = new byte[218 + shift];
        // 冒险团加成没有独立字段，按基础通关经验合并；拆分顺序保证两项之和不超过已到账经验。
        BinaryPrimitives.WriteUInt32LittleEndian(body, checked(reward.BaseExperience + reward.AdventureExperience));
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), reward.RankExperience);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8), Part(reward, ExperienceBonusSource.Party));
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(12), Part(reward, ExperienceBonusSource.Avatar));
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(21),
            checked(Part(reward, ExperienceBonusSource.Potion) + Part(reward, ExperienceBonusSource.QuestPotion)));
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(25), Part(reward, ExperienceBonusSource.BlackDiamond));
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(29), Part(reward, ExperienceBonusSource.InternetCafe));
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(37), Part(reward, ExperienceBonusSource.Creature));
        // 02393C80分别投影到结果页两组“成长之契约”栏；不是组队或评分经验。
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(49), Part(reward, ExperienceBonusSource.GrowthContract));
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(53), Part(reward, ExperienceBonusSource.FatigueBurning));
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(61), Part(reward, ExperienceBonusSource.Guild));
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(85), monsterGrowthContractExperience);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(93), monsterEquipmentExperience);
        // 杀怪经验明细：u32 条数 + 每条（对象键、基础经验），客户端按对象键归并到怪物类型后显示各自经验。
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(151), checked((uint)entries.Count));
        for (int index = 0; index < entries.Count; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(155 + index * 8), entries[index].ObjectKey);
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(159 + index * 8), entries[index].Experience);
        }
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(194 + shift), monsterExperience);
        // 评分quartet第4槽（147）在实机探针中投影到“领主”行；精英/超级精英行走151/155的对象明细。
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(147), monsterBossExperience);
        return body;
    }

    // 未冻结分项的历史回执只有成长契约与秘药两项，按旧口径展示；其余来源保持0，不借旧回执伪造其他加成。
    static uint Part(DungeonClearExperienceReward reward, ExperienceBonusSource source)
    {
        if (reward.BonusParts.IsDefault)
            return source switch
            {
                ExperienceBonusSource.GrowthContract => reward.GrowthContractExperience,
                ExperienceBonusSource.Potion => reward.PotionExperience,
                _ => 0
            };
        return checked((uint)reward.BonusParts.Aggregate(0UL,
            (total, part) => part.Source == source ? total + part.Amount : total));
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
        uint monsterGrowthContractExperience = 0, uint monsterEquipmentExperience = 0,
        IReadOnlyList<DungeonObjectExperience>? objectExperience = null, uint monsterBossExperience = 0)
    {
        var empty = EmptyCardReward(reward, monsterExperience, monsterGrowthContractExperience, monsterEquipmentExperience,
            objectExperience, monsterBossExperience);
        if (card is null) return empty;
        card.Validate();
        var free = Display(card);
        var bonusItems = blackDiamond is null ? [] : Display(blackDiamond);
        int count = free.Count;
        int bonusCount = bonusItems.Count;
        // 对象明细在前，卡牌区和尾部跟着同样的 8N 平移。
        int shift = checked((objectExperience?.Count ?? 0) * 8);
        byte[] body = new byte[empty.Length + (count+bonusCount) * 8];
        empty.AsSpan(0,156+shift).CopyTo(body);
        int at = 156+shift;
        body[at++] = checked((byte)count);
        foreach (var item in free) Pair(item.Id, item.Count);
        at += 7; // 本人之外的七个免费席。
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(at),checked((uint)(options?.Price ?? 0)));
        at += 4 + 8; // 费用及未使用的额外组。
        body[at++] = checked((byte)bonusCount);
        foreach (var item in bonusItems) Pair(item.Id, item.Count);
        at += 7;
        empty.AsSpan(184+shift).CopyTo(body.AsSpan(at));
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
