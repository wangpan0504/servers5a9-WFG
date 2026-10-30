using System.Globalization;
using System.Text;
using static PvfLib.Tests.TestCheck;

namespace PvfLib.Tests;

internal static class NumericTextTests
{
    public static void Run()
    {
        int[] integers = [int.MinValue, -1000001, -1, 0, 1, 1000001, int.MaxValue];
        float[] singles = [float.MinValue, -12345678.5f, -1.125f, -1.005f, -0.005f,
            -float.Epsilon, BitConverter.Int32BitsToSingle(unchecked((int)0x80000000)),
            0f, float.Epsilon, 0.005f, 1.005f, 1.125f, 12345678.5f, float.MaxValue,
            float.NaN, float.NegativeInfinity, float.PositiveInfinity,
            BitConverter.Int32BitsToSingle(0x7FA12345),
            BitConverter.Int32BitsToSingle(unchecked((int)0xFFC12345))];
        var tokens = new List<PvfToken>();
        foreach (int value in integers) tokens.Add(new(0, value));
        foreach (float value in singles) tokens.Add(new(2, BitConverter.SingleToInt32Bits(value)));
        // 固定位模式补充指数及非正规值覆盖，不读取真实资源。
        uint bits = 0x12345678;
        for (int i = 0; i < 256; i++)
        {
            bits = unchecked(bits * 1664525 + 1013904223);
            tokens.Add(new(2, unchecked((int)bits)));
        }
        tokens.AddRange([new(3, 0), new(6, 1), new(5, 0), new(7, 0), new(9, 0)]);
        var expected = new StringBuilder();
        foreach (var token in tokens)
            if (token.Type == 0)
                expected.Append(token.Value.ToString(CultureInfo.InvariantCulture)).Append(' ');
            else if (token.Type == 2)
                expected.Append(BitConverter.Int32BitsToSingle(token.Value).ToString("F2", CultureInfo.InvariantCulture)).Append(' ');
        expected.Append("\nscript.bin\n`甲A` \n{5=``}\n{7=``}");
        string expectedText = expected.ToString();
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            foreach (string culture in new[] { "en-US", "fr-FR", "ar-SA", "tr-TR" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                foreach (bool variant in new[] { false, true })
                {
                    using var archive = PvfArchive.OpenBytes(ArchiveFixture.Create(variant,
                        ("script.bin", 1, ArchiveFixture.Tokens(tokens.ToArray()))), new() { TextCacheBytes = 0 });
                    That(archive.ReadText("script.bin") == expectedText,
                        "数字文本与原 invariant ToString 逐字符相同，包含极值、舍入、负零及特殊浮点数");
                    That(archive.ReadTokens("script.bin").SequenceEqual(tokens), "文本投影不改变原始 token 位模式");
                }
            }
        }
        finally { CultureInfo.CurrentCulture = original; }
    }
}
