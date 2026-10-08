using System.Buffers.Binary;
using System.Security.Cryptography;
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
        RunMaster();
        RunOrdinary();
    }

    public static void RunOrdinary()
    {
        int before = Count;
        That(DungeonSettlementRequests.TryParseCardRights([]) &&
            !DungeonSettlementRequests.TryParseCardRights([0]) &&
            !DungeonSettlementRequests.TryParseCardRights([0, 0, 0, 0]),
            "70严格空请求，不借46摘要合同接受未知正文");
        That(DungeonSettlementPackets.CardRightsAccepted(new short[] { 123, 0, -1, 32767, -32768, 1, 2, 3 })
                .SequenceEqual(Convert.FromHexString("017B000000FFFFFF7F0080010002000300")),
            "70独立固定样本完整编码公共success及8个显式i16疲劳值");
        foreach (int count in new[] { 0, 1, 7, 9 })
        {
            bool rejected = false;
            try { DungeonSettlementPackets.CardRightsAccepted(new short[count]); }
            catch (InvalidDataException) { rejected = true; }
            That(rejected, "70不隐填或截断成员快照: " + count);
        }
        foreach (byte type in new byte[] { 0, 1 })
            foreach (byte member in new byte[] { 0, 3, 7 })
                That(DungeonSettlementRequests.TryParseCardSelection(new byte[] { type, member }, out var selection) &&
                    selection.Type == type && selection.MemberIndex == member,
                    "71免费/付费及成员索引保留，不将索引解释为本地卡槽: " + type + "/" + member);
        foreach (byte[] invalid in new byte[][] { [], [0], [0, 0, 0], [0, 0, 0, 0, 0, 0], [2, 0], [255, 0], [0, 8], [1, 255] })
            That(!DungeonSettlementRequests.TryParseCardSelection(invalid, out _),
                "71拒绝未知类型、成员、截短及伪摘要尾: " + Convert.ToHexString(invalid));
        var cardRows = Enumerable.Range(0, 8).Select(_ =>
            new DungeonCardSelectionRow(255, 255, Array.Empty<DungeonCardDisplayItem>(), false)).ToArray();
        cardRows[0] = new(0, 255, Array.Empty<DungeonCardDisplayItem>(), false);
        cardRows[1] = new(255, 0, new DungeonCardDisplayItem[] { new(20, 2), new(50, 3) }, true);
        // 后6行固定FF/FF/00/00；独立样本不由builder生成预期。
        byte[] actualCardSample = DungeonSettlementPackets.CardSelectionAccepted(cardRows);
        byte[] exactCardSample = Convert.FromHexString("0100FF0000FF00021400000002000000320000000300000001FFFF0000FFFF0000FFFF0000FFFF0000FFFF0000FFFF0000");
        That(actualCardSample.Length == 49 && actualCardSample.SequenceEqual(exactCardSample),
            "71独立非空样本完整8行及2个item/value显示条目，未选择owner保持FF");
        cardRows[1] = new(255, 0, Enumerable.Repeat(new DungeonCardDisplayItem(uint.MaxValue, 1), 127).ToArray(), false);
        That(DungeonSettlementPackets.CardSelectionAccepted(cardRows).Length == 33 + 127 * 8,
            "71显式signed count上限127保留完整条目，不截断");
        foreach (int count in new[] { 7, 9 })
        {
            bool rejected = false;
            try { DungeonSettlementPackets.CardSelectionAccepted(Enumerable.Repeat(cardRows[0], count).ToArray()); }
            catch (InvalidDataException) { rejected = true; }
            That(rejected, "71必须完整8行: " + count);
        }
        foreach (var invalidRow in new DungeonCardSelectionRow[] {
            new(8, 255, Array.Empty<DungeonCardDisplayItem>(), false),
            new(255, 8, Array.Empty<DungeonCardDisplayItem>(), false),
            new(255, 0, Enumerable.Repeat(new DungeonCardDisplayItem(20, 1), 128).ToArray(), false) })
        {
            cardRows[0] = invalidRow;
            bool rejected = false;
            try { DungeonSettlementPackets.CardSelectionAccepted(cardRows); }
            catch (InvalidDataException) { rejected = true; }
            That(rejected, "71拒绝非法owner及signed count128，不能借成功截短正文");
        }
        That(DungeonSettlementPackets.Clear().Length == 0, "可信清场NOTI31无奖励正文，不伪造结算成功");
        That(DungeonSettlementRequests.TryParseScoreScroll([]) &&
            !DungeonSettlementRequests.TryParseScoreScroll([0]) &&
            !DungeonSettlementRequests.TryParseScoreScroll([1, 0]) &&
            DungeonSettlementPackets.ScoreScrollAccepted().SequenceEqual(new byte[] { 1 }),
            "普通69严格空请求及完整公共success响应，不能把统计请求视为发奖");
        foreach (byte operation in new byte[] { 0, 1, 2 })
        {
            That(DungeonSettlementRequests.TryParseOrdinaryExit(new byte[] { 1, operation }, out byte parsedOperation) &&
                parsedOperation == operation &&
                DungeonSettlementPackets.OrdinaryExitAccepted(operation).SequenceEqual(new byte[] { 1, 1, operation }),
                "普通72提交保留再挑战/其他副本/回城字典及完整3B响应: " + operation);
        }
        foreach (byte[] invalid in new byte[][] { [], [1], [1, 2, 0], [0, 2], [2, 2], [255, 2], [1, 3], [1, 255] })
            That(!DungeonSettlementRequests.TryParseOrdinaryExit(invalid, out _),
                "72不截短、不接受悬停mode2或未确认operation: " + Convert.ToHexString(invalid));
        bool invalidExitResponseRejected = false;
        try { DungeonSettlementPackets.OrdinaryExitAccepted(3); }
        catch (ArgumentOutOfRangeException) { invalidExitResponseRejected = true; }
        That(invalidExitResponseRejected, "72 builder不编码未确认operation成功");
        byte[] direct = Convert.FromHexString("003412017856EFCDAB8907A598BADCFE1020304001000000020000000300000004000000FFFFFFFF092233445566778899");
        bool accepted = DungeonSettlementRequests.TryParseOrdinary(direct, out var parsed);
        That(direct.Length == 49 && accepted &&
            parsed is { Mode: 0, FirstStatistic: 0x1234, CharacterKey: 0x5678, ParticipantStatistic: 0x89ABCDEF,
                ParticipantFlag: 7, BestFlag: 0xA5, SecondStatistic: 0xFEDCBA98, ThirdStatistic: 0x40302010,
                Grade: 9, FourthStatistic: 0x55443322, FifthStatistic: 0x99887766 } &&
            parsed.CharacterStatistics.SequenceEqual(new uint[] { 1, 2, 3, 4, uint.MaxValue }) &&
            parsed.BodyChecksum is null && parsed.ChecksumMatches is null,
            "独立49B固定样本完整保留身份和全部未知统计，宽度/偏移不混成经验");
        byte[] digest = MD5.HashData(direct);
        byte[] tail = [(byte)(digest[0] ^ digest[13] ^ digest[8] ^ digest[10] ^ 0x81),
            (byte)(digest[1] ^ digest[9] ^ digest[5] ^ digest[12] ^ 0x78),
            (byte)(digest[2] ^ digest[6] ^ digest[14] ^ digest[4] ^ 0x1A),
            (byte)(digest[3] ^ digest[15] ^ digest[11] ^ digest[7] ^ 0xBF)];
        byte[] received = [.. direct, .. tail];
        That(DungeonSettlementRequests.TryParseOrdinary(received, out var checkedRequest) && checkedRequest!.ChecksumMatches == true &&
            checkedRequest.Grade == parsed!.Grade && checkedRequest.CharacterStatistics.SequenceEqual(parsed.CharacterStatistics),
            "opcode46已证追加4B摘要形成53B，业务尾字段不从摘要读取");
        received[20] = 99;
        That(checkedRequest!.CharacterStatistics[0] == 1 &&
            DungeonSettlementRequests.TryParseOrdinary(received, out var changed) && changed!.ChecksumMatches == false,
            "请求快照冻结缓冲，摘要不匹配只报告状态不伪造业务错误域");
        for (int length = 0; length < 49; length++)
            That(!DungeonSettlementRequests.TryParseOrdinary(direct.AsSpan(0, length), out _), "所有截短正文拒绝: " + length);
        foreach (int length in new[] { 50, 51, 52, 54, 56, 100 })
            That(!DungeonSettlementRequests.TryParseOrdinary(new byte[length], out _), "未证特殊或追加宽度拒绝: " + length);
        byte[] wrong = (byte[])direct.Clone();
        wrong[0] = 1;
        That(!DungeonSettlementRequests.TryParseOrdinary(wrong, out _), "其他首mode不借用普通结算");
        wrong[0] = 0;
        foreach (byte count in new byte[] { 0, 2, 8, 255 })
        {
            wrong[3] = count;
            That(!DungeonSettlementRequests.TryParseOrdinary(wrong, out _), "只支持完整单人形状，不能截断成员行: " + count);
        }
        Console.WriteLine($"PASS {Count - before} 普通结算请求与清场通知协议断言");
    }

    public static void RunMaster()
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
        byte[] card = DungeonSettlementPackets.EmptyCardSelected();
        That(card.Length == 33 && card[0] == 1 && card.AsSpan(1, 4).SequenceEqual(new byte[4]) &&
            Enumerable.Range(1, 7).All(i => card[1 + i * 4] == 255 && card[2 + i * 4] == 255 && card[3 + i * 4] == 0 && card[4 + i * 4] == 0),
            "71八席空条目仍完整消费，缺席席位用-1，没有道具金币");
        Bytes(DungeonSettlementPackets.ResultCommand(1, 2), [1, 1, 2], "72先state后option原值回显");
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
