using System.Buffers.Binary;
using DfoServer.Network;
using DfoServer.Network.Parsers;

namespace DfoServer;

internal static class ClientNoticeTests
{
    public static void Run()
    {
        int count = 0;
        void Check(bool ok, string name) { count++; if (!ok) throw new Exception("FAIL: " + name); }
        Check(!ClientNotices.TryParse(16, new byte[25], out _), "dungeon selection belongs to the dungeon module");
        Check(!ClientNotices.TryParse(37, new byte[8], out _), "finish loading belongs to the dungeon module");
        Check(!ClientNotices.TryParse(37, new byte[4], out _), "finish loading other width stays unrecognized");
        Check(ClientNotices.TryParse(38, new byte[10], out var skill) && skill.Action == "use_skill" &&
            skill.Detail == "bytes=00000000000000000000 checksum=absent", "use skill records ten bytes without a checksum");
        Check(!ClientNotices.TryParse(38, new byte[9], out _), "use skill other width stays unrecognized");
        // 独立固定预期来自匹配ARAD正常战斗的三类实收样本，不以解析结果反算预期。
        (string Hex, ushort At1, ushort At3, ushort At5, string Tail)[] uses =
        [
            ("002E00D00729000001004AADA701", 46, 2000, 41, "4AADA701"),
            ("0005007017234800010052760356", 5, 6000, 18467, "52760356"),
            ("00A9000000F16D000100DD2A7E19", 169, 0, 28145, "DD2A7E19"),
            ("00050070172900000100D260CC5C", 5, 6000, 41, "D260CC5C")
        ];
        foreach (var sample in uses)
        {
            byte[] body = Convert.FromHexString(sample.Hex);
            Check(SkillRequests.TryParseUse(body, out var use) && use is not null && use.Unknown0 == 0 &&
                use.Unknown1 == sample.At1 && use.Unknown3 == sample.At3 && use.Unknown5 == sample.At5 &&
                use.Unknown7 == 0 && use.Unknown8 == 1 && use.RemainingFields.SequenceEqual(Convert.FromHexString(sample.Tail)) &&
                use.BodyChecksum == BinaryPrimitives.ReadUInt32LittleEndian(Convert.FromHexString(sample.Tail)) &&
                use.ChecksumMatches == true,
                "actual skill use preserves all fields and matches the independent body checksum sample");
            Check(ClientNotices.TryParse(38, body, out var actual) && actual.Action == "use_skill" &&
                actual.Detail == "bytes=" + sample.Hex + " checksum=matched", "actual fourteen-byte skill use reaches the observation handler");
            body[10] ^= 255;
            Check(use!.RemainingFields.SequenceEqual(Convert.FromHexString(sample.Tail)) &&
                use.RemainingFields is System.Collections.IList frozen && frozen.IsReadOnly && use.ChecksumMatches == true,
                "skill use owns an immutable copy of the suffix and the original checksum result");
            Check(SkillRequests.TryParseUse(body, out var badChecksum) && badChecksum!.ChecksumMatches == false &&
                badChecksum.BodyChecksum != use.BodyChecksum && ClientNotices.TryParse(38, body, out var mismatch) &&
                mismatch.Detail == "bytes=" + Convert.ToHexString(body) + " checksum=mismatched",
                "checksum mismatch remains recorded while the current producer and consumer await verification");
            body = Convert.FromHexString(sample.Hex);
            body[1] ^= 1;
            Check(SkillRequests.TryParseUse(body, out var badBody) && badBody!.ChecksumMatches == false &&
                badBody.BodyChecksum == use.BodyChecksum, "a changed body does not match the unchanged checksum");
        }
        Check(SkillRequests.TryParseUse(new byte[10], out var original) && original!.RemainingFields.Count == 0 &&
            original.BodyChecksum is null && original.ChecksumMatches is null,
            "original ten-byte writer footprint has no fabricated suffix or checksum result");
        foreach (int length in new[] { 0, 9, 11, 12, 13, 15, 88 })
            Check(!SkillRequests.TryParseUse(new byte[length], out var invalid) && invalid is null &&
                !ClientNotices.TryParse(38, new byte[length], out _), "unsupported skill use length remains unhandled " + length);
        Check(ClientNotices.TryParse(33, Convert.FromHexString("2100490C0000"), out var trigger) && trigger.Action == "quest_trigger" && trigger.Detail == "quest=3145 value=0 tail=0", "quest trigger 3145 clear value");
        Check(!ClientNotices.TryParse(33, Convert.FromHexString("2100490C00"), out _), "quest trigger other width stays unrecognized");
        Check(!ClientNotices.TryParse(33, Convert.FromHexString("2200490C0000"), out _), "quest trigger echo mismatch stays unrecognized");
        Check(ClientNotices.TryParse(34, Convert.FromHexString("2200490CFFFF0100FFFF"), out var finish) && finish.Detail == "quest=3145 reward=65535 multiplier=1 reserved=65535", "finish quest 3145");
        Check(!ClientNotices.TryParse(34, Convert.FromHexString("2100490CFFFF0100FFFF"), out _), "finish quest echo mismatch stays unrecognized");
        Check(!ClientNotices.TryParse(63, [], out _), "cera query belongs to currency module");
        Check(!ClientNotices.TryParse(63, [0], out _), "nonempty 63 stays unrecognized");
        Check(ClientNotices.TryParse(120, [], out _), "empty 120");
        Check(ClientReplies.EmptyBlacklist.AsSpan().SequenceEqual(new byte[] { 1, 0 }), "empty blacklist reply");
        Check(!ClientNotices.TryParse(140, [0, 0, 0, 0], out _), "140 belongs to the guild member query module");
        Check(!ClientNotices.TryParse(140, [], out _), "140 malformed body is not a diagnostic notice");
        Check(ClientNotices.TryParse(441, Convert.FromHexString("050102030405"), out var list) && list.Detail == "count=5 bytes=0102030405", "441 recorded list");
        Check(ClientNotices.TryParse(441, [0], out var none) && none.Detail == "count=0 bytes=", "441 empty list");
        Check(!ClientNotices.TryParse(441, [2, 1], out _), "441 count mismatch stays unrecognized");
        // 地址使用文档示例网段，计数字符串使用合成值，避免保留采集设备信息。
        uint addressValue = BinaryPrimitives.ReadUInt32LittleEndian(Convert.FromHexString("C0000201"));
        Check(ClientNotices.TryParse(593, Convert.FromHexString("0F000000C0000201"), out var address) && address.Detail == "type=15 value=" + addressValue, "593 type 15 address");
        Check(ClientNotices.TryParse(593, Convert.FromHexString("330000000900000073616D706C652D6964"), out var text) && text.Detail == "type=51 text=73616D706C652D6964", "593 counted text");
        Check(!ClientNotices.TryParse(593, [1, 0, 0, 0], out _), "593 short body stays unrecognized");
        Check(!ClientNotices.TryParse(1507, Convert.FromHexString("01000100"), out _), "1507 is owned by the guild ranking handler");
        Check(ClientNotices.TryParse(752, new byte[0x298], out var blob) && blob.Detail == "bytes=664", "752 fixed blob");
        Check(!ClientNotices.TryParse(752, new byte[48], out _), "752 other width stays unrecognized");
        Check(!ClientNotices.TryParse(1427, new byte[48], out _) && !ClientNotices.TryParse(1427, new byte[90], out _), "1427 layout remains unrecognized");
        Console.WriteLine($"PASS {count} client notice assertions");
    }
}
