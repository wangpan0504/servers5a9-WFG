using System.Buffers.Binary;
using DfoServer.Game.Dungeons;
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
        MembershipPackets();
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
        That(!DungeonSettlementRequests.TryParsePlayResult(bad, out _), "当前普通单人结算不冒充组队报告");
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
        Bytes(DungeonSettlementPackets.ResultCommand(1, 2), [1, 1, 2], "72先state后option原值回显");
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

    internal static byte[] Report(ushort actorId, byte point)
    {
        byte[] body = new byte[53];
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(1), 12);
        body[3] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(4), actorId);
        body[10] = body[11] = point;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(49), ClientBodyChecksum.Compute(body.AsSpan(0, 49)));
        return body;
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
