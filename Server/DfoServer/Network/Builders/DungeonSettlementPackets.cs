using System.Buffers.Binary;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;

namespace DfoServer.Network.Builders;

internal enum DungeonResultOption : byte
{
    Retry = 0, // dstr 479: 再次挑战地下城；dstr 48373: 进入下一个地下城
    OtherDungeon = 1, // dstr 480: 请选择其他地下城
    ReturnTown = 2, // dstr 481: 返回城镇；dstr 46658: 返回[冒险者的游戏]
    ContinuationTail = 3 // 已记录的连图尾包，没有独立文案。
}

// ACK72失败提示与ACK16错误码不是同一枚举；本版失败分支仅133有独立显示。
internal enum DungeonResultClientError : ushort
{
    SecretShopPending = 133 // dstr 25053: 有队员正在使用神秘商店， 请稍后再进行操作。
}

internal enum DungeonRetryAvailability : byte { Unavailable = 0, Available = 9 }

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

    // NOTI261读取u8写入结算页资格；9开放再挑战，0保留禁用状态，不负责切换场景。
    public static byte[] RetryEligibility(bool available) => [(byte)(available ? DungeonRetryAvailability.Available : DungeonRetryAvailability.Unavailable)];

    // ARAD 039EFF80消费NOTI615：flag:u8(1) + buffId:u32，按variousbufflist.etc将月轮山底部Buff牌翻面。
    public static byte[] ClearPartyBuffCard(int buffId)
    {
        byte[] body = new byte[5];
        body[0] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(1), checked((uint)buffId));
        return body;
    }

    public static byte[] PlayResult(ushort actorId, DungeonSettlementPresentation presentation, IReadOnlyList<ushort>? members = null)
    {
        if (members is null || members.Count == 0) members = [actorId];
        byte[] body = new byte[9 + 7 * members.Count];
        body[0] = presentation.RankGrade;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(1), presentation.ClearTimeMilliseconds);
        body[6] = presentation.RankPoint;
        body[7] = 1; // 正式ClearFact仅在本次完整路线清完后成立。
        body[8] = checked((byte)members.Count);
        for (int index = 0; index < members.Count; index++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(9 + 7 * index), members[index]);
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(11 + 7 * index), presentation.ClearTimeMilliseconds);
        }
        return body;
    }

    // ARAD 023807C0：轮数、经验、附加条目数，再读普通/冠军两组；0为金币、-1为空牌。
    public const int TournamentCardRows = 2;
    public const int TournamentCardsPerRow = 2;
    public const byte TournamentUnselected = 255;
    public static byte[] TournamentReward(byte completedRounds, uint experience, DungeonTournamentReward rewards)
    {
        var body = new List<byte> { completedRounds };
        body.AddRange(BitConverter.GetBytes(experience));
        body.Add(0);
        for (int row = 0; row < TournamentCardRows; row++)
        {
            var cards = rewards.Row(row);
            body.Add(checked((byte)cards.Length));
            foreach (var card in cards)
            {
                body.AddRange(BitConverter.GetBytes(card.Item?.ItemId ?? (card.Gold > 0 ? 0 : -1)));
                body.AddRange(BitConverter.GetBytes(card.Item?.Count ?? card.Gold));
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
    public static byte[] TournamentSelection(byte slot, DungeonTournamentReward rewards, ReadOnlySpan<byte> selected)
    {
        var body = new List<byte> { 1 };
        for (int row = 0; row < TournamentCardRows; row++)
        {
            int count = rewards.Row(row).Length;
            body.Add(checked((byte)count));
            for (int card = 0; card < count; card++)
                body.Add(selected[row] == card ? slot : TournamentUnselected);
        }
        return [.. body];
    }

    // 结果页加成分项按实机哨兵探针及匹配原码逐行投影：基础块+8为通关组队、+12为扮装；
    // 22个加成槽中1秘药、2黑钻、3网吧、5宠物、6冒险团、8通关成长契约、9通关疲劳燃烧、11通关公会、
    // 12杀怪组队、15杀怪疲劳燃烧、16疲劳Buff、17杀怪成长契约、19装备、20杀怪公会；槽7与14由客户端合并成
    // “归来的勇士”，槽0/4/10/13/18/21当前无对应到账来源。幸运角色和队伍奖励另取+123/+131。
    // 显示值只取本次已到账的同一份分项。
    public static byte[] EmptyCardReward(DungeonClearExperienceReward reward, uint monsterExperience,
        uint monsterGrowthContractExperience = 0, uint monsterEquipmentExperience = 0,
        IReadOnlyList<DungeonObjectExperience>? objectExperience = null, uint monsterBossExperience = 0,
        uint monsterFatigueBurningExperience = 0, uint monsterGuildExperience = 0, uint monsterFatigueBuffExperience = 0,
        uint monsterPartyExperience = 0)
    {
        // 所有翻牌奖励为空。固定经验字段和杀怪明细来自已经提交的同一次结算。
        // 本版固定尾部含八个独立bool，不能套用其他版本的210B正文。
        // 对象明细是变长列表（4B 条数 + 每条 8B），插在固定头和卡牌区之间；其后所有偏移整体平移 8N。
        var entries = objectExperience ?? [];
        int shift = checked(entries.Count * 8);
        byte[] body = new byte[218 + shift];
        BinaryPrimitives.WriteUInt32LittleEndian(body, reward.BaseExperience);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), reward.RankExperience);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8), Part(reward, ExperienceBonusSource.Party));
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(12), Part(reward, ExperienceBonusSource.Avatar));
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(21),
            checked(Part(reward, ExperienceBonusSource.Potion) + Part(reward, ExperienceBonusSource.QuestPotion)));
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(25), Part(reward, ExperienceBonusSource.BlackDiamond));
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(29), Part(reward, ExperienceBonusSource.InternetCafe));
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(37), Part(reward, ExperienceBonusSource.Creature));
        // 023944C9将独立槽6交039DF580，039E4E18以C007“冒险团奖励”命名该行。
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(41), reward.AdventureExperience);
        // 02393C80分别投影到结果页两组“成长之契约”栏；不是组队或评分经验。
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(49), Part(reward, ExperienceBonusSource.GrowthContract));
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(53), Part(reward, ExperienceBonusSource.FatigueBurning));
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(61), Part(reward, ExperienceBonusSource.Guild));
        // 0239408E读槽12，0239443A写杀怪数组索引0；039E4B85以C004“组队奖励”命名该行。
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(65), monsterPartyExperience);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(77), monsterFatigueBurningExperience);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(81), monsterFatigueBuffExperience);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(85), monsterGrowthContractExperience);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(93), monsterEquipmentExperience);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(97), monsterGuildExperience);
        // 02394367读取+123后经02394529写结果页索引15（73485幸运角色），+131写索引16（73486队伍奖励）。
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(123), Part(reward, ExperienceBonusSource.LuckyCharacter));
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(131), Part(reward, ExperienceBonusSource.LuckyParty));
        // 杀怪经验明细：u32 条数 + 每条（对象键、基础经验），客户端按对象键归并到怪物类型后显示各自经验。
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(151), checked((uint)entries.Count));
        for (int index = 0; index < entries.Count; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(155 + index * 8), entries[index].ObjectKey);
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(159 + index * 8), entries[index].Experience);
        }
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(194 + shift), monsterExperience);
        // 02394CC8读取尾部后，02394CDB再次写通关C6，覆盖前面的41槽；两处必须来自同一实际冒险团分量。
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(206 + shift), reward.AdventureExperience);
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

    public static byte[] CardRights(short fatigue = 1, byte slot = 0)
    {
        if (slot >= 8) throw new ArgumentOutOfRangeException(nameof(slot));
        byte[] body = new byte[17];
        body[0] = 1;
        for (int index = 0; index < 8; index++)
            BinaryPrimitives.WriteInt16LittleEndian(body.AsSpan(1 + index * 2), index == slot ? fatigue : (short)-1);
        return body;
    }

    public static byte[] CardReward(DungeonClearExperienceReward reward, uint monsterExperience, DungeonCardReward? card,
        DungeonCardOptions? options = null,DungeonCardReward? blackDiamond = null,
        uint monsterGrowthContractExperience = 0, uint monsterEquipmentExperience = 0,
        IReadOnlyList<DungeonObjectExperience>? objectExperience = null, uint monsterBossExperience = 0, byte slot = 0,
        uint monsterFatigueBurningExperience = 0, uint monsterGuildExperience = 0, uint monsterFatigueBuffExperience = 0,
        uint monsterPartyExperience = 0)
    {
        if (slot >= 8) throw new ArgumentOutOfRangeException(nameof(slot));
        var empty = EmptyCardReward(reward, monsterExperience, monsterGrowthContractExperience, monsterEquipmentExperience,
            objectExperience, monsterBossExperience, monsterFatigueBurningExperience, monsterGuildExperience, monsterFatigueBuffExperience,
            monsterPartyExperience);
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
        int at = 156+shift+slot;
        body[at++] = checked((byte)count);
        foreach (var item in free) Pair(item.Id, item.Count);
        at += 7 - slot; // 本人之外的七个免费席，保留原始队伍位置。
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(at),checked((uint)(options?.Price ?? 0)));
        at += 4 + 8; // 费用及未使用的额外组。
        at += slot;
        body[at++] = checked((byte)bonusCount);
        foreach (var item in bonusItems) Pair(item.Id, item.Count);
        at += 7 - slot;
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

    public static byte[] FreeCardSelected(byte slot = 0)
    {
        if (slot >= 8) throw new ArgumentOutOfRangeException(nameof(slot));
        byte[] body = new byte[33];
        body[0] = 1;
        for (int index = 0; index < 8; index++)
        {
            body[1 + index * 4] = byte.MaxValue;
            body[2 + index * 4] = byte.MaxValue;
        }
        body[1 + slot * 4] = slot;
        // 选择窗口由本地待选位置展示，汇总按成员恢复；不把各人的本地牌面合成共享牌列。
        return body;
    }

    public static byte[] CardSelected(DungeonCardView view, IReadOnlyList<DungeonCardView?>? partyCards = null)
    {
        if (view.Slot >= 8) throw new ArgumentOutOfRangeException(nameof(view));
        if (partyCards is not null && partyCards.Count != 8) throw new ArgumentException("队伍牌面必须包含八席", nameof(partyCards));
        if (partyCards is null && view.Paid is null && view.FreeSelected) return FreeCardSelected(view.Slot);
        var members = partyCards?.ToArray() ?? new DungeonCardView?[8];
        members[view.Slot] = view;
        if (members.Where((member, slot) => member is not null && member.Slot != slot).Any())
            throw new ArgumentException("翻牌结果的成员槽不一致", nameof(partyCards));
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)1);
        for (int slot = 0; slot < 8; slot++)
        {
            var member = members[slot];
            var items = member?.Paid is { } reward ? Display(reward) : [];
            writer.Write(member?.FreeSelected == true ? (byte)slot : byte.MaxValue);
            writer.Write(member?.Paid is not null ? (byte)slot : byte.MaxValue);
            writer.Write(checked((byte)items.Count));
            foreach (var item in items) { writer.Write(item.Id); writer.Write(item.Count); }
            writer.Write((byte)0);
        }
        return stream.ToArray();
    }

    // 0233B698未为71注册专用失败读取器；只发公共失败前缀，不猜测业务错误码或伪造选中。
    public static byte[] CardRejected() => [0, 0, 0];

    public static byte[] PartyCardReward(byte[] original, byte ownSlot, IReadOnlyList<DungeonCardView?> cards)
    {
        if (ownSlot >= 8 || cards.Count != 8) throw new ArgumentOutOfRangeException(nameof(ownSlot));
        int start = checked(156 + (int)BinaryPrimitives.ReadUInt32LittleEndian(original.AsSpan(151)) * 8);
        int at = start;
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        stream.Write(original.AsSpan(0, start));
        for (int group = 0; group < 3; group++)
        {
            for (int slot = 0; slot < 8; slot++)
            {
                int length = checked(1 + original[at] * 8);
                if (group != 1 && cards[slot] is { FreeSelected: true } selected)
                {
                    var reward = group == 0 ? selected.Free : selected.BlackDiamond;
                    var items = reward is null ? [] : Display(reward);
                    stream.WriteByte(checked((byte)items.Count));
                    foreach (var item in items) { writer.Write(item.Id); writer.Write(item.Count); }
                }
                else stream.Write(original.AsSpan(at, length));
                at += length;
            }
            if (group == 0) { stream.Write(original.AsSpan(at, 4)); at += 4; }
        }
        stream.Write(original.AsSpan(at));
        return stream.ToArray();
    }

    public static byte[] ResultCommand(byte state, byte option) => [1, state, option];

    // 039F8B40/039F63E0固定把首项作为金币，含物品牌保留占位；完全空牌使用零条目。
    static List<(uint Id, uint Count)> Display(DungeonCardReward reward)
    {
        reward.Validate();
        if (reward.Gold == 0 && !reward.Items().Any()) return [];
        var result = new List<(uint, uint)> { (0, checked((uint)reward.Gold)) };
        result.AddRange(reward.Items().GroupBy(core => core.ItemId).Select(group =>
            (checked((uint)group.Key), checked((uint)group.Sum(core => (long)core.Count)))));
        return result;
    }
}
