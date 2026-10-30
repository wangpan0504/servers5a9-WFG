using System.Globalization;
using System.Text;
using static PvfLib.Tests.TestCheck;

namespace PvfLib.Tests;

internal static class ArchiveIndexTests
{
    public static void Run()
    {
        CanonicalSplitPaths();
        // 目录交错出现，文件可与目录同名；重复项使用不同内容，独立预期保留首项。
        var files = new List<(string Path, int Type, byte[] Bytes)>
        {
            ("ROOT", 2, [1]), ("root/child", 2, [2]), ("other/child", 2, [3]),
            ("/DIR\\A/../I.TXT/", 3, Encoding.Unicode.GetBytes("首项")),
            ("目录/Ä/FILE", 2, [4]), ("dir/i.txt", 2, [99]), ("root", 2, [99])
        };
        for (int i = 0; i < 257; i++)
            files.Add(($"dir{i % 13}/nested//./tmp/../item{i}.bin", 2, BitConverter.GetBytes(i)));
        var expected = new Dictionary<string, (int Type, byte[] Bytes)>(StringComparer.Ordinal);
        foreach (var file in files)
            expected.TryAdd(ReferencePath(file.Path), (file.Type, file.Bytes));

        foreach (bool variant in new[] { false, true })
        {
            using var archive = PvfArchive.OpenBytes(ArchiveFixture.Create(variant, files.ToArray()));
            var metadata = expected.Select(pair => new PvfFile(pair.Key, pair.Value.Type, pair.Value.Bytes.Length)).ToArray();
            That(archive.Files.SequenceEqual(metadata), "interleaved directories retain original first-entry enumeration order");
            That(archive.FileCount == expected.Count, "duplicate normalization retains one file");
            foreach (var pair in expected)
            {
                string query = " /./" + pair.Key.ToUpperInvariant().Replace('/', '\\') + "/ ";
                That(archive.FindFile(query) == new PvfFile(pair.Key, pair.Value.Type, pair.Value.Bytes.Length), "normalized lookup preserves metadata");
                That(archive.Contains(query) && archive.ReadRaw(query).SequenceEqual(pair.Value.Bytes), "lookup selects exact directory and first payload");
            }
            foreach (string missing in new[] { "", ".", "dir", "root/missing", "absent/child", "dir0/item0.bin", "dir0/nested/other.bin" })
            {
                That(!archive.Contains(missing) && archive.FindFile(missing) is null, "directory and missing leaf are not files");
                Reject<FileNotFoundException>(() => archive.ReadRaw(missing));
            }
            Reject<InvalidDataException>(() => archive.Contains("root/../../child"));
            var culture = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
                That(archive.ReadText("DIR/I.TXT") == "首项", "index normalization is culture independent");
            }
            finally { CultureInfo.CurrentCulture = culture; }
            Parallel.ForEach(expected, pair => That(archive.Contains(pair.Key) && archive.ReadRaw(pair.Key).SequenceEqual(pair.Value.Bytes),
                "concurrent index reads do not mutate directory or entry state"));
            using var iterator = archive.Files.GetEnumerator();
            That(iterator.MoveNext(), "enumeration can pause before disposal");
            archive.Dispose();
            Reject<ObjectDisposedException>(() => iterator.MoveNext());
            That(archive.FileCount == 0, "dispose drops shared-prefix index");

            using var empty = PvfArchive.OpenBytes(ArchiveFixture.Create(variant));
            That(empty.FileCount == 0 && !empty.Files.Any() && !empty.Contains("file"), "empty archive index remains usable");
            byte[] invalidDuplicate = ArchiveFixture.Create(variant, ("a", 2, [1]), ("A", 2, [2]));
            ArchiveFixture.Put(invalidDuplicate, 48 + 24 + 8, int.MaxValue);
            Reject<InvalidDataException>(() => PvfArchive.OpenBytes(invalidDuplicate));
        }
    }

    private static void CanonicalSplitPaths()
    {
        string[] directories = ["", "dir", "dir/nested", "DIR", "/dir", "dir/", "dir//nested", "dir/./nested",
            "dir/tmp/..", " dir", "dir ", "目录/ä", "İ", "..", "dir/../../"];
        string[] names = ["item.bin", "ITEM.BIN", "child/item.bin", "child\\item.bin", ".", "..", "",
            " item.bin", "item.bin ", "\titem.bin\n", "../item.bin", "ä.bin", "İ.bin", "x y.bin"];
        foreach (bool variant in new[] { false, true })
        {
            var valid = new List<(string Directory, string Name, int Type, byte[] Bytes)>();
            var expected = new Dictionary<string, (int Type, byte[] Bytes, string Query)>(StringComparer.Ordinal);
            foreach (string directory in directories)
                foreach (string name in names)
                {
                    string? normalized = null;
                    try { normalized = ReferencePath(directory + "/" + name); }
                    catch (ArgumentOutOfRangeException) { }
                    if (string.IsNullOrEmpty(normalized))
                    {
                        byte[] invalid = ArchiveFixture.CreateParts(variant, (directory, name, 2, [1]));
                        Reject<InvalidDataException>(() => PvfArchive.OpenBytes(invalid));
                        continue;
                    }
                    byte[] payload = BitConverter.GetBytes(valid.Count);
                    valid.Add((directory, name, 2, payload));
                    expected.TryAdd(normalized, (2, payload, directory + "/" + name));
                }
            using var archive = PvfArchive.OpenBytes(ArchiveFixture.CreateParts(variant, valid.ToArray()));
            That(archive.Files.SequenceEqual(expected.Select(pair => new PvfFile(pair.Key, pair.Value.Type, pair.Value.Bytes.Length))),
                "分离目录和叶名的快慢路径保留完整有序元数据及规范化首项");
            // 原始查询只规范化一次；斜杠内的边缘空白不能被第二次规范化误删。
            foreach (var pair in expected)
            {
                That(archive.FindFile(pair.Value.Query) == new PvfFile(pair.Key, pair.Value.Type, pair.Value.Bytes.Length),
                    "分离目录与叶名的原始查询保留规范化元数据");
                That(archive.ReadRaw(pair.Value.Query).SequenceEqual(pair.Value.Bytes), "快慢路径交错重复保持首项内容");
            }

            byte[] shared = ArchiveFixture.CreateParts(variant, ("dir/nested", "one", 2, [1]), ("dir/nested", "two", 2, [2]));
            ArchiveFixture.Put(shared, 48 + 24 + 4, System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(shared.AsSpan(48 + 4)));
            using var repeatedDirectory = PvfArchive.OpenBytes(shared);
            That(repeatedDirectory.ReadRaw("dir/nested/one").SequenceEqual(new byte[] { 1 }) &&
                repeatedDirectory.ReadRaw("dir/nested/two").SequenceEqual(new byte[] { 2 }), "同原始目录偏移复用规范状态不串叶名");
            byte[] invalidDuplicate = ArchiveFixture.CreateParts(variant, ("dir", "one", 2, [1]), ("dir", "one", 2, [2]));
            ArchiveFixture.Put(invalidDuplicate, 48 + 24 + 8, int.MaxValue);
            Reject<InvalidDataException>(() => PvfArchive.OpenBytes(invalidDuplicate));
        }
    }

    private static string ReferencePath(string path)
    {
        var parts = new List<string>();
        foreach (string part in path.Trim().Replace('\\', '/').ToLowerInvariant().Split('/', StringSplitOptions.RemoveEmptyEntries))
            if (part == "..") parts.RemoveAt(parts.Count - 1);
            else if (part != ".") parts.Add(part);
        return string.Join('/', parts);
    }
}
