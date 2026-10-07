using System.Text;
using static PvfLib.Tests.TestCheck;

namespace PvfLib.Tests;

internal static class ArchiveBoundaryTests
{
    public static void Run()
    {
        PvfToken[] tokens = [new(0, -42), new(2, BitConverter.SingleToInt32Bits(1.5f)), new(3, 0), new(6, 1), new(5, 10), new(7, 11), new(9, 12), new(255, 13)];
        foreach (bool variant in new[] { false, true })
        {
            byte[] fixture = ArchiveFixture.Create(variant,
                ("script.bin", 1, ArchiveFixture.Tokens(tokens)),
                ("a.txt", 3, Encoding.Unicode.GetBytes("甲A")),
                ("b.txt", 3, Encoding.Unicode.GetBytes("乙B")),
                ("large.bin", 2, new byte[128]), ("bad.bin", 1, [0, 1, 2]), ("empty.bin", 2, []));
            using var archive = PvfArchive.OpenBytes(fixture, new() { ChunkCacheBytes = 64, TextCacheBytes = 64 });
            That(archive.ReadTokens("script.bin").SequenceEqual(tokens), "all token types and exact values preserved");
            That(archive.ReadText("script.bin") == "-42 1.50 \nscript.bin\n`甲A` \n{5=``}\n{7=``}", "text projection preserves legacy formatting");
            That(archive.ResolveString(-1) == "" && archive.ResolveString(1) == "甲A", "negative and wide string offsets");
            Reject<InvalidDataException>(() => archive.ResolveString(int.MaxValue));
            Reject<InvalidDataException>(() => archive.ReadTokens("a.txt"));
            Reject<InvalidDataException>(() => archive.ReadTokens("missing"));
            Reject<FileNotFoundException>(() => archive.ReadText("missing"));
            Reject<InvalidDataException>(() => archive.ReadTokens("bad.bin"));
            Reject<InvalidDataException>(() => archive.ReadText("bad.bin"));
            Reject<InvalidDataException>(() => archive.ReadText("large.bin"));
            byte[] owned = archive.ReadRaw("a.txt");
            _ = archive.ReadText("a.txt");
            _ = archive.ReadText("b.txt");
            That(archive.CachedChunks == 1 && archive.CachedChunkBytes == 64 && archive.CachedTextBytes == 64, "eviction enforces separate byte budgets");
            That(archive.ReadText("a.txt") == "甲A", "evicted text reloads unchanged");
            owned[0] = 0;
            That(archive.ReadRaw("a.txt")[0] != 0, "caller cannot corrupt a reloaded chunk");
            That(archive.ReadRaw("large.bin").Length == 128 && archive.CachedChunkBytes <= 64, "oversized chunk is read without retention");
            That(archive.ReadRaw("empty.bin").Length == 0, "empty decoded payload is supported");
            Parallel.For(0, 200, i =>
            {
                string path = i % 2 == 0 ? "a.txt" : "b.txt";
                That(archive.ReadText(path) == (i % 2 == 0 ? "甲A" : "乙B"), "concurrent eviction preserves text");
                That(archive.ReadTokens("script.bin").SequenceEqual(tokens), "concurrent token reads preserve arrays");
            });
            That(archive.CachedChunkBytes <= 64 && archive.CachedTextBytes <= 64, "concurrent cache stays bounded");
            archive.Dispose();
            archive.Dispose();
            Reject<ObjectDisposedException>(() => archive.ReadText("a.txt"));
            Reject<ObjectDisposedException>(() => archive.ReadRaw("a.txt"));
            Reject<ObjectDisposedException>(() => archive.ReadTokens("script.bin"));
            Reject<ObjectDisposedException>(() => archive.ResolveString(0));
            Reject<ObjectDisposedException>(() => archive.Contains("a.txt"));
            Reject<ObjectDisposedException>(() => archive.FindFile("a.txt"));
            Reject<ObjectDisposedException>(() => archive.Files.ToArray());
            That(archive.CachedChunks == 0 && archive.CachedTextBytes == 0, "dispose releases caches");

            using var uncached = PvfArchive.OpenBytes(fixture, new() { ChunkCacheBytes = 0, TextCacheBytes = 0 });
            _ = uncached.ReadText("a.txt");
            _ = uncached.ReadTokens("script.bin");
            int scanned = 0;
            uncached.ScanRawFiles(uncached.Files, (file, read) =>
            {
                That(read().Length == file.Size, "scan reads every payload including empty/unsupported scripts");
                scanned++;
            });
            That(scanned == 6 && uncached.CachedChunks == 0 && uncached.CachedTextBytes == 0, "scan and disabled caches do not retain chunks/text");
            Reject<ArgumentOutOfRangeException>(() => PvfArchive.OpenBytes(fixture, new() { TextCacheBytes = -1 }));
            Reject<ArgumentOutOfRangeException>(() => PvfArchive.OpenBytes(fixture, new() { ChunkCacheBytes = -1 }));
            Malformed(fixture, variant);
            FileLifecycle(fixture);
        }
    }

    private static void Malformed(byte[] fixture, bool variant)
    {
        foreach (int length in new[] { 0, 47, 48, fixture.Length / 2, fixture.Length - 1 })
            Reject<InvalidDataException>(() => PvfArchive.OpenBytes(fixture.AsSpan(0, length)));
        byte[] bad = (byte[])fixture.Clone();
        ArchiveFixture.Put(bad, 48 + 8, 99999);
        Reject<InvalidDataException>(() => PvfArchive.OpenBytes(bad));
        bad = (byte[])fixture.Clone();
        ArchiveFixture.Put(bad, 48 + 12, int.MaxValue);
        Reject<InvalidDataException>(() => PvfArchive.OpenBytes(bad));
        bad = (byte[])fixture.Clone();
        ArchiveFixture.Put(bad, 48, int.MaxValue);
        Reject<InvalidDataException>(() => PvfArchive.OpenBytes(bad));
        foreach (int size in new[] { 39, 41 })
        {
            bad = (byte[])fixture.Clone();
            // 文件本身缩小到 1 字节，目录合法，但分块实际解压 40 字节与声明不符。
            ArchiveFixture.Put(bad, 48 + 16, 1);
            ArchiveFixture.RewriteGroupSize(bad, variant, size);
            using var archive = PvfArchive.OpenBytes(bad);
            Reject<InvalidDataException>(() => archive.ReadRaw("script.bin"));
            That(archive.CachedChunks == 0, "failed decode does not enter the cache");
        }
    }

    private static void FileLifecycle(byte[] fixture)
    {
        string path = Path.Combine(Path.GetTempPath(), "pvflib-" + Guid.NewGuid().ToString("N") + ".pvf");
        try
        {
            File.WriteAllBytes(path, fixture);
            using (var archive = PvfArchive.Open(path))
            {
                That(archive.ReadText("a.txt") == "甲A", "file source reads exact offsets");
                Parallel.For(0, 30, i => That(archive.ReadRaw(i % 2 == 0 ? "a.txt" : "b.txt").Length == 4, "concurrent file reads"));
                if (OperatingSystem.IsWindows())
                    Reject<IOException>(() => { using var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite); });
            }
            using (var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
                writer.SetLength(10);
            Reject<InvalidDataException>(() => PvfArchive.Open(path));
            using var exclusive = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
            That(exclusive.Length == 10, "failed open closes the file handle");
        }
        finally
        {
            File.Delete(path);
        }
    }
}
