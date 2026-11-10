using System.Text.Json;
using PvfLib;

// 用户明确要求四类支援准入一致；只补正式可达且原有专用支援行的地图。
internal static class PairedCompanionMapDelta
{
    internal static void Write(PvfArchive source, string sourcePath, string eligibilityPath, string outputPath)
    {
        if (Directory.Exists(outputPath) || File.Exists(outputPath)) throw new IOException("Output already exists: " + outputPath);
        using var eligibility = JsonDocument.Parse(File.ReadAllBytes(eligibilityPath));
        var root = eligibility.RootElement;
        if (!string.Equals(Path.GetFullPath(root.GetProperty("pvf").GetString()!), Path.GetFullPath(sourcePath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Map eligibility belongs to another source");
        var blocked = root.GetProperty("blocked").EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        var entries = new List<object>(); var evidence = new List<object>(); var skipped = new List<object>();
        foreach (var item in root.GetProperty("allowed").EnumerateObject())
        {
            string path = item.Name;
            if (!path.StartsWith("map/", StringComparison.Ordinal) || !path.EndsWith(".map", StringComparison.Ordinal) || path.Split('/').Any(p => p is "." or ".."))
                throw new InvalidDataException("Invalid approved MAP path");
            if (blocked.Contains(path)) { skipped.Add(new { path, reason = "shared-with-excluded-mode" }); continue; }
            if (!source.Contains(path)) throw new InvalidDataException("Approved MAP is missing: " + path);
            var raw = source.ReadTokens(path);
            if (!raw.Any(t => t.Type == 3 && source.ResolveString(t.Value) == "[ai character]")) continue;
            var rows = MapMigration.Rows(source, path);
            var original = rows.Where(r => r.Values[0] is 20900 or 9986).ToArray();
            if (original.Length == 0) continue;
            if (original.Select(r => r.Values[0]).Distinct().Count() != original.Length ||
                original.Any(r => r.Faction != "[character]" || r.Rank != "[normal]" || r.Values.Length != 6) ||
                original.Any(r => !r.Values.AsSpan(1).SequenceEqual(original[0].Values.AsSpan(1))))
                throw new InvalidDataException("Ambiguous existing support placement: " + path);
            var missing = new List<int>();
            foreach (int id in new[] { 74990, 19973 })
            {
                var found = rows.Where(r => r.Values[0] == id).ToArray();
                if (found.Length == 0) missing.Add(id);
                else if (found.Length != 1 || found[0].Faction != original[0].Faction || found[0].Rank != original[0].Rank ||
                    !found[0].Values.AsSpan(1).SequenceEqual(original[0].Values.AsSpan(1)))
                    throw new InvalidDataException("Existing female support placement differs: " + path);
            }
            evidence.Add(new { path, owners = item.Value, existing_ids = rows.Select(r => r.Values[0]), missing });
            if (missing.Count == 0) continue;
            var tokens = TemplateDelta.Read(source, path);
            int end = tokens.FindIndex(t => t.Kind == 3 && t.Text == "[/ai character]");
            if (end < 0) throw new InvalidDataException("Missing actor block end");
            var additions = new List<ScriptToken>();
            foreach (int id in missing)
            {
                additions.AddRange(original[0].Values.Take(4).Select((value, i) => ScriptToken.Int(i == 0 ? id : value)));
                additions.Add(ScriptToken.String(original[0].Faction)); additions.Add(ScriptToken.String(original[0].Rank));
                additions.AddRange(original[0].Values.Skip(4).Select(ScriptToken.Int));
            }
            tokens.InsertRange(end, additions);
            entries.Add(new { path, data_type = 1, append = false, expected_before_sha256 = MapMigration.Hash(source.ReadRaw(path)), tokens });
        }
        if (evidence.Count == 0) throw new InvalidDataException("No reachable dedicated support placements");
        Directory.CreateDirectory(outputPath);
        var options = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(Path.Combine(outputPath, "delta.json"), JsonSerializer.Serialize(new { schema = "support-profile-delta-v1", entries }, options));
        File.WriteAllText(Path.Combine(outputPath, "coverage.json"), JsonSerializer.Serialize(new { evidence, skipped, catalog_errors = root.GetProperty("errors"), source_read_only = true }, options));
        Console.WriteLine($"PAIRED_SUPPORT_MAP_DELTA reachableSupportMaps={evidence.Count} changedMaps={entries.Count} excludedShared={skipped.Count}");
    }
}
