using System.Buffers.Binary;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;
using DfoServer.Network.Builders;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Game.Currency;
using DfoServer.Network.Parsers;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class DungeonSettlementProtocolTests
{
    public static void Run()
    {
        int before = Count;
        ClearLimitTimeStartPacket();
        MembershipPackets();
        ExperiencePartOffsets();
        PartyReports();
        PartyCardSlots();
        byte[] boss = Convert.FromHexString("04001B0100000000000000000003000000B2080000000000003F03EF007400030106001E002000");
        That(DungeonSettlementRequests.TryParseBossCheck(boss, out var parsedBoss) && parsedBoss!.ActorId == 4 &&
            parsedBoss.BossKey == 283 && parsedBoss.SpawnLevel == 3 && parsedBoss.SpawnHp == 2226 && parsedBoss.RemainingFields.Count == 35,
            "匹配实收117完整39B；Boss key不是序号，统计尾不误作检查值");
        foreach (int width in new[] { 0, 4, 38, 40 })
            That(!DungeonSettlementRequests.TryParseBossCheck(new byte[width], out _), "117拒绝其他版本或残缺宽度");
        byte[] report = Report(4, 90);
        That(DungeonSettlementRequests.TryParsePlayResult(report, out var parsed) && parsed!.ActorId == 4 &&
            parsed.RankPoint == 90 && parsed.MaximumRankPoint == 90 && parsed.RemainingFields.Count == 37,
            "46普通单人包含原生49B和通用4B检查值");
        byte[] bad = (byte[])report.Clone();
        bad[49] ^= 1;
        That(!DungeonSettlementRequests.TryParsePlayResult(bad, out _), "46不匹配正文检查值不能结算");
        foreach (int width in new[] { 0, 49, 52, 54, 57 })
            That(!DungeonSettlementRequests.TryParsePlayResult(new byte[width], out _), "46不套用残缺或特殊资源宽度");
        bad = (byte[])report.Clone(); bad[3] = 2;
        That(!DungeonSettlementRequests.TryParsePlayResult(bad, out _), "成员数与完整正文长度必须匹配");
        Bytes(DungeonSettlementPackets.BossAccepted(283), [1, 1, 27, 1], "115成功包含允许位及Boss key");
        var presentation = new DungeonSettlementPresentation(12345, 90, 90, 1200);
        byte[] result = DungeonSettlementPackets.PlayResult(4, presentation);
        That(result.Length == 16 && result[0] == 90 && U32(result, 1) == 12345 && result[5] == 0 &&
            result[6] == 90 && result[7] == 1 && result[8] == 1 && U16(result, 9) == 4 && U32(result, 11) == 12345 && result[15] == 0,
            "34固定9B及单人7B，时间和评分显示不作新经验来源");
        var reward = new DungeonClearExperienceReward(130, 19, 149, 3, "fixture", "fixture");
        byte[] clear = DungeonSettlementPackets.EmptyCardReward(reward, 1200);
        That(clear.Length == 218 && U32(clear, 0) == 130 && U32(clear, 4) == 19 && clear[192] == 0 && clear[193] == 0 &&
            U32(clear, 194) == 1200 && clear.AsSpan(8, 184).IndexOfAnyExcept((byte)0) == -1 &&
            clear.AsSpan(210, 8).IndexOfAnyExcept((byte)0) == -1,
            "35匹配218B空动态表，双倍位与附属位均0，不能借活动位打开结果页");
        byte[] rights = DungeonSettlementPackets.CardRights();
        That(rights.Length == 17 && rights[0] == 1 && U16(rights, 1) == 1 && Enumerable.Range(1, 7).All(i => U16(rights, 1 + i * 2) == ushort.MaxValue),
            "70只保留本人免费行资格，其余席与收费行缺席");
        byte[] card = DungeonSettlementPackets.FreeCardSelected();
        That(card.Length == 33 && card[0] == 1 && card.AsSpan(1, 4).SequenceEqual(new byte[] { 0, 255, 0, 0 }) &&
            Enumerable.Range(1, 7).All(i => card[1 + i * 4] == 255 && card[2 + i * 4] == 255 && card[3 + i * 4] == 0 && card[4 + i * 4] == 0),
            "71八席完整消费，仅揭示本人免费牌，未开放付费席用-1");
        var bonus = new DfoServer.Game.Dungeons.DungeonCardReward(123,
            new DfoServer.Game.Inventory.Models.ItemCore(10400, DfoServer.GameWorld.Items.ItemKind.Equipment, 345), "fixture", "fixture", 1);
        byte[] rewarded = DungeonSettlementPackets.CardReward(reward, 1200, bonus);
        That(rewarded.Length == 234 && rewarded[156] == 2 && U32(rewarded,157) == 0 && U32(rewarded,161) == 123 &&
            U32(rewarded,165) == 10400 && U32(rewarded,169) == 1 && rewarded[208] == 0 && rewarded[209] == 0 && U32(rewarded,210) == 1200 &&
            rewarded.AsSpan(226,8).IndexOfAnyExcept((byte)0) == -1,
            "35免费席按金币及物品ID/本次数量预载，动态段后218B固定字段及八bool尾保持完整");
        Bytes(DungeonSettlementPackets.ResultCommand(1, (byte)DungeonResultOption.ReturnTown), [1, 1, 2], "72先state后option原值回显");
        That((byte)DungeonResultOption.Retry == 0 && (byte)DungeonResultOption.OtherDungeon == 1 &&
            (byte)DungeonResultOption.ContinuationTail == 3 && (ushort)DungeonResultClientError.SecretShopPending == 133,
            "结果页业务动作及神秘商店错误分别绑定72合同，不借16的同值错误");
        Bytes(DungeonSettlementPackets.RetryEligibility(false), [0], "261不可再挑战");
        Bytes(DungeonSettlementPackets.RetryEligibility(true), [9], "261可再挑战");
        var membership = new DfoServer.Game.Dungeons.CardMembership(100,200,1,3,0);
        var options = new DfoServer.Game.Dungeons.DungeonCardOptions(bonus,340,false,membership,bonus);
        byte[] premium = DungeonSettlementPackets.CardReward(reward,1200,bonus,options,bonus);
        That(premium.Length == 250 && U32(premium,180) == 340 && premium[192] == 2 && U32(premium,193) == 0 &&
            U32(premium,197) == 123 && U32(premium,201) == 10400 && premium[224] == 0 && premium[225] == 0 && U32(premium,226) == 1200,
            "35会员组扩展仍保留本版尾部，费用与黑钻独立动态组明确排列");
        byte[] paid = DungeonSettlementPackets.CardSelected(new(options,true,bonus));
        That(paid.Length == 49 && paid[1] == 0 && paid[2] == 0 && paid[3] == 2 && U32(paid,4) == 0 &&
            U32(paid,8) == 123 && U32(paid,12) == 10400 && U32(paid,16) == 1,
            "71同时保留免费及付费选中状态，付费条目独立携带实际金币和本次数量");
        That(U16(DungeonSettlementPackets.CardRights(0),1) == 0 && U16(DungeonSettlementPackets.CardRights(0),9) == ushort.MaxValue,
            "70的八个i16是成员疲劳值，不能伪造第五个成员来开放付费资格");
        var equipment = bonus with { Gold = 0 };
        var stack = bonus with { Item = new(999, DfoServer.GameWorld.Items.ItemKind.Material, 4) };
        var doubleStack = DungeonCardRewardPolicy.DoubleItems(stack);
        That(doubleStack.Item!.Value == 8 && doubleStack.ExtraItem is null && doubleStack.Gold == stack.Gold,
            "双倍堆叠只增加数量，不增加金币或创建第二份兼容堆叠");
        var doubleEquipment = DungeonCardRewardPolicy.DoubleItems(equipment);
        byte[] doubled = DungeonSettlementPackets.CardSelected(new(options, false, doubleEquipment));
        That(doubled.Length == 49 && doubled[1] == 255 && doubled[2] == 0 && doubled[3] == 2 &&
            U32(doubled,4) == 0 && U32(doubled,8) == 0 && U32(doubled,12) == 10400 && U32(doubled,16) == 2 &&
            doubleEquipment.Item!.Value == 345 && doubleEquipment.ExtraItem == doubleEquipment.Item,
            "双倍装备保持品级和两个完整核心，显示数量2且金币首项为零占位");
        byte[] noEquipment = DungeonSettlementPackets.CardSelected(new(options, true, equipment with { Item = null }));
        That(noEquipment.Length == 41 && noEquipment[3] == 1 && U32(noEquipment,4) == 0 && U32(noEquipment,8) == 0,
            "空付费牌只有零金币首项，没有伪造补偿或第二个装备槽金币");
        var eventOptions = options with { DoublePaidItems = true };
        byte[] eventCard = DungeonSettlementPackets.CardReward(reward,1200,bonus,eventOptions,bonus);
        That(eventCard[^26] == 1 && eventCard[^27] == 0 && eventCard.AsSpan(eventCard.Length-8).IndexOfAnyExcept((byte)0) == -1 &&
            rewarded[^26] == 0 && rewarded[^8] == 0,
            "活动双倍位独立于八成员标记，普通及活动牌均不伪造回归资格");
        foreach (bool black in new[] { false, true })
        foreach (bool hasItem in new[] { false, true })
        foreach (var (isDouble, legacyReturning) in new[] { (false,false), (true,false), (false,true), (true,true) })
        {
            var frozen = options with { DoublePaidItems = isDouble, LegacyReturningPlayer = legacyReturning,
                Membership = options.Membership with { LegacyReturningPlayerExpires = legacyReturning ? 1700000000 : 0 } };
            string archived = System.Text.Json.JsonSerializer.Serialize(frozen);
            if (legacyReturning)
            {
                using var json = System.Text.Json.JsonDocument.Parse(archived);
                That(json.RootElement.GetProperty("ReturningPlayer").GetBoolean() &&
                    json.RootElement.GetProperty("Membership").GetProperty("ReturningPlayerExpires").GetInt64() == 1700000000,
                    "兼容字段仍使用旧本地JSON字面键名，不把新Legacy标识符写入回执");
            }
            frozen = System.Text.Json.JsonSerializer.Deserialize<DungeonCardOptions>(archived)!;
            That(System.Text.Json.JsonSerializer.Serialize(frozen) == archived,
                "旧本地回归字段仅保留JSON回执形状，不能因裁剪玩法破坏已冻结回执核对");
            var body = DungeonSettlementPackets.CardReward(reward,1200,hasItem ? bonus : bonus with { Item = null },frozen,black ? bonus : null);
            var consumed = ReadCardTail(body);
            That(consumed.Rule == (isDouble ? 1 : 0) && consumed.Auxiliary == 0 && consumed.Members.AsSpan().IndexOfAnyExcept((byte)0) == -1,
                "独立消费者跨装备、黑钻及普通/双倍逐段读取35，旧回归回执也不激活标记或改变活动位");
        }
        var price = DungeonCardPriceCatalog.Parse([new(3,1),new(0,1),new(0,100),new(0,3),new(0,340)],_ => "[gold card cost table]");
        That(price.Price(1) == 100 && price.Price(2) == 100 && price.Price(3) == 340 && price.Price(95) == 340,
            "付费价格使用PVF等级段，缺精确行取下界而不误设免费");
        Reject<InvalidDataException>(() => DungeonCardPriceCatalog.Parse([new(3,1),new(0,1)],_ => "[gold card cost table]"),
            "残缺价格表不能默认为零金币");
        Console.WriteLine($"PASS {Count - before} Boss及空奖励结算协议断言");
    }

    // NOTI35加成分项按实机哨兵探针映射（2026-10-22）逐槽锁定；显示值只能来自本次冻结分项，不能借用未到账来源。
    static void ExperiencePartOffsets()
    {
        var reward = new DungeonClearExperienceReward(100, 7, 1000, 1, "fixture", "fixture")
        {
            BonusParts =
            [
                new(ExperienceBonusSource.Party, 808),
                new(ExperienceBonusSource.Avatar, 1212),
                new(ExperienceBonusSource.Potion, 102),
                new(ExperienceBonusSource.QuestPotion, 3),
                new(ExperienceBonusSource.BlackDiamond, 103),
                new(ExperienceBonusSource.InternetCafe, 104),
                new(ExperienceBonusSource.Creature, 106),
                new(ExperienceBonusSource.GrowthContract, 109),
                new(ExperienceBonusSource.FatigueBurning, 114),
                new(ExperienceBonusSource.Guild, 112)
            ]
        };
        byte[] body = DungeonSettlementPackets.EmptyCardReward(reward, 1200, 118, 120);
        That(body.Length == 218 && U32(body, 8) == 808 && U32(body, 12) == 1212 && U32(body, 21) == 105 &&
            U32(body, 25) == 103 && U32(body, 29) == 104 && U32(body, 37) == 106 && U32(body, 49) == 109 &&
            U32(body, 53) == 114 && U32(body, 61) == 112 && U32(body, 85) == 118 && U32(body, 93) == 120 &&
            U32(body, 194) == 1200,
            "35加成分项按实机映射写入基础块+8/+12及对应槽位，杀怪侧成长契约与装备分来源累计");
        var legacy = new DungeonClearExperienceReward(100, 7, 1000, 1, "fixture", "fixture")
        { GrowthContractExperience = 109, PotionExperience = 102 };
        byte[] old = DungeonSettlementPackets.EmptyCardReward(legacy, 1200);
        That(U32(old, 21) == 102 && U32(old, 49) == 109 && U32(old, 8) == 0 && U32(old, 25) == 0 &&
            U32(old, 53) == 0 && U32(old, 61) == 0,
            "未冻结分项的历史结算只按旧口径展示成长契约与秘药，其余来源保持0");
        // 杀怪经验明细是变长段：u32 条数在151，条目（对象键、基础经验）从155起，其后卡牌区与尾部整体平移8N。
        DungeonObjectExperience[] entries = [new(3587, 211), new(3591, 907)];
        byte[] detail = DungeonSettlementPackets.EmptyCardReward(reward, 1200, 118, 120, entries);
        That(detail.Length == 234 && U32(detail, 147) == 0 && U32(detail, 151) == 2 && U32(detail, 155) == 3587 && U32(detail, 159) == 211 &&
            U32(detail, 163) == 3591 && U32(detail, 167) == 907 && detail[171] == 0 && U32(detail, 194) == 0 &&
            U32(detail, 210) == 1200 && detail.AsSpan(226, 8).IndexOfAnyExcept((byte)0) == -1,
            "35杀怪明细按条数+对象键/基础经验写入，尾段杀怪合计与八bool尾随8N平移；未传领主值时147槽保持0");
        That(U32(DungeonSettlementPackets.EmptyCardReward(reward, 1200), 151) == 0,
            "35没有明细时条数为0，不能借旧的固定正文让客户端读出条目");
        // 领主行只读评分quartet第4槽（147），与151/155的对象明细互不替代。
        byte[] bossBody = DungeonSettlementPackets.EmptyCardReward(reward, 1200, 118, 120, null, 4400);
        That(bossBody.Length == 218 && U32(bossBody, 147) == 4400 && U32(bossBody, 151) == 0 && U32(bossBody, 194) == 1200,
            "35领主基础经验写入147槽；无对象明细时条数仍为0");
        var detailCardReward = new DungeonCardReward(123,
            new DfoServer.Game.Inventory.Models.ItemCore(10400, DfoServer.GameWorld.Items.ItemKind.Equipment, 345), "fixture", "fixture", 1);
        byte[] detailCard = DungeonSettlementPackets.CardReward(reward, 1200, detailCardReward, objectExperience: entries,
            monsterBossExperience: 4400);
        That(detailCard.Length == 250 && U32(detailCard, 147) == 4400 && U32(detailCard, 155) == 3587 && U32(detailCard, 159) == 211 &&
            detailCard[172] == 2 && U32(detailCard, 173) == 0 && U32(detailCard, 177) == 123 &&
            detailCard[224] == 0 && U32(detailCard, 226) == 1200 &&
            detailCard.AsSpan(242, 8).IndexOfAnyExcept((byte)0) == -1,
            "35杀怪明细与免费席同时存在时两段动态长度都按各自8N平移");
    }

    static void PartyReports()
    {
        foreach (string hex in new[]
        {
            "000000020100010000006303000000000014630000000000000000FFFFFFFFA40100004C010000A40100004C0100000900000000000000006DB771BA",
            "000000020100010000006303000000000032630100000001000000FFFFFFFF2D4C01006B6400002D4C0100E56D0000090000000000000000A11D5D9F"
        })
        {
            That(DungeonSettlementRequests.TryParsePlayResult(Convert.FromHexString(hex), out var report) &&
                report!.Participants.Count == 2 && report.Participants[0].ActorId == 1 && report.Participants[0].Value == 1 &&
                report.Participants[1].ActorId == 3 && report.Participants[1].RankPoint < report.MaximumRankPoint &&
                report.MaximumRankPoint == 99 && report.RemainingFields.Count == 37,
                "实机60B两人报告按成员行解析，队员评分可以低于最高评分");
        }
        foreach (int count in new[] { 2, 4, 8 })
        {
            var body = Report(Enumerable.Range(1, count).Select(i => ((ushort)i, (byte)(100 - i))).ToArray());
            That(body.Length == 46 + 7 * count && DungeonSettlementRequests.TryParsePlayResult(body, out var parsed) &&
                parsed!.Participants.Count == count && parsed.Participants[^1].RankPoint == 100 - count,
                "普通报告支持完整有界成员表，末行不会误读为最高评分或统计尾");
            That(!DungeonSettlementRequests.TryParsePlayResult(body[..^1], out _) &&
                !DungeonSettlementRequests.TryParsePlayResult([.. body, 0], out _), "多人报告拒绝截断和额外尾字节");
        }
        foreach (var body in new[] { Report((4, 90), (4, 20)), Report((4, 90), (8, 20)) })
        {
            if (U16(body, 4) != U16(body, 11)) body[18] = 20;
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(body.Length - 4), ClientBodyChecksum.Compute(body.AsSpan(0, body.Length - 4)));
            That(!DungeonSettlementRequests.TryParsePlayResult(body, out _), "重复成员或不等于全表最高分的最大评分均拒绝");
        }
        foreach (byte count in new byte[] { 0, 9, 255 })
        {
            var body = Report(4, 90); body[3] = count;
            That(!DungeonSettlementRequests.TryParsePlayResult(body, out _), "成员数只能为1至8");
        }
        var special = Report((4, 90), (8, 20)); special[0] = 1;
        That(!DungeonSettlementRequests.TryParsePlayResult(special, out _), "特殊报告种类不能套用普通组队格式");
        var presentation = new DungeonSettlementPresentation(12345, 20, 20, 1200);
        byte[] result = DungeonSettlementPackets.PlayResult(8, presentation, [4, 8]);
        That(result.Length == 23 && result[6] == 20 && result[8] == 2 && U16(result, 9) == 4 && U16(result, 16) == 8 &&
            U32(result, 11) == 12345 && U32(result, 18) == 12345, "34包含完整入场成员且本人显示自己的评分");
    }

    static void PartyCardSlots()
    {
        var reward = new DungeonClearExperienceReward(130, 19, 149, 3, "fixture", "fixture");
        var free = new DungeonCardReward(123, null, "fixture", "fixture", 1);
        var bonus = new DungeonCardReward(456, null, "fixture", "fixture", 1);
        foreach (byte slot in new byte[] { 0, 1, 3, 7 })
        {
            var rights = DungeonSettlementPackets.CardRights(87, slot);
            That(Enumerable.Range(0, 8).All(i => U16(rights, 1 + i * 2) == (i == slot ? 87 : ushort.MaxValue)),
                "70将本人疲劳写入原始队伍槽，其余席不开放");
            var body = DungeonSettlementPackets.CardReward(reward, 1200, free, blackDiamond: bonus, slot: slot);
            using var reader = new BinaryReader(new MemoryStream(body));
            reader.ReadBytes(156);
            for (int group = 0; group < 3; group++)
            {
                for (int member = 0; member < 8; member++)
                {
                    int count = reader.ReadByte();
                    bool own = group != 1 && member == slot;
                    That(count == (own ? 1 : 0), "35只有本人真实槽位含免费或黑钻候选，其他成员奖励不串用");
                    if (own) That(reader.ReadUInt32() == 0 && reader.ReadUInt32() == (group == 0 ? 123 : 456),
                        "免费与黑钻组的金币仍属于本人独立牌面");
                }
                if (group == 0) reader.ReadUInt32();
            }
            ReadCardTail(body);
            var view = new DungeonCardView(null, true, bonus) { Slot = slot };
            using var selected = new BinaryReader(new MemoryStream(DungeonSettlementPackets.CardSelected(view)));
            That(selected.ReadByte() == 1, "71保留公共成功前缀");
            for (int member = 0; member < 8; member++)
            {
                bool own = member == slot;
                That(selected.ReadByte() == (own ? 0 : 255) && selected.ReadByte() == (own ? 0 : 255) &&
                    selected.ReadByte() == (own ? 1 : 0), "71只揭示本人原始席位，不占用队长席");
                if (own) That(selected.ReadUInt32() == 0 && selected.ReadUInt32() == 456, "付费候选携带本人完整金币");
                That(selected.ReadByte() == 0, "71成员尾标志保留零");
            }
            That(selected.BaseStream.Position == selected.BaseStream.Length, "71完整消费八席动态正文");
            var onlyFree = DungeonSettlementPackets.FreeCardSelected(slot);
            That(Enumerable.Range(0, 8).All(i => onlyFree[1 + i * 4] == (i == slot ? 0 : 255) && onlyFree[2 + i * 4] == 255),
                "仅免费翻牌同样按本人原始槽位确认");
        }
    }

    internal static byte[] Report(ushort actorId, byte point) => Report([(actorId, point)]);

    internal static byte[] Report(params (ushort ActorId, byte Point)[] participants)
    {
        byte[] body = new byte[46 + 7 * participants.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(1), 12);
        body[3] = checked((byte)participants.Length);
        for (int index = 0; index < participants.Length; index++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(4 + 7 * index), participants[index].ActorId);
            body[10 + 7 * index] = participants[index].Point;
        }
        body[4 + 7 * participants.Length] = participants.Max(member => member.Point);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(body.Length - 4), ClientBodyChecksum.Compute(body.AsSpan(0, body.Length - 4)));
        return body;
    }

    static void ClearLimitTimeStartPacket()
    {
        // 固定字节独立编码；600仅作为读宽样本，不代表D4000时长政策。
        Bytes(DungeonPackets.ClearLimitTimeStart(600, 0xFEDCBA98),
            Convert.FromHexString("5802000098BADCFE"), "603正文严格8B，秒值不提前乘1000，未知次项不截断或换序");
        Bytes(DungeonPackets.ClearLimitTimeStart(0, 0),
            Convert.FromHexString("0000000000000000"), "603显式零值保持原样，不代入默认时限或计数");
        // 全宽仅证明序列化，不授予原生乘1000后的安全计时资格。
        Bytes(DungeonPackets.ClearLimitTimeStart(uint.MaxValue, 0x80000001),
            Convert.FromHexString("FFFFFFFF01000080"), "603两项均保留完整无符号32位线格式");
        byte[] first = DungeonPackets.ClearLimitTimeStart(1, 2);
        byte[] second = DungeonPackets.ClearLimitTimeStart(1, 2);
        That(!ReferenceEquals(first, second), "603每次构包返回独立数组");
        first[0] = 255;
        Bytes(second,
            Convert.FromHexString("0100000002000000"), "603每次独立构包，不共享可变正文");
    }

    static uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
    static ushort U16(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset));

    // 按02393C80读宽走完动态段，不使用Builder的固定偏移或负索引作为预期。
    static void MembershipPackets()
    {
        const long now = 1700000000;
        var membership = new CardMembership(now + 3600, now + 7200, GoldCardUsed: 9);
        var wallet = AccountWallet.Empty.SetContractExpiry(56, membership.BlackDiamondExpires).SetContractExpiry(58, membership.GoldCardExpires);
        byte[] info = PremiumPackets.SelectEntries(wallet, DateTimeOffset.FromUnixTimeSeconds(now), [56, 58]);
        using (var reader = new BinaryReader(new MemoryStream(info)))
        {
            That(reader.ReadByte() == 2 && reader.ReadByte() == 56 && reader.ReadInt64() == 3600 &&
                reader.ReadByte() == 58 && reader.ReadInt64() == 7200 && reader.BaseStream.Position == info.Length,
                "1760仅同步56黑钻及58概览，期限为剩余秒，不伪造其他契约");
        }
        byte[] service = DungeonCardMembershipPackets.DevilService0(membership, now);
        That(info[10] == 58 && BinaryPrimitives.ReadInt64LittleEndian(info.AsSpan(11)) == 7200 &&
            service.Length == 77 && service[0] == 1 && U16(service, 1) == 1 &&
            service.AsSpan(3, 6).IndexOfAnyExcept((byte)0) == -1 && U32(service, 9) == now + 7200 && service[13] == 9 &&
            service.AsSpan(14).IndexOfAnyExcept((byte)0) == -1 && membership.GoldCardAvailable(now),
            "type58同步服务0绝对期限及已用9次，仍可免费翻一次，不开放其他七项服务");
        var exhausted = membership with { GoldCardUsed = 10 };
        byte[] exhaustedService = DungeonCardMembershipPackets.DevilService0(exhausted, now);
        That(PremiumPackets.SelectEntries(wallet, DateTimeOffset.FromUnixTimeSeconds(now), [56, 58])[10] == 58 &&
            U32(exhaustedService, 9) == now + 7200 && exhaustedService[13] == 10 &&
            !exhausted.GoldCardAvailable(now) && exhausted.GoldCardActive(now),
            "每日10次耗尽后保留契约期限和真实次数，但免费黄金牌资格关闭");
        var beforeReset = new DateTimeOffset(2026, 10, 13, 5, 59, 59, TimeSpan.FromHours(8));
        That(CardMembership.ServiceDay(beforeReset) == new DateOnly(2026, 10, 12).DayNumber &&
            CardMembership.ServiceDay(beforeReset.AddSeconds(1)) == new DateOnly(2026, 10, 13).DayNumber,
            "免费黄金牌业务日按中国时间06:00切换，不以UTC零点或本机时区清零");
        var expired = membership with { BlackDiamondExpires = now, GoldCardExpires = now };
        byte[] cleared = PremiumPackets.SelectEntries(AccountWallet.Empty.SetContractExpiry(56,now).SetContractExpiry(58,now),
            DateTimeOffset.FromUnixTimeSeconds(now), [56, 58]);
        That(cleared[1] == 56 && cleared[10] == 58 && BinaryPrimitives.ReadInt64LittleEndian(cleared.AsSpan(2)) == 0 &&
            BinaryPrimitives.ReadInt64LittleEndian(cleared.AsSpan(11)) == 0 &&
            DungeonCardMembershipPackets.DevilService0(expired, now).AsSpan(3).IndexOfAnyExcept((byte)0) == -1,
            "权益关闭或到期必须同步零期限和空魔王owner，不能留下前角色的免费状态");
        byte[] combined = PremiumPackets.SelectEntries(wallet.SetContractExpiry(22, now + 100).SetContractExpiry(27,now + 200),
            DateTimeOffset.FromUnixTimeSeconds(now), [56, 58]);
        That(combined[0] == 4 && combined[1] == 22 && combined[10] == 27 && combined[19] == 56 && combined[28] == 58 &&
            BinaryPrimitives.ReadInt64LittleEndian(combined.AsSpan(29)) == 7200,
            "58和56复用统一1760目录，与上游22/27同时下发，不再发送平行会员列表");
    }

    static (byte Rule, byte Auxiliary, byte[] Members) ReadCardTail(byte[] body)
    {
        using var reader = new BinaryReader(new MemoryStream(body, writable: false));
        reader.ReadBytes(4 * 4 + 1 + 22 * 4);
        for (int group = 0; group < 2; group++)
            reader.ReadBytes(reader.ReadByte() * 8);
        reader.ReadBytes(7 * 4 + 4 * 4);
        reader.ReadBytes(reader.ReadInt32() * 8);
        reader.ReadBytes(reader.ReadByte() * 15);
        for (int group = 0; group < 3; group++)
        {
            for (int member = 0; member < 8; member++)
                reader.ReadBytes(reader.ReadByte() * 8);
            if (group == 0) reader.ReadUInt32();
        }
        That(reader.ReadUInt32() == 0 && reader.ReadUInt32() == 0, "35全局值及弃用u32没有藏入活动位");
        byte rule = reader.ReadByte(), auxiliary = reader.ReadByte();
        reader.ReadBytes(4 * 4);
        var members = reader.ReadBytes(8);
        That(members.Length == 8 && reader.BaseStream.Position == body.Length, "35消费者完整读取尾部且无剩余或截断");
        return (rule, auxiliary, members);
    }
}
