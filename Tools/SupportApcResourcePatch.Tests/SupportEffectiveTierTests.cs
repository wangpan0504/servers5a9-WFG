using System.Text.Json;
using System.Text.Json.Nodes;
using PvfLib;
using static SupportBehaviorDelta;

internal static class SupportEffectiveTierTests
{
    internal static void Run(string pvf, string evidence, string output)
    {
        int checks = 0;
        void Check(bool value, string reason) { checks++; if (!value) throw new InvalidOperationException(reason); }
        using var source = PvfArchive.Open(pvf);
        SupportEffectiveTierDelta.Write(source, evidence, output);
        using var delta = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(output, "delta.json")));
        using var report = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(output, "effective-tier-report.json")));
        var rows = report.RootElement.GetProperty("templates").EnumerateArray().ToDictionary(r => r.GetProperty("id").GetInt32());
        var registry = TemplateDelta.Read(source, "aicharacter/aicharacter.lst");
        var ids = Enumerable.Range(0, registry.Count / 2).Where(i => rows.ContainsKey(registry[i * 2].Value))
            .ToDictionary(i => PvfArchive.NormalizePath("aicharacter/" + registry[i * 2 + 1].Text), i => registry[i * 2].Value);
        Check(rows.Count == 134, "All dedicated female IDs must be present");
        int count = 0;
        foreach (var entry in delta.RootElement.GetProperty("entries").EnumerateArray())
        {
            string path = entry.GetProperty("path").GetString()!;
            Check(!entry.GetProperty("append").GetBoolean() && entry.GetProperty("expected_before_sha256").GetString() == MapMigration.Hash(source.ReadRaw(path)), "Source identity is mandatory");
            Check(path.StartsWith("aicharacter/support/femalecrusader/", StringComparison.Ordinal) || path.StartsWith("aicharacter/support/enchantress/", StringComparison.Ordinal), "Do not modify player skills, gear, male profiles or maps");
            var before = TemplateDelta.Read(source, path);
            var after = entry.GetProperty("tokens").Deserialize<List<ScriptToken>>()!;
            Check(after.Take(before.Count).SequenceEqual(before), "Every original token including learned levels, equipment, AI, KEY, rates and active skills is retained");
            Check(after.Count == before.Count + 37 && after[before.Count].Text == "[skill data up]" && after[^1].Text == "[/skill data up]", "Exactly five native main BUFF column adjustments");
            bool female = path.StartsWith("aicharacter/support/femalecrusader/", StringComparison.Ordinal);
            var row = rows[ids[path]];
            string tier = row.GetProperty("tier").GetString()!;
            int tierIndex = tier == "entry" ? 0 : tier == "graduate" ? 1 : 2;
            int intelligence = new[] { 6000, 7200, 9000 }[tierIndex];
            int minStrength = new[] { 4950, 7750, 14750 }[tierIndex], minAttack = new[] { 850, 1250, 2350 }[tierIndex];
            Check(row.GetProperty("applicable_intelligence").GetInt32() == intelligence, "Stat calibration and resource floor use the user-selected tiers");
            var raw = TemplateDelta.Read(source, female ? "skill/atpriest/blessofcourage.skl" : "skill/mage/forbiddencurse.skl");
            int begin = raw.FindIndex(t => t.Text == "[dungeon]"), end = raw.FindIndex(t => t.Text == "[/dungeon]");
            var dungeon = raw.GetRange(begin + 1, end - begin - 1);
            var table = Section(dungeon, "level info");
            var constants = Section(dungeon, "static data");
            int level = row.GetProperty("effective").GetInt32();
            var modifiers = Section(after, "skill data up");
            for (int i = 0; i < 5; i++)
            {
                var m = modifiers.GetRange(i * 7, 7);
                int column = i + (female ? 1 : 2);
                Check(m[0].Text == (female ? "[at priest]" : "[mage]") && m[1].Value == (female ? 14 : 335) &&
                    m[2].Text == "[dungeon]" && m[3].Text == "[level]" && m[4].Value == column && m[5].Text == "+" && m[6].Kind == 0 && m[6].Value > 0,
                    "Only correct profession/main skill/ordinary dungeon base columns may change");
                int baseline = table[1 + (level - 1) * table[0].Value + column].Value;
                int divisor = constants[column - (female ? 0 : 1)].Value;
                int minimum = i < 3 ? minAttack : minStrength;
                // 模拟原生整数基础列及单精度智力放大，先不用任何装备、圣歌、偏爱加成。
                int applied = (int)((baseline + m[6].Value) * (1.0f + (float)intelligence / divisor));
                Check(applied >= minimum, "Real buff formula must meet corresponding male tier without optional buffs");
                Check((int)(applied * 1.15f) >= minimum && (int)(applied * 1.3f) >= minimum, "Favoritism/song remain optional increases");
                Check(applied - minimum < 32, "Base adjustment must not overcorrect the requested minimum");
            }
            count++;
        }
        Check(count == 134, "No female profile or appearance may be omitted");
        bool rejected = false;
        try { SupportEffectiveTierDelta.Write(source, evidence, output); } catch (IOException) { rejected = true; }
        Check(rejected, "Existing candidates cannot be overwritten");
        foreach (string field in new[] { "id", "tier", "learned", "effective" })
        {
            var bad = JsonNode.Parse(File.ReadAllText(evidence))!;
            var first = bad["templates"]![0]!;
            first[field] = field == "tier" ? JsonValue.Create("huge") : JsonValue.Create(-1);
            string invalidEvidence = Path.Combine(output, "invalid-" + field + ".json");
            File.WriteAllText(invalidEvidence, bad.ToJsonString());
            string invalidOutput = output + "-invalid-" + field;
            rejected = false;
            try { SupportEffectiveTierDelta.Write(source, invalidEvidence, invalidOutput); }
            catch (InvalidDataException) { rejected = true; }
            Check(rejected && !Directory.Exists(invalidOutput), "Mismatched " + field + " evidence must reject without a partial candidate");
        }
        Console.WriteLine($"PASS {checks} dedicated female tier identity, native column math, male minimum and preservation checks");
    }
}
