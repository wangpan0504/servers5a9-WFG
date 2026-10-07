using System.Buffers.Binary;
using DfoServer.Network;

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
        Check(ClientNotices.TryParse(38, new byte[10], out var skill) && skill.Action == "use_skill" && skill.Detail.Length == 6 + 20, "use skill records ten bytes");
        Check(!ClientNotices.TryParse(38, new byte[9], out _), "use skill other width stays unrecognized");
        Check(ClientNotices.TryParse(33, Convert.FromHexString("2100490C0000"), out var trigger) && trigger.Action == "quest_trigger" && trigger.Detail == "quest=3145 value=0 tail=0", "quest trigger 3145 clear value");
        Check(!ClientNotices.TryParse(33, Convert.FromHexString("2100490C00"), out _), "quest trigger other width stays unrecognized");
        Check(!ClientNotices.TryParse(33, Convert.FromHexString("2200490C0000"), out _), "quest trigger echo mismatch stays unrecognized");
        Check(ClientNotices.TryParse(34, Convert.FromHexString("2200490CFFFF0100FFFF"), out var finish) && finish.Detail == "quest=3145 reward=65535 multiplier=1 reserved=65535", "finish quest 3145");
        Check(!ClientNotices.TryParse(34, Convert.FromHexString("2100490CFFFF0100FFFF"), out _), "finish quest echo mismatch stays unrecognized");
        Check(!ClientNotices.TryParse(63, [], out _), "cera query belongs to currency module");
        Check(!ClientNotices.TryParse(63, [0], out _), "nonempty 63 stays unrecognized");
        Check(ClientNotices.TryParse(120, [], out _), "empty 120");
        Check(ClientReplies.EmptyBlacklist.AsSpan().SequenceEqual(new byte[] { 1, 0 }), "empty blacklist reply");
        Check(ClientNotices.TryParse(140, [0, 0, 0, 0], out var flag) && flag.Detail == "value=0", "140 zero u32");
        Check(!ClientNotices.TryParse(140, [], out _), "140 other width stays unrecognized");
        Check(ClientNotices.TryParse(441, Convert.FromHexString("050102030405"), out var list) && list.Detail == "count=5 bytes=0102030405", "441 recorded list");
        Check(ClientNotices.TryParse(441, [0], out var none) && none.Detail == "count=0 bytes=", "441 empty list");
        Check(!ClientNotices.TryParse(441, [2, 1], out _), "441 count mismatch stays unrecognized");
        // 地址使用文档示例网段，计数字符串使用合成值，避免保留采集设备信息。
        uint addressValue = BinaryPrimitives.ReadUInt32LittleEndian(Convert.FromHexString("C0000201"));
        Check(ClientNotices.TryParse(593, Convert.FromHexString("0F000000C0000201"), out var address) && address.Detail == "type=15 value=" + addressValue, "593 type 15 address");
        Check(ClientNotices.TryParse(593, Convert.FromHexString("330000000900000073616D706C652D6964"), out var text) && text.Detail == "type=51 text=73616D706C652D6964", "593 counted text");
        Check(!ClientNotices.TryParse(593, [1, 0, 0, 0], out _), "593 short body stays unrecognized");
        Check(ClientNotices.TryParse(1507, Convert.FromHexString("01000100"), out var quad) && quad.Action == "recorded_1507" && quad.Detail == "a=1 b=0 c=1", "1507 four bytes stay a neutral record");
        Check(ClientNotices.TryParse(752, new byte[0x298], out var blob) && blob.Detail == "bytes=664", "752 fixed blob");
        Check(!ClientNotices.TryParse(752, new byte[48], out _), "752 other width stays unrecognized");
        Check(!ClientNotices.TryParse(1427, new byte[48], out _) && !ClientNotices.TryParse(1427, new byte[90], out _), "1427 layout remains unrecognized");
        Console.WriteLine($"PASS {count} client notice assertions");
    }
}
