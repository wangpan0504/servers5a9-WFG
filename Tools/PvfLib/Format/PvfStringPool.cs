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
        if (value < 0)
            return "";
        bool unicode = (value & 1) != 0;
        byte[] pool = unicode ? wide : ansi;
        int start = unicode ? value - 1 : value / 2, step = unicode ? 2 : 1;
        if (start >= pool.Length)
            throw new InvalidDataException("PVF 字符串偏移越界");
        int end = start;
        while (end + step <= pool.Length && (pool[end] != 0 || unicode && pool[end + 1] != 0))
            end += step;
        if (end + step > pool.Length)
            throw new InvalidDataException("PVF 字符串未终止");
        if (unicode)
            return Encoding.Unicode.GetString(pool, start, end - start);
        try
        {
            return StrictUtf8.GetString(pool, start, end - start);
        }
        catch (DecoderFallbackException)
        {
            return FallbackEncoding.Value.GetString(pool, start, end - start);
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
