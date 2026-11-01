using System.Security.Cryptography;
using System.Text.Json;
using PvfLib;

internal sealed record ResourceDelta(string Path, string RawName, byte[] Before, byte[] After)
{
    internal bool Changed => !Before.AsSpan().SequenceEqual(After);
    internal string BeforeHash => Convert.ToHexString(SHA256.HashData(Before)).ToLowerInvariant();

    internal static void Run(PvfArchive source, IReadOnlyList<ResourceDelta> patches, string target, bool verify, bool combined)
    {
        string output = System.IO.Path.GetFullPath(target);
        bool rawDirectory = Directory.Exists(output);
        if (patches.Select(patch => patch.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != patches.Count ||
            patches.Select(patch => patch.RawName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != patches.Count)
            throw new InvalidDataException("Duplicate resource delta path");
        if (!verify && combined && (File.Exists(output) || rawDirectory && Directory.EnumerateFileSystemEntries(output).Any()))
            throw new IOException("Combined delta output must be a new or empty directory: " + output);
        using var candidate = verify && !rawDirectory ? PvfArchive.Open(output) : null;
        if (!verify) Directory.CreateDirectory(output);
        foreach (var patch in patches)
        {
            string rawPath = System.IO.Path.Combine(output, patch.RawName);
            if (verify)
            {
                byte[] actual = candidate is not null ? candidate.ReadRaw(patch.Path) : File.ReadAllBytes(rawPath);
                if (!actual.AsSpan().SequenceEqual(patch.After))
                    throw new InvalidDataException("Candidate does not contain the exact resource delta: " + patch.Path);
                Console.WriteLine($"VERIFY path={patch.Path} exactDelta=true");
            }
            else
            {
                File.WriteAllBytes(rawPath, patch.After);
                Console.WriteLine($"PATCH path={patch.Path} changed={patch.Changed.ToString().ToLowerInvariant()} bytes={patch.After.Length}");
            }
        }
        if (verify && combined && rawDirectory) VerifyManifest(patches, output);
        if (verify && combined && candidate is not null) VerifyUnchanged(source, candidate, patches);
        if (!verify)
        {
            var replacements = patches.Where(patch => patch.Changed).Select(patch => new
            {
                path = patch.Path, raw_file = System.IO.Path.Combine(output, patch.RawName), expected_before_sha256 = patch.BeforeHash
            });
            File.WriteAllText(System.IO.Path.Combine(output, "manifest.json"),
                JsonSerializer.Serialize(new { replacements }, new JsonSerializerOptions { WriteIndented = true }));
        }
        Console.WriteLine($"PASS resource deltas={patches.Count} changed={patches.Count(patch => patch.Changed)}; source PVF read-only; no whole-PVF hash");
    }

    static void VerifyUnchanged(PvfArchive source, PvfArchive candidate, IReadOnlyList<ResourceDelta> patches)
    {
        if (source.Format != candidate.Format || source.FileCount != candidate.FileCount)
            throw new InvalidDataException("Candidate archive format or file count changed");
        var actual = candidate.Files.ToDictionary(file => file.Path, StringComparer.Ordinal);
        var targetPaths = patches.Select(patch => patch.Path).ToHashSet(StringComparer.Ordinal);
        int unchanged = 0;
        foreach (var file in source.Files)
        {
            if (!actual.TryGetValue(file.Path, out var found) || found.DataType != file.DataType)
                throw new InvalidDataException("Candidate resource path or data type changed: " + file.Path);
            if (targetPaths.Contains(file.Path)) continue;
            if (file.Size != found.Size || !source.ReadRaw(file.Path).AsSpan().SequenceEqual(candidate.ReadRaw(file.Path)))
                throw new InvalidDataException("Unrelated resource changed: " + file.Path);
            unchanged++;
        }
        Console.WriteLine($"VERIFY unrelatedResources={unchanged} rawUnchanged=true metadataTypesUnchanged=true");
    }

    static void VerifyManifest(IReadOnlyList<ResourceDelta> patches, string output)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(System.IO.Path.Combine(output, "manifest.json")));
        var rows = document.RootElement.GetProperty("replacements").EnumerateArray().ToArray();
        var expected = patches.Where(patch => patch.Changed).ToDictionary(patch => patch.Path, StringComparer.Ordinal);
        if (rows.Length != expected.Count) throw new InvalidDataException("Combined manifest delta count changed");
        foreach (var row in rows)
        {
            string path = row.GetProperty("path").GetString() ?? "";
            if (!expected.Remove(path, out var patch) ||
                row.GetProperty("raw_file").GetString() != System.IO.Path.Combine(output, patch.RawName) ||
                row.GetProperty("expected_before_sha256").GetString() != patch.BeforeHash)
                throw new InvalidDataException("Combined manifest source guard or raw path changed: " + path);
        }
    }
}
