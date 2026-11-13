using System.Text.Json;
using System.Text.Json.Nodes;
using PvfLib;
using static SupportBehaviorDelta;

internal static class SupportBalancedTierTests
{
    internal static void Run(string pvf, string evidence, string output)
    {
        int checks = 0;
        void Check(bool value, string reason) { checks++; if (!value) throw new InvalidOperationException(reason); }
        using var source = PvfArchive.Open(pvf);
        SupportBalancedTierDelta.Write(source, evidence, output);
        using var delta = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(output, "delta.json")));
        using var report = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(output, "balanced-tier-report.json")));
        var rows = report.RootElement.GetProperty("templates").EnumerateArray().ToArray();
        Check(rows.Length == 134 && rows.Select(r => r.GetProperty("id").GetInt32()).Distinct().Count() == 134, "All female variants must be balanced exactly once");
        var entries = delta.RootElement.GetProperty("entries").EnumerateArray().ToArray();
        Check(entries.Length == 134, "No male, player skill, equipment or map may be changed");
        foreach (var entry in entries)
        {
            string path = entry.GetProperty("path").GetString()!;
            Check(!entry.GetProperty("append").GetBoolean() && entry.GetProperty("expected_before_sha256").GetString() == MapMigration.Hash(source.ReadRaw(path)), "Raw concurrency guard retained");
            var before = TemplateDelta.Read(source, path);
            var after = entry.GetProperty("tokens").Deserialize<List<ScriptToken>>()!;
            var sourceModifiers = Section(before, "skill data up");
            var modifiers = Section(after, "skill data up");
            Check(sourceModifiers.Count == 35 && modifiers.Count == 49, "Replace old main additions with five main and two awakening corrections");
            Set(before, "skill data up", []);
            Set(after, "skill data up", []);
            Check(before.SequenceEqual(after), "Appearance, AI, quick keys, equipment, passive and learned levels remain identical");
            Check(modifiers.Where((_, i) => i % 7 == 1).Select(t => t.Value).Distinct().Count() == 2 &&
                modifiers.Where((_, i) => i % 7 == 1).All(t => t.Value is 14 or 33 or 335 or 343), "Do not change favoritism, song, doll buffs or damage skills");
        }
        foreach (var row in rows)
        {
            int tier = row.GetProperty("tier").GetInt32();
            int intelligence = new[] { 7500, 9000, 11000 }[tier];
            Check(row.GetProperty("applicable_intelligence").GetInt32() == intelligence && row.GetProperty("awakening_level").GetInt32() == 40, "Native bridge and effective awakening levels paired");
            int columnIndex = 0;
            foreach (var column in row.GetProperty("columns").EnumerateArray())
            {
                int baseline = column.GetProperty("baseline").GetInt32(), divisor = column.GetProperty("divisor").GetInt32();
                float correction = column.GetProperty("percent").GetSingle();
                float value = baseline * (1 + correction / 100) * (1 + (float)intelligence / divisor);
                double target = columnIndex < 3 ? new[] { 850, 1250, 2350 }[tier] : columnIndex < 5 ? new[] { 4950, 7750, 14750 }[tier] :
                    1668 * (1 + new[] { 5350, 6400, 8650 }[tier] / 750.0);
                Check(Math.Abs(value - target) < 0.01, "Base BUFF and awakening must match the independent same-tier male reference");
                Check(correction > -100 && float.IsFinite(correction), "Never invert or invalidate native effects");
                if (columnIndex < 5)
                    Check(value * 1.15f > value, "Favoritism remains an extra native increase, not pre-subtracted from the base");
                columnIndex++;
            }
            Check(columnIndex == 7, "Both main and awakening stat columns covered");
        }
        bool rejected = false;
        try { SupportBalancedTierDelta.Write(source, evidence, output); } catch (IOException) { rejected = true; }
        Check(rejected, "Never overwrite a prior candidate");
        foreach (string field in new[] { "equipment_bonus", "kit", "template_id" })
        {
            var bad = JsonNode.Parse(File.ReadAllText(evidence))!;
            var first = bad["templates"]!.AsArray().First(r => r!["base_id"]!.GetValue<int>() == 74990)!;
            if (field == "kit") first["kit"]![0] = -1;
            else if (field == "template_id") first[field] = -1;
            else first["skills"]!.AsArray().Single(r => r!["skill"]!.GetValue<int>() == 14)![field] = -1;
            string badPath = Path.Combine(output, "invalid-" + field + ".json"), badOutput = output + "-invalid-" + field;
            File.WriteAllText(badPath, bad.ToJsonString());
            rejected = false;
            try { SupportBalancedTierDelta.Write(source, badPath, badOutput); } catch (InvalidDataException) { rejected = true; }
            Check(rejected && !Directory.Exists(badOutput), "Invalid equipment or identity proof cannot create a partial candidate");
        }
        Console.WriteLine($"PASS {checks} female balance identity, retained gameplay, male-reference main/awakening and evidence rejection checks");
    }
}
