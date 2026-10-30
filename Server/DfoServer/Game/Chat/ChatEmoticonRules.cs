using System.Text;
using DfoServer.Game.Characters;
using DfoServer.GameWorld.Characters;

namespace DfoServer.Game.Chat;

internal static class ChatEmoticonRules
{
    // 原字节正文截断时同时保留CP936字符和完整表情标记，不通过解码重编码改写物品引用。
    public static int ByteBoundary(ReadOnlySpan<byte> text, int maximum)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximum);
        int at = 0;
        while (at < text.Length)
        {
            int end = at + (text[at] is >= 0x81 and <= 0xfe && at + 1 < text.Length ? 2 : 1);
            int marker = at + (text[at] == 0x7f && at + 1 < text.Length && text[at + 1] == 0x1e ? 1 : 0);
            if (text[marker] == 0x1e && marker + 2 < text.Length && text[marker + 1] == 0x20)
            {
                int close = marker + 2 + (text[marker + 2] is >= 0x81 and <= 0xfe ? 2 : 1);
                if (close < text.Length && text[close] == 0x1f) end = close + 1;
            }
            if (end > maximum) break;
            at = end;
        }
        return at;
    }

    public static byte[] FilterBytes(ReadOnlySpan<byte> text, IReadOnlySet<ushort> allowed)
    {
        using var output = new MemoryStream(text.Length);
        int first = !text.IsEmpty && text[0] == 0x7f ? 1 : 0;
        if (first != 0) output.WriteByte(0x7f);
        for (int at = first; at < text.Length;)
        {
            int start = at;
            bool prefix = text[at] == 0x7f && at + 1 < text.Length && text[at + 1] == 0x1e;
            int marker = at + (prefix ? 1 : 0);
            if (text[marker] == 0x1e)
            {
                int width = marker + 2 < text.Length && text[marker + 2] is >= 0x81 and <= 0xfe ? 2 : 1;
                int end = marker + 2 + width;
                bool complete = end < text.Length && text[marker + 1] == 0x20 && text[end] == 0x1f;
                at = complete ? end + 1 : marker + 1;
                if (!complete) continue;
                try
                {
                    string id = CharacterName.Encoding.GetString(text.Slice(marker + 2, width));
                    if (id.Length != 1 || !allowed.Contains((ushort)id[0])) continue;
                }
                catch (DecoderFallbackException) { continue; }
            }
            else at++;
            output.Write(text.Slice(start, at - start));
        }
        return output.ToArray();
    }

    public static ChatEmoticonEligibility Eligibility(Character character, long now)
    {
        ulong low = 0, high = 0;
        byte tail = 0;
        foreach (var (type, expiry) in character.Inventory.AccountWallet.PremiumContracts)
        {
            if (expiry <= now) continue;
            if (type is > 0 and < 64) low |= 1UL << type;
            else if (type is >= 64 and < 128) high |= 1UL << (type - 64);
            else if (type is >= 128 and < 132) tail |= (byte)(1 << (type - 128));
        }
        var marriage = character.Marriage;
        return new(character.Level, marriage is { State: 4 }, marriage?.Members.GetValueOrDefault(character.Id)?.LovePoints ?? 0,
            low, high, tail);
    }

    // 原生正文为可选7F前缀及1E 20 <表情编号> 1F；整体过滤、整体截断，不能切成普通控制字符。
    public static string Prepare(string text, IReadOnlySet<ushort> allowed, int maxBytes, out bool changed)
    {
        var result = new StringBuilder(Math.Min(text.Length, maxBytes));
        int bytes = 0;
        changed = false;
        int first = text.Length > 0 && text[0] == '\x7f' ? 1 : 0;
        if (first != 0)
        {
            if (maxBytes < 1)
            {
                changed = true;
                return "";
            }
            result.Append('\x7f');
            bytes = 1;
        }
        for (int at = first; at < text.Length;)
        {
            int start = at;
            bool prefix = text[at] == '\x7f' && at + 1 < text.Length && text[at + 1] == '\x1e';
            int marker = at + (prefix ? 1 : 0);
            if (text[marker] == '\x1e')
            {
                bool complete = marker + 3 < text.Length && text[marker + 1] == ' ' && text[marker + 3] == '\x1f';
                at = complete ? marker + 4 : marker + 1;
                if (!complete || !allowed.Contains((ushort)text[marker + 2]))
                {
                    changed = true;
                    continue;
                }
            }
            else at++;
            var unit = text.AsSpan(start, at - start);
            int width = ChatText.Encoding.GetByteCount(unit);
            if (bytes + width > maxBytes)
            {
                changed = true;
                break;
            }
            result.Append(unit);
            bytes += width;
        }
        return result.ToString();
    }
}
