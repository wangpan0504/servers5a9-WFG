using System.Text;
using static PvfLib.Format.PvfCodec;

namespace PvfLib.Format;

internal sealed class PvfStringPool
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
    private static readonly Lazy<Encoding> FallbackEncoding = new(() =>
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding("GB18030");
    });
    private readonly byte[] ansi, wide;

    internal PvfStringPool(ReadOnlySpan<byte> names, bool protectedFormat)
    {
        int cursor = 8;
        ansi = ReadPool(names, ref cursor, protectedFormat ? "StRa" : "sTrA", 0xAA74472E, protectedFormat);
        wide = ReadPool(names, ref cursor, protectedFormat ? "StRw" : "sTrW", 0x9A82F037, protectedFormat);
    }

    internal string Resolve(int value)
    {
        ReadOnlySpan<byte> bytes = Locate(value, out bool unicode);
        if (unicode)
            return Encoding.Unicode.GetString(bytes);
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return FallbackEncoding.Value.GetString(bytes);
        }
    }

    internal void AppendTo(StringBuilder target, int value)
    {
        ReadOnlySpan<byte> bytes = Locate(value, out bool unicode);
        Encoding encoding = SelectEncoding(bytes, unicode, out int count);
        // 先完整验证并选择编码，不能把失败的 UTF-8 前缀写入后再追加回退文本。
        const int InlineChars = 256;
        if (count > InlineChars)
        {
            // 长字符串保留原分配路径，不扩大栈或引入长期持有的大缓冲。
            target.Append(encoding.GetString(bytes));
            return;
        }
        Span<char> buffer = stackalloc char[InlineChars];
        int written = encoding.GetChars(bytes, buffer);
        target.Append(buffer[..written]);
    }

    private ReadOnlySpan<byte> Locate(int value, out bool unicode)
    {
        unicode = (value & 1) != 0;
        if (value < 0)
            return [];
        byte[] pool = unicode ? wide : ansi;
        int start = unicode ? value - 1 : value / 2, step = unicode ? 2 : 1;
        if (start >= pool.Length)
            throw new InvalidDataException("PVF 字符串偏移越界");
        int end = start;
        while (end + step <= pool.Length && (pool[end] != 0 || unicode && pool[end + 1] != 0))
            end += step;
        if (end + step > pool.Length)
            throw new InvalidDataException("PVF 字符串未终止");
        return pool.AsSpan(start, end - start);
    }

    private static Encoding SelectEncoding(ReadOnlySpan<byte> bytes, bool unicode, out int count)
    {
        if (unicode)
        {
            count = Encoding.Unicode.GetCharCount(bytes);
            return Encoding.Unicode;
        }
        try
        {
            count = StrictUtf8.GetCharCount(bytes);
            return StrictUtf8;
        }
        catch (DecoderFallbackException)
        {
            Encoding fallback = FallbackEncoding.Value;
            count = fallback.GetCharCount(bytes);
            return fallback;
        }
    }

    private static byte[] ReadPool(ReadOnlySpan<byte> names, ref int cursor, string key, uint xor, bool protectedFormat)
    {
        if (cursor + 8L > names.Length)
            throw new InvalidDataException("PVF 字符串池头不完整");
        uint length = U32(names, cursor) ^ xor, decoded = U32(names, cursor + 4) ^ length;
        cursor += 8;
        if (length == 0 || cursor + (long)length > names.Length || protectedFormat && decoded > MaxBytes)
            throw new InvalidDataException("PVF 字符串池长度无效");
        byte[] encrypted = names.Slice(cursor, (int)length).ToArray();
        cursor += (int)length;
        Crypt(encrypted, key, protectedFormat, 0x269EC9);
        return Inflate(encrypted, protectedFormat ? (int)decoded : null);
    }
}
