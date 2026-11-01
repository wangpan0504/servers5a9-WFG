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
            Lookup(variant);
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

    private static void Lookup(bool variant)
    {
        Encoding gb = Encoding.GetEncoding("GB18030");
        const int SuffixStart = 4; // 夹具文件名raw及其终止符占4字节。
        byte[] utf8 = Encoding.UTF8.GetBytes("中文🙂e\u0301");
        byte[] gbText = gb.GetBytes("汉字");
        byte[] first = Encoding.UTF8.GetBytes("prefixsuffix\0");
        byte[] second = Encoding.UTF8.GetBytes("same\0");
        byte[] narrow = [..first, ..second, ..second, ..utf8, 0, ..gbText, 0, 0];
        byte[] wide = Encoding.Unicode.GetBytes("wide-prefix\0same\0甲🙂A\0\0");
        using var archive = Open(narrow, wide);
        Found(archive, "raw", 0);
        Found(archive, "prefixsuffix", SuffixStart * 2);
        Found(archive, "same", (SuffixStart + first.Length) * 2);
        Found(archive, "中文🙂e\u0301", (SuffixStart + first.Length + second.Length * 2) * 2);
        Found(archive, "汉字", (SuffixStart + first.Length + second.Length * 2 + utf8.Length + 1) * 2);
        Found(archive, "", (SuffixStart + narrow.Length - 1) * 2);
        Found(archive, "wide-prefix", 1);
        Found(archive, "甲🙂A", Encoding.Unicode.GetByteCount("wide-prefix\0same\0") + 1);
        Missing(archive, "suffix");
        Missing(archive, "prefix");
        Missing(archive, "RAW");
        Missing(archive, "wide");
        Missing(archive, "中文🙂é");
        Missing(archive, "not-present");
        TestCheck.Reject<ArgumentNullException>(() => archive.TryFindStringValue(null!, out _));
        TestCheck.Reject<ArgumentException>(() => archive.TryFindStringValue("raw\0suffix", out _));

        using var onlyWide = Open([], Encoding.Unicode.GetBytes("same\0same\0\0"));
        Found(onlyWide, "same", 1);
        Found(onlyWide, "", Encoding.Unicode.GetByteCount("same\0same\0") + 1);
        using var noEmpty = Open(Encoding.UTF8.GetBytes("tail\0"), Encoding.Unicode.GetBytes("wide\0"));
        Missing(noEmpty, "");
        using var unterminated = Open(Encoding.UTF8.GetBytes("tail"), Encoding.Unicode.GetBytes("wide"));
        Missing(unterminated, "tail");
        Missing(unterminated, "wide");
        Missing(unterminated, "");
        using var oddWide = Open([], [65, 0, 0]);
        Missing(oddWide, "A");

        // 同一窄字节序列有效UTF-8时，不能因GB18030重编码恰好匹配而改变解析语义。
        using var ambiguous = Open([0xC2, 0xA9, 0], []);
        Found(ambiguous, "©", SuffixStart * 2);
        Missing(ambiguous, gb.GetString([0xC2, 0xA9]));
        using var alignedWide = Open([], Encoding.Unicode.GetBytes("ĀA\0"));
        Found(alignedWide, "ĀA", 1);
        Missing(alignedWide, "A");
        string longText = new('界', 257);
        using var longLookup = Open(Encoding.UTF8.GetBytes(longText + '\0'), []);
        Found(longLookup, longText, SuffixStart * 2);
        byte[] gbDuplicate = gb.GetBytes("汉字");
        using var duplicateEncoding = Open([..gbDuplicate, 0, ..Encoding.UTF8.GetBytes("汉字\0")], []);
        Found(duplicateEncoding, "汉字", SuffixStart * 2);
        var disposed = Open([0], [0, 0]);
        disposed.Dispose();
        TestCheck.Reject<ObjectDisposedException>(() => disposed.TryFindStringValue("raw", out _));

        PvfArchive Open(byte[] ansi, byte[] unicode) => PvfArchive.OpenBytes(
            ArchiveFixture.CreateStringPools(variant, ansi, unicode, ("raw", 1, [])));

        static void Found(PvfArchive input, string text, int expected)
        {
            That(input.TryFindStringValue(text, out int actual) && actual == expected,
                "字符串查找只返回完整项，重复项和窄宽顺序确定");
            That(input.ResolveString(actual) == text, "查找偏移经公开解析往返一致");
        }

        static void Missing(PvfArchive input, string text)
        {
            That(!input.TryFindStringValue(text, out int value) && value == 0,
                "不存在、后缀、大小写或未终止字符串不返回伪偏移");
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
