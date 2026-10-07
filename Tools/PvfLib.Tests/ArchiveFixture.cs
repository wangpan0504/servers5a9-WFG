using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace PvfLib.Tests;

// 独立编码器逐字节生成小型输入，不依赖库内部编解码实现。
internal static class ArchiveFixture
{
    public static byte[] Create(bool variant, params (string Path, int Type, byte[] Bytes)[] files)
    {
        using var names = new MemoryStream();
        using var body = new MemoryStream();
        byte[] table = new byte[files.Length * 24], groups = new byte[files.Length * 8];
        for (int i = 0; i < files.Length; i++)
        {
            Put(table, i * 24, checked((int)names.Length * 2));
            names.Write(Encoding.UTF8.GetBytes(files[i].Path + '\0'));
            Put(table, i * 24 + 4, -1);
            Put(table, i * 24 + 8, i);
            Put(table, i * 24 + 16, files[i].Bytes.Length);
            Put(table, i * 24 + 20, files[i].Type);
            byte[] packed = Compress(files[i].Bytes);
            Crypt(packed, variant ? "bODy" : "BodY", variant);
            body.Write(packed);
            Put(groups, i * 8, checked((int)body.Length));
            Put(groups, i * 8 + 4, files[i].Bytes.Length);
        }
        byte[] pools = [.. new byte[8], .. Pool(names.ToArray(), false), .. Pool(Encoding.Unicode.GetBytes("甲A\0"), true)];
        byte[] header = new byte[48];
        Put(header, 0, 0x69706B6E);
        Put(header, 24, files.Length);
        Put(header, 32, checked((int)body.Length));
        Put(header, 36, files.Length);
        Put(header, 44, pools.Length);
        Crypt(header, variant ? "hEAd" : "HeaD", variant);
        if (!variant)
            for (int i = 24; i < 28; i++)
                header[i] ^= 0x55;
        Crypt(groups, variant ? "grpi" : "GRPI", variant);
        return [.. header, .. table, .. pools, .. groups, .. body.ToArray()];

        byte[] Pool(byte[] raw, bool wide)
        {
            byte[] packed = Compress(raw);
            Crypt(packed, wide ? (variant ? "StRw" : "sTrW") : (variant ? "StRa" : "sTrA"), variant, 0x269EC9);
            byte[] prefix = new byte[8];
            Put(prefix, 0, packed.Length ^ unchecked((int)(wide ? 0x9A82F037u : 0xAA74472Eu)));
            Put(prefix, 4, raw.Length ^ packed.Length);
            return [.. prefix, .. packed];
        }
    }

    public static byte[] Tokens(params PvfToken[] tokens)
    {
        byte[] bytes = new byte[tokens.Length * 5];
        for (int i = 0; i < tokens.Length; i++)
        {
            bytes[i * 5] = tokens[i].Type;
            Put(bytes, i * 5 + 1, tokens[i].Value);
        }
        return bytes;
    }

    public static void RewriteGroupSize(byte[] bytes, bool variant, int size)
    {
        byte[] header = bytes[..48];
        if (!variant)
            for (int i = 24; i < 28; i++)
                header[i] ^= 0x55;
        Crypt(header, variant ? "hEAd" : "HeaD", variant);
        int offset = 48 + BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(24)) * 24 + BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(44));
        int length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(36)) * 8;
        Span<byte> groups = bytes.AsSpan(offset, length);
        Crypt(groups, variant ? "grpi" : "GRPI", variant);
        BinaryPrimitives.WriteInt32LittleEndian(groups[4..], size);
        Crypt(groups, variant ? "grpi" : "GRPI", variant);
    }

    public static void Put(byte[] bytes, int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset), value);

    private static byte[] Compress(byte[] bytes)
    {
        if (bytes.Length == 0)
            return [0x78, 0x9C, 0x03, 0, 0, 0, 0, 1];
        using var output = new MemoryStream();
        using (var stream = new ZLibStream(output, CompressionLevel.Fastest, true))
            stream.Write(bytes);
        return output.ToArray();
    }

    private static void Crypt(Span<byte> bytes, string key, bool variant, uint magic = 0x269EC3)
    {
        unchecked
        {
            uint factor = variant ? 0x393u : 0x1C1u;
            uint seed = (variant ? 0x339E9711u : 0x76826701u) * key[0] + factor * (key[3] + factor * (key[2] + factor * key[1]));
            for (int pos = 0; pos < bytes.Length; pos += 4)
            {
                uint first = 0x343FD * seed + magic;
                seed = 0x343FD * first + magic;
                uint mask = (first & 0xFFFF0000) + (seed >> 16);
                for (int i = 0; i < Math.Min(4, bytes.Length - pos); i++)
                    bytes[pos + i] ^= (byte)(mask >> (8 * i));
            }
        }
    }
}
