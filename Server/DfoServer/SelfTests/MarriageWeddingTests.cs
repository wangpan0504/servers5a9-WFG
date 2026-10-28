using System.Buffers.Binary;
using DfoServer.Game.Marriage;
using DfoServer.GameWorld.Marriage;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class MarriageWeddingTests
{
    public static void RunCore()
    {
        int checks = Count;
        int[] maleJobs = [0, 2, 4, 7, 8, 9, 13, 15];
        That(Enumerable.Range(0, 16).Where(job => MarriageWeddingRules.SexGroup(job) == 1).SequenceEqual(maleJobs),
            "结婚性别采用本版原生职业映射，7/8/9不按奇偶错分");
        That(MarriageWeddingRules.EligibleProposal(1, 30, 7, true, 2, 30, 3, true, 30) &&
            !MarriageWeddingRules.EligibleProposal(1, 30, 7, true, 2, 30, 8, true, 30) &&
            !MarriageWeddingRules.EligibleProposal(1, 29, 7, true, 2, 30, 3, true, 30) &&
            !MarriageWeddingRules.EligibleProposal(1, 30, 7, true, 1, 30, 3, true, 30) &&
            !MarriageWeddingRules.EligibleProposal(1, 30, 7, false, 2, 30, 3, true, 30),
            "求婚检查双方等级、不同角色、性别与已有关系资格");

        var record = MarriageWeddingRules.CreateEngagement(Guid.NewGuid(), 123, 1, 2, 1000);
        That(record is { State: 1, Revision: 1, FirstId: 1, SecondId: 2, CreatedAt: 1000 } &&
            record.Members.Count == 2, "婚约建立时双方状态与聚合版本同时初始化");
        var ended = MarriageWeddingRules.EndRelationship(record, 1001);
        That(!ended.Record.Active && ended.Record.Revision == 2 && ended.Principal.IsEmpty && record.Active,
            "解除只生成新快照，不修改旧聚合");
        Reject<InvalidOperationException>(() => MarriageWeddingRules.EndRelationship(ended.Record, 1002), "解除重放不能生成第二次退款");

        var proposal = MarriageWeddingRequests.Request(Structure("78563412010201"));
        That(proposal == new MarriageWeddingRequest(0x12345678, 1, 2, 1), "求婚结构中的目标与三个操作字节按原码偏移读取");
        That(MarriageWeddingRequests.Response(Structure("4433221107000002")) ==
            new MarriageWeddingResponse(0x11223344, 7, 0, 2), "协议离婚答复末尾状态与中间u16不混读");
        That(MarriageWeddingRequests.Character(Structure("34120000")) == 0x1234 &&
            MarriageWeddingRequests.UpgradeRing(Structure("0700000003")) == new MarriageRingRequest(7, 3) &&
            MarriageWeddingRequests.UseLovePointItem(Structure("09000000")) == 9, "角色卡、婚戒券与爱心券的结构槽位合同");
        That(MarriageWeddingRequests.SendLetter(Structure("0244332211665503")) ==
            new MarriageLetterRequest(2, 0x11223344, 0x5566, 3) &&
            MarriageWeddingRequests.EnterCeremony(Structure("443322118877665501")) ==
            new MarriageEnterCeremonyRequest(0x11223344, 0x55667788, 1), "邀请与进礼堂结构的非对齐字段保持顺序");
        var gift = MarriageWeddingRequests.MoneyGift(Convert.FromHexString("443322111027000004000000D0A1B9D6"));
        That(gift == new MarriageMoneyGiftRequest(0x11223344, 10000, "小怪"), "礼金是普通写入路径，不额外跳过结构前缀");

        int[] widths = [20, 21, 17, 18, 17, 21, 22];
        for (int index = 0; index < widths.Length; index++)
        {
            int width = widths[index];
            Reject<InvalidDataException>(() => Parse(index, new byte[width - 1]), "婚礼结构拒绝截断正文");
            Reject<InvalidDataException>(() => Parse(index, new byte[width + 1]), "婚礼结构拒绝尾随正文");
        }
        Reject<InvalidDataException>(() => MarriageWeddingRequests.Request(new byte[7]), "不能将结构前缀误剥离后仍接受请求");
        Reject<InvalidDataException>(() => MarriageWeddingRequests.MoneyGift(Convert.FromHexString("0100000010270000FFFFFFFF")), "礼金留言无符号超长拒绝");
        Reject<InvalidDataException>(() => MarriageWeddingRequests.MoneyGift(Convert.FromHexString("01000000102700000100000081")), "礼金留言截断双字节编码拒绝");

        That(Convert.ToHexString(MarriageWeddingPackets.RequestResult(-7, 0x11223344, 2, 1, 2)) ==
            "F9FFFFFF44332211020102" &&
            Convert.ToHexString(MarriageWeddingPackets.ResponseAck(0, 0x11223344, 7, 2)) ==
            "01000000004433221107000002", "通知不带成功标志，ACK的前导标志与签名错误码宽度各自保持");
        That(Convert.ToHexString(MarriageWeddingPackets.Info(0x11223344, 4, 2, 0x88776655, 520, 3)) ==
            "443322110402556677880802000003", "婚姻状态15字节固定位置对应原码");
        var ceremony = new MarriageCeremonyWire(1, 2, 3000, 4, 2, "小怪", "Tester");
        byte[] ceremonyBody = MarriageWeddingPackets.Ceremony(ceremony, 30);
        byte[] letterBody = MarriageWeddingPackets.Letter(ceremony, 1, 0x11223344);
        That(ceremonyBody.Length == 79 && ceremonyBody[16] == 2 && ceremonyBody[17] == 0xD0 &&
            BinaryPrimitives.ReadUInt16LittleEndian(ceremonyBody.AsSpan(77)) == 30 &&
            letterBody.Length == 82 && letterBody[77] == 1 &&
            BinaryPrimitives.ReadUInt32LittleEndian(letterBody.AsSpan(78)) == 0x11223344,
            "婚礼预约与邀请共用77字节前部，尾字段分别为u16和u8加u32");
        That(MarriageWeddingPackets.Avatar(0, 1, "A", "B", 2, 3, 4).Length == 71 &&
            MarriageWeddingPackets.CeremonyBegin(1, 2, "A", "B").Length == 68 &&
            Convert.ToHexString(MarriageWeddingPackets.InvitedGuests([1, 0x11223344])) == "020100000044332211",
            "婚礼装扮、开场与来宾名单长度按消费者逐项读取");
        That(MarriageWeddingPackets.GiftAck(-1).Length == 5 && MarriageWeddingPackets.GiftAck(0, 99).Length == 9 &&
            MarriageWeddingPackets.LetterAck(-1).Length == 5 && MarriageWeddingPackets.LetterAck(0, 30).Length == 7 &&
            MarriageWeddingPackets.RingAck(-2, 1).Length == 6, "分支回执只在消费者成功分支追加字段");
        Reject<ArgumentNullException>(() => MarriageWeddingPackets.GiftAck(0), "成功礼金回执不能省略未知后续字段");
        Reject<ArgumentException>(() => MarriageWeddingPackets.CharacterAck(new byte[96]), "未知角色卡不得用短包填充成功");
        Reject<ArgumentException>(() => MarriageWeddingPackets.CeremonyBegin(1, 2, new string('A', 30), "B"), "固定名称不截断越界");
        Console.WriteLine($"PASS {Count - checks} marriage wedding core assertions");
    }

    public static void Run(PvfArchive pvf) => Run(MarriageCatalog.Load(pvf));

    public static void Run(MarriageCatalog catalog)
    {
        int checks = Count;
        var engaged = MarriageWeddingRules.CreateEngagement(Guid.NewGuid(), 123, 1, 2, 1000);
        var reservation = MarriageWeddingRules.Reserve(engaged, catalog, Guid.NewGuid(), 1, 0, 0, 1000, 1300, true);
        var record = reservation.Record;
        That(reservation.Cost.Gold == catalog.Ceremonies[0].Gold && record.State == 2 && record.Revision == 2 &&
            record.Wedding!.EndsAt == 1300 + catalog.Timing.NotifyEndSeconds, "预约生成实际成本，通知容差不重复使用开场等待时长，未提前宣布已婚");
        Reject<InvalidOperationException>(() => MarriageWeddingRules.Reserve(record, catalog, Guid.NewGuid(), 1, 0, 0, 1001, 1001, true),
            "预约重放不产生第二笔费用");
        Reject<InvalidOperationException>(() => MarriageWeddingRules.Reserve(engaged, catalog, Guid.NewGuid(), 1, 0, 0, 1000, 1000, false),
            "准备任务未达成不能预约");
        That(MarriageWeddingRules.StageEndFee(engaged, catalog) == catalog.AlimonyRaw[0] &&
            MarriageWeddingRules.StageEndFee(engaged with { State = 4 }, catalog) == catalog.AlimonyRaw[1], "解除费用按婚约或已婚阶段索引");

        record = MarriageWeddingRules.SendInvitation(record, 1, 3, "Guest", 1001);
        That(record.Wedding!.InviteRemaining[1] == catalog.Ceremonies[0].LettersPerPersonRaw - 1 &&
            record.Wedding.InviteRemaining[2] == catalog.Ceremonies[0].LettersPerPersonRaw, "邀请仅扣发送方的额度");
        Reject<InvalidOperationException>(() => MarriageWeddingRules.SendInvitation(record, 2, 3, "Guest", 1002), "另一配偶不能重复邀请同一来宾");
        var giftId = Guid.NewGuid();
        record = MarriageWeddingRules.AddGift(record, catalog, new() { Id = giftId, CharacterId = 3, Name = "Guest", Gold = 10000 }, 1002);
        Reject<InvalidOperationException>(() => MarriageWeddingRules.AddGift(record, catalog,
            new() { Id = Guid.NewGuid(), CharacterId = 3, Gold = 10000 }, 1003), "重复送礼不会形成第二笔未结清账单");
        That(MarriageWeddingRules.GuestGift(catalog, 0, 99999).SequenceEqual(catalog.GuestGiftTiers[0].GiftsByWeddingType[0]) &&
            MarriageWeddingRules.GuestGift(catalog, 0, 100000).SequenceEqual(catalog.GuestGiftTiers[1].GiftsByWeddingType[0]),
            "礼金奖励在资源上限边界正确换档");
        var refunds = MarriageWeddingRules.EndRelationship(record, 1004);
        That(refunds.Principal.Length == 2 && refunds.Principal.Any(row => row.GiftId == giftId && row.Gold == 10000) &&
            MarriageWeddingRules.RefundableLedger(refunds.Record).IsEmpty && refunds.Record.Wedding!.Gifts.All(row => row.Settled),
            "取消账单保留付款人与来宾本金且解除后不能再取第二次退款");

        Reject<InvalidOperationException>(() => MarriageWeddingRules.SendInvitation(record, 1, 4, "Late", 1300), "开场后不能趁通知容差继续发请帖");
        Reject<InvalidOperationException>(() => MarriageWeddingRules.AddGift(record, catalog,
            new() { Id = Guid.NewGuid(), CharacterId = 4, Gold = 10000 }, 1300), "开场后不能趁通知容差继续扣礼金");
        var married = MarriageWeddingRules.CompleteCeremony(record, 1300);
        Reject<InvalidOperationException>(() => MarriageWeddingRules.CompleteCeremony(married, 1301), "婚礼完成重放不重复授予婚姻状态");
        Reject<InvalidOperationException>(() => MarriageWeddingRules.UpgradeRing(married, catalog, 1, 2, catalog.UpgradeRingTickets[2].ItemId),
            "婚戒越级拒绝");
        var ring = MarriageWeddingRules.UpgradeRing(married, catalog, 1, 1, catalog.UpgradeRingTickets[1].ItemId);
        That(ring.Record.RingLevel == 1 && ring.Cost.Gold == 0 && ring.Cost.Item == catalog.UpgradeRingTickets[1],
            "婚戒只扣一张匹配资源券，不重复扣除购买金币");
        var loveItem = catalog.LovePoints.ItemPoints.First(row => row.Value == 520);
        var love = MarriageWeddingRules.UseLovePointItem(married, catalog, 1, loveItem.Key);
        That(love.Record.Members[1].LovePoints == 520 && love.Record.Members[2].LovePoints == 0 && love.Cost.Item.Count == 1,
            "爱心券只更新使用者并消耗一件");
        Reject<InvalidOperationException>(() => MarriageWeddingRules.UseLovePointItem(love.Record, catalog, 1, loveItem.Key),
            "超过999的爱心道具整笔拒绝，不截断加点后仍消耗");
        var daily = MarriageWeddingRules.GrantDailyLove(love.Record, catalog, 1, 20261026);
        That(ReferenceEquals(daily, MarriageWeddingRules.GrantDailyLove(daily, catalog, 1, 20261026)) &&
            ReferenceEquals(daily, MarriageWeddingRules.GrantDailyLove(daily, catalog, 1, 20261025)),
            "每日爱心同日和旧日重放不增加积分或版本");
        Console.WriteLine($"PASS {Count - checks} marriage wedding resource rules assertions");
    }

    static byte[] Structure(string payload)
    {
        byte[] data = Convert.FromHexString(payload);
        byte[] body = new byte[13 + data.Length];
        body.AsSpan(0, 13).Fill(0xA5); data.CopyTo(body, 13); return body;
    }

    static void Parse(int kind, byte[] body)
    {
        switch (kind)
        {
            case 0: MarriageWeddingRequests.Request(body); break;
            case 1: MarriageWeddingRequests.Response(body); break;
            case 2: MarriageWeddingRequests.Character(body); break;
            case 3: MarriageWeddingRequests.UpgradeRing(body); break;
            case 4: MarriageWeddingRequests.UseLovePointItem(body); break;
            case 5: MarriageWeddingRequests.SendLetter(body); break;
            case 6: MarriageWeddingRequests.EnterCeremony(body); break;
        }
    }
}
