using System.Text;
using static PvfLib.Tests.TestCheck;

namespace PvfLib.Tests;

internal static class StringTextTests
{
    public static void Run()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Encoding gb = Encoding.GetEncoding("GB18030");
        byte[][] narrow =
        [
            [], Encoding.UTF8.GetBytes("[name]"), Encoding.UTF8.GetBytes("中文🙂e\u0301"),
            [0x81, 0x30, 0x81, 0x30], [0x41, 0x42, 0xFF], [0xE4, 0xB8],
            Encoding.UTF8.GetBytes(new string('x', 255)), Encoding.UTF8.GetBytes(new string('y', 256)),
            Encoding.UTF8.GetBytes(new string('z', 257)), Encoding.UTF8.GetBytes(new string('甲', 4096)), [.. Encoding.UTF8.GetBytes(new string('a', 300)), 0xFF]
        ];
        byte[][] wide = [[], Encoding.Unicode.GetBytes("甲🙂A"), [0x00, 0xD8], [0x00, 0xDC],
            [0x00, 0xD8, 0x41, 0x00], Encoding.Unicode.GetBytes(new string('界', 255)), Encoding.Unicode.GetBytes(new string('界', 256)),
            Encoding.Unicode.GetBytes(new string('界', 257))];
        foreach (bool variant in new[] { false, true })
        {
            foreach (byte[] value in narrow)
            {
                string expected;
                try { expected = new UTF8Encoding(false, true).GetString(value); }
                catch (DecoderFallbackException) { expected = gb.GetString(value); }
                Check(variant, [.. value, 0], [0, 0], 22, expected);
            }
            foreach (byte[] value in wide)
                Check(variant, [0], [.. value, 0, 0], 1, Encoding.Unicode.GetString(value));
            Check(variant, [0], [0, 0], -1, "");
            Check(variant, [0], [0, 0], int.MinValue, "");
            foreach (int offset in new[] { int.MaxValue, int.MaxValue - 1 })
                Reject(variant, [0], [0, 0], offset);
            Reject(variant, [65], [0, 0], 22);
            Reject(variant, [0], [65], 1);
            // 独立 UTF-16 文件仍按原编码规则处理奇数尾字节，不经过字符串池路径。
            using var direct = PvfArchive.OpenBytes(ArchiveFixture.Create(variant, ("raw", 3, new byte[] { 65, 0, 66 })));
            That(direct.ReadText("raw") == Encoding.Unicode.GetString(new byte[] { 65, 0, 66 }), "原始宽文本奇数尾语义不变");
        }
    }

    private static void Check(bool variant, byte[] narrow, byte[] wide, int offset, string expected)
    {
        var tokens = new[] { new PvfToken(3, offset), new PvfToken(6, offset), new PvfToken(6, offset) };
        using var archive = PvfArchive.OpenBytes(ArchiveFixture.CreateStringPools(variant, narrow, wide,
            ("script.bin", 1, ArchiveFixture.Tokens(tokens))), new() { TextCacheBytes = 0 });
        string text = "\n" + expected + "\n`" + expected + "` `" + expected + "` ";
        That(archive.ResolveString(offset) == expected, "独立编码预期与公开字符串解析一致");
        That(archive.ReadText("script.bin") == text, "短长字符串投影保持编码、终止符及标点");
        That(archive.ReadTokens("script.bin").SequenceEqual(tokens), "字符串文本读取不改变token位模式");
        Parallel.For(0, 8, _ => That(archive.ReadText("script.bin") == text, "同归档并发文本不共享可变字符缓冲"));
    }

    private static void Reject(bool variant, byte[] narrow, byte[] wide, int offset)
    {
        using var archive = PvfArchive.OpenBytes(ArchiveFixture.CreateStringPools(variant, narrow, wide,
            ("script.bin", 1, ArchiveFixture.Tokens(new PvfToken(3, offset)))));
        TestCheck.Reject<InvalidDataException>(() => archive.ResolveString(offset));
        TestCheck.Reject<InvalidDataException>(() => archive.ReadText("script.bin"));
    }
}
