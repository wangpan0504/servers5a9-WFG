using System.Text.Json;
using PvfLib;

internal static class ProfileDeltaAudit
{
    internal static void Run(string sourcePath, string candidatePath, string deltaPath, string reportPath)
    {
        if (File.Exists(reportPath)) throw new IOException("Audit report already exists");
        using var source = PvfArchive.Open(sourcePath, new() { ChunkCacheBytes = 512L * 1024 * 1024 });
        using var candidate = PvfArchive.Open(candidatePath, new() { ChunkCacheBytes = 512L * 1024 * 1024 });
        using var document = JsonDocument.Parse(File.ReadAllBytes(deltaPath));
        if (document.RootElement.GetProperty("schema").GetString() != "support-profile-delta-v1") throw new InvalidDataException("Unsupported delta");
        var entries = document.RootElement.GetProperty("entries").EnumerateArray().ToDictionary(e => e.GetProperty("path").GetString()!, StringComparer.Ordinal);
        int appends = 0, replacements = 0, nonTarget = 0, validatedMaps = 0;
        foreach (var pair in entries)
        {
            var entry = pair.Value;
            string path = pair.Key;
            bool append = entry.GetProperty("append").GetBoolean();
            if (!candidate.Contains(path) || source.Contains(path) == append || candidate.FindFile(path)!.DataType != entry.GetProperty("data_type").GetInt32())
                throw new InvalidDataException("Candidate entry identity mismatch: " + path);
            if (!append && MapMigration.Hash(source.ReadRaw(path)) != entry.GetProperty("expected_before_sha256").GetString())
                throw new InvalidDataException("Source resource changed: " + path);
            var wanted = JsonSerializer.Deserialize<List<ScriptToken>>(entry.GetProperty("tokens").GetRawText())!;
            var actual = candidate.ReadTokens(path);
            if (wanted.Count != actual.Length) throw new InvalidDataException("Token count mismatch: " + path);
            for (int i = 0; i < actual.Length; i++)
                if (actual[i].Type != wanted[i].Kind || (wanted[i].Text is string text
                    ? candidate.ResolveString(actual[i].Value) != text : actual[i].Value != wanted[i].Value))
                    throw new InvalidDataException($"Candidate token mismatch: {path}:{i}");
            if (append) appends++; else replacements++;
            if (path.StartsWith("map/", StringComparison.Ordinal) && path.EndsWith(".map", StringComparison.Ordinal))
            {
                var before = MapMigration.Rows(source, path);
                var after = MapMigration.Rows(candidate, path);
                if (after.Count != before.Count + 2 || after[^2].Values[0] != 74990 || after[^1].Values[0] != 19973 ||
                    before.Where((row, ordinal) => !row.Values.AsSpan().SequenceEqual(after[ordinal].Values) ||
                        row.Faction != after[ordinal].Faction || row.Rank != after[ordinal].Rank || row.Ordinal != after[ordinal].Ordinal).Any() ||
                    after.TakeLast(2).Any(row => row.Faction != "[character]" || row.Rank != "[normal]"))
                    throw new InvalidDataException("MAP actor rows or ordinals changed: " + path);
                validatedMaps++;
            }
        }
        foreach (var file in source.Files)
        {
            if (entries.ContainsKey(file.Path)) continue;
            var found = candidate.FindFile(file.Path);
            if (found is null || found.DataType != file.DataType || found.Size != file.Size ||
                !source.ReadRaw(file.Path).AsSpan().SequenceEqual(candidate.ReadRaw(file.Path)))
                throw new InvalidDataException("Non-target resource changed: " + file.Path);
            nonTarget++;
        }
        if (candidate.FileCount != source.FileCount + appends) throw new InvalidDataException("Unexpected additional resources");
        File.WriteAllText(reportPath, JsonSerializer.Serialize(new { source_pvf = Path.GetFullPath(sourcePath), candidate_pvf = Path.GetFullPath(candidatePath),
            delta = Path.GetFullPath(deltaPath), delta_sha256 = MapMigration.Hash(File.ReadAllBytes(deltaPath)), appends, replacements,
            non_target_raw_identical = nonTarget, validated_maps = validatedMaps, source_count = source.FileCount, candidate_count = candidate.FileCount,
            passed = true, source_read_only = true }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"AUDIT_PASS appends={appends} replacements={replacements} nonTargetRawIdentical={nonTarget}");
    }
}
