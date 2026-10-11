using System.Buffers.Binary;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Dungeons;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class DungeonSettlementProtocolTests
{
    public static void Run()
    {
        int before = Count;
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
        That(clear.Length == 218 && U32(clear, 0) == 130 && U32(clear, 4) == 19 && clear[192] == 1 &&
            U32(clear, 194) == 1200 && clear.AsSpan(8, 184).IndexOfAnyExcept((byte)0) == -1 &&
            clear.AsSpan(210, 8).IndexOfAnyExcept((byte)0) == -1,
            "35匹配218B空动态表，基础/评分/已发击杀经验位及结果开关独立");
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
            U32(rewarded,165) == 10400 && U32(rewarded,169) == 1 && rewarded[208] == 1 && U32(rewarded,210) == 1200 &&
            rewarded.AsSpan(226,8).IndexOfAnyExcept((byte)0) == -1,
            "35免费席按金币及物品ID/本次数量预载，动态段后218B固定字段及八bool尾保持完整");
        Bytes(DungeonSettlementPackets.ResultCommand(1, 2), [1, 1, 2], "72先state后option原值回显");
        var membership = new DfoServer.Game.Dungeons.CardMembership(100,200,1,3,0);
        var options = new DfoServer.Game.Dungeons.DungeonCardOptions(bonus,340,false,membership,bonus);
        byte[] premium = DungeonSettlementPackets.CardReward(reward,1200,bonus,options,bonus);
        That(premium.Length == 250 && U32(premium,180) == 340 && premium[192] == 2 && U32(premium,193) == 0 &&
            U32(premium,197) == 123 && U32(premium,201) == 10400 && premium[224] == 1 && U32(premium,226) == 1200,
            "35会员组扩展仍保留本版尾部，费用与黑钻独立动态组明确排列");
        byte[] paid = DungeonSettlementPackets.CardSelected(new(options,true,bonus));
        That(paid.Length == 49 && paid[1] == 0 && paid[2] == 0 && paid[3] == 2 && U32(paid,4) == 0 &&
            U32(paid,8) == 123 && U32(paid,12) == 10400 && U32(paid,16) == 1,
            "71同时保留免费及付费选中状态，付费条目独立携带实际金币和本次数量");
        That(U16(DungeonSettlementPackets.CardRights(true),9) == 1,"70本人付费席在第五个资格槽开放");
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
}
