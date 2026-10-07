using System.Globalization;
using System.Text;
using static PvfLib.Tests.TestCheck;

namespace PvfLib.Tests;

internal static class ArchiveIndexTests
{
    public static void Run()
    {
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

    private static string ReferencePath(string path)
    {
        var parts = new List<string>();
        foreach (string part in path.Trim().Replace('\\', '/').ToLowerInvariant().Split('/', StringSplitOptions.RemoveEmptyEntries))
            if (part == "..") parts.RemoveAt(parts.Count - 1);
            else if (part != ".") parts.Add(part);
        return string.Join('/', parts);
    }
}
