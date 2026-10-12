using System.Buffers.Text;
using System.Collections.Immutable;

namespace DfoServer.Game.Settings;

internal static class HotkeySettingsRules
{
    // 本版字符串读器要求length < 1400；不能沿用参考端8192B的接收上限。
    public const int MaximumTextLength = 1399;

    public static byte Profile(byte job) => job <= 15
        ? job == 10 ? (byte)1 : (byte)0 : throw new InvalidDataException("未知职业的键位配置");

    public static void Validate(ImmutableArray<byte> text)
    {
        if (text.IsDefaultOrEmpty || text.Length > MaximumTextLength)
            throw new InvalidDataException("键位文本长度无效");
        var remaining = text.AsSpan();
        while (!remaining.IsEmpty)
        {
            int end = remaining.IndexOf((byte)'\n');
            var row = end < 0 ? remaining : remaining[..end];
            if (!row.IsEmpty && row[^1] == '\r') row = row[..^1];
            int tab = row.IndexOf((byte)'\t');
            if (tab <= 0 || tab == row.Length - 1)
                throw new InvalidDataException("键位行必须包含动作名和整数键值");
            foreach (byte value in row[..tab])
                if (!(value is >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z' or >= (byte)'0' and <= (byte)'9'))
                    throw new InvalidDataException("键位动作名不是本版文本标识");
            if (!Utf8Parser.TryParse(row[(tab + 1)..], out int _, out int parsed) || parsed != row.Length - tab - 1)
                throw new InvalidDataException("键位值不是完整整数");
            remaining = end < 0 ? [] : remaining[(end + 1)..];
        }
    }
}
