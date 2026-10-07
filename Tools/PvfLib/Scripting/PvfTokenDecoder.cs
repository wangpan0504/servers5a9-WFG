using System.Globalization;
using System.Text;
using static PvfLib.Format.PvfCodec;

namespace PvfLib.Scripting;

internal static class PvfTokenDecoder
{
    internal static PvfToken[] Tokens(ReadOnlySpan<byte> raw)
    {
        Validate(raw);
        var tokens = new PvfToken[raw.Length / 5];
        for (int i = 0; i < tokens.Length; i++)
            tokens[i] = new(raw[i * 5], I32(raw, i * 5 + 1));
        return tokens;
    }

    internal static string Text(ReadOnlySpan<byte> raw, int dataType, Func<int, string> resolve)
    {
        if (dataType == 3)
            return Encoding.Unicode.GetString(raw);
        Validate(raw);
        var result = new StringBuilder();
        // 从只读块直接格式化，避免另分配 raw 副本及完整 token 数组。
        for (int at = 0; at < raw.Length; at += 5)
        {
            int value = I32(raw, at + 1);
            switch (raw[at])
            {
                case 0:
                    result.Append(value.ToString(CultureInfo.InvariantCulture)).Append(' ');
                    break;
                case 2:
                    result.Append(BitConverter.Int32BitsToSingle(value).ToString("F2", CultureInfo.InvariantCulture)).Append(' ');
                    break;
                case 3:
                    result.Append('\n').Append(resolve(value)).Append('\n');
                    break;
                case 6:
                    result.Append('`').Append(resolve(value)).Append("` ");
                    break;
                // 文本是有损展示；引用及组合数值由调用方通过原始 token 解释。
                case 5:
                    result.Append("\n{5=``}");
                    break;
                case 7:
                    result.Append("\n{7=``}");
                    break;
            }
        }
        return result.ToString();
    }

    private static void Validate(ReadOnlySpan<byte> raw)
    {
        if (raw.Length % 5 != 0)
            throw new InvalidDataException("PVF token 流长度不完整");
    }
}
