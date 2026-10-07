using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using PvfLib;
using static PvfLib.Tests.TestCheck;

namespace PvfLib.Tests;

internal static class PvfArchiveTests
{
    public static void Run()
    {
        foreach (var (input, expected) in new[]
        {
            (" /DIR\\test.TXT/ ", "dir/test.txt"), ("./dir//x/../test.txt", "dir/test.txt"),
            ("dir/./test.txt", "dir/test.txt"), ("dir/..", ""), ("/./", ""), ("dir/", "dir"), ("目录/NAME", "目录/name")
        })
            That(PvfArchive.NormalizePath(input) == expected, "PVF fast path preserves normalization: " + input);
        bool escaped = false;
        try
        {
            PvfArchive.NormalizePath("dir/../../outside");
        }
        catch (InvalidDataException)
        {
            escaped = true;
        }
        That(escaped, "PVF normalization still rejects escape above archive root");
        foreach (bool protectedFormat in new[] { false, true })
        {
            byte[] bytes = Fixture(protectedFormat);
            using var archive = PvfArchive.OpenBytes(bytes);
            bytes[^1] ^= 1; // OpenBytes 必须拥有自己的原始数据。
            That(archive.FileCount == 2 && archive.GroupCount == 1, "PVF compact index preserves normalized first-entry ownership");
            That(archive.Format == (protectedFormat ? "protected_nkpi" : "nkpi"), "PVF format identity");
            That(archive.FindFile(".\\DIR//test.TXT") == new PvfFile("dir/test.txt", 3, 4), "PVF lookup preserves normalized path, type and size");
            That(archive.FindFile("absent") is null && !archive.Contains("absent"), "missing PVF entry remains missing");
            int scanned = 0;
            archive.ScanRawFiles(archive.Files, (file, read) =>
            {
                byte[] raw = read();
                byte first = raw[0];
                raw[0] ^= 1;
                That(raw.Length == file.Size && read()[0] == first, "bulk scan reads exact independent file bytes");
                scanned++;
            });
            That(scanned == 2 && archive.CachedChunks == 0, "bulk scan does not retain decoded groups in the normal read cache");
            That(archive.Files.OrderBy(file => file.Path).SequenceEqual(new[] { new PvfFile("bin.dat", 2, 4), new PvfFile("dir/test.txt", 3, 4) }),
                "PVF enumeration matches lookup metadata and removes duplicate paths");
            That(archive.ReadText("dir/test.txt") == "甲A", "duplicate path keeps the first data offset");
            byte[] raw = archive.ReadRaw("bin.dat");
            That(raw.AsSpan().SequenceEqual(new byte[] { 1, 2, 3, 4 }), "raw file slice is unchanged");
            raw[0] = 0;
            That(archive.ReadRaw("bin.dat")[0] == 1 && archive.CachedChunks == 1, "raw result cannot mutate shared chunk cache");
        }
    }

    // 小型归档覆盖目录布局；不需要打开真实 PVF，也不把资源文件纳入仓库。
    static byte[] Fixture(bool protectedFormat)
    {
        byte[] raw = [.. Encoding.Unicode.GetBytes("甲A"), 1, 2, 3, 4];
        byte[] body = Compress(raw);
        Crypt(body, protectedFormat ? "bODy" : "BodY", protectedFormat);
        byte[] names = [.. new byte[8], .. Pool("test.txt\0dir\0bin.dat\0DIR\0TEST.TXT\0"u8.ToArray(), false), .. Pool([0, 0], true)];
        byte[] files = new byte[3 * 24];
        Entry(0, 0, 18, 0, 3); // test.txt / dir
        Entry(1, 26, -1, 4, 2); // bin.dat / 根目录
        Entry(2, 50, 42, 4, 2); // TEST.TXT / DIR，归一化后重复，不能覆盖首项
        byte[] group = new byte[8];
        Put(group, 0, body.Length);
        Put(group, 4, raw.Length);
        Crypt(group, protectedFormat ? "grpi" : "GRPI", protectedFormat);
        byte[] header = new byte[48];
        Put(header, 0, 0x69706B6E);
        Put(header, 24, 3);
        Put(header, 32, body.Length);
        Put(header, 36, 1);
        Put(header, 44, names.Length);
        Crypt(header, protectedFormat ? "hEAd" : "HeaD", protectedFormat);
        if (!protectedFormat)
            for (int i = 24; i < 28; i++)
                header[i] ^= 0x55;
        return [.. header, .. files, .. names, .. group, .. body];

        void Entry(int index, int name, int directory, int offset, int type)
        {
            int at = index * 24;
            Put(files, at, name);
            Put(files, at + 4, directory);
            Put(files, at + 12, offset);
            Put(files, at + 16, 4);
            Put(files, at + 20, type);
        }
        byte[] Pool(byte[] text, bool wide)
        {
            byte[] packed = Compress(text);
            Crypt(packed, wide ? (protectedFormat ? "StRw" : "sTrW") : (protectedFormat ? "StRa" : "sTrA"), protectedFormat, 0x269EC9);
            byte[] prefix = new byte[8];
            Put(prefix, 0, packed.Length ^ unchecked((int)(wide ? 0x9A82F037u : 0xAA74472Eu)));
            Put(prefix, 4, text.Length ^ packed.Length);
            return [.. prefix, .. packed];
        }
    }

    static byte[] Compress(byte[] bytes)
    {
        using var result = new MemoryStream();
        using (var stream = new ZLibStream(result, CompressionLevel.Fastest, leaveOpen: true))
            stream.Write(bytes);
        return result.ToArray();
    }
    static void Put(byte[] bytes, int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset), value);
    static void Crypt(Span<byte> bytes, string key, bool protectedKey, uint magic = 0x269EC3)
    {
        unchecked
        {
            uint factor = protectedKey ? 0x393u : 0x1C1u;
            uint seed = (protectedKey ? 0x339E9711u : 0x76826701u) * key[0] + factor * (key[3] + factor * (key[2] + factor * key[1]));
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
