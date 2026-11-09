using System.Text.Json;
using PvfLib;

internal static class SupportNaturalTierTests
{
    internal static void Run(string pvf, string evidence, string output)
    {
        int checks = 0;
        void Check(bool value, string message) { checks++; if (!value) throw new InvalidOperationException(message); }
        using var source = PvfArchive.Open(pvf);
        SupportNaturalTierDelta.Write(source, evidence, output);
        using var report = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(output, "natural-tier-report.json")));
        var rows = report.RootElement.GetProperty("templates").EnumerateArray().ToArray();
        Check(rows.Length == 134, "All female templates and variants covered");
        foreach (var row in rows)
        {
            int effective = row.GetProperty("tier").GetString() switch { "entry" => 20, "graduate" => 30, "huge" => 35, _ => -1 };
            Check(row.GetProperty("learned").GetInt32() + row.GetProperty("equipment_bonus").GetInt32() == effective, "Actual equipped bonus plus learning yields intended effective tier");
        }
        foreach (var group in rows.GroupBy(row => row.GetProperty("base_id").GetInt32()))
        foreach (int skill in group.First().GetProperty("passives").EnumerateArray().Select(row => row.GetProperty("skill").GetInt32()))
        {
            int Level(string tier) => group.First(row => row.GetProperty("tier").GetString() == tier)
                .GetProperty("passives").EnumerateArray().Single(row => row.GetProperty("skill").GetInt32() == skill).GetProperty("level").GetInt32();
            Check(Level("entry") < Level("graduate") && Level("graduate") < Level("huge"), "Real profession passive levels distinguish all three tiers");
        }
        using var delta = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(output, "delta.json")));
        int actors = 0;
        foreach (var entry in delta.RootElement.GetProperty("entries").EnumerateArray())
        {
            string path = entry.GetProperty("path").GetString()!;
            var after = entry.GetProperty("tokens").Deserialize<List<ScriptToken>>()!;
            Check(!entry.GetProperty("append").GetBoolean() && entry.GetProperty("expected_before_sha256").GetString() == MapMigration.Hash(source.ReadRaw(path)), "Only real source resources may be replaced");
            if (path == "etc/support_companion_profiles.etc")
            {
                Check(!after.Any(t => t.Text == "[equal tiers]"), "Tier catalog no longer claims equality");
                continue;
            }
            Check(path.StartsWith("aicharacter/support/femalecrusader/", StringComparison.Ordinal) || path.StartsWith("aicharacter/support/enchantress/", StringComparison.Ordinal), "No male/fighter/map/shared skill changes");
            var before = TemplateDelta.Read(source, path);
            Check(OutsideLearning(before).SequenceEqual(OutsideLearning(after)), "All native stat rates/equipment/KEY and AI references preserved");
            Check(!after.Any(t => t.Text == "[skill data up]"), "No artificial skill coefficient tier scaling");
            actors++;
        }
        Check(actors == 134, "Natural tier delta covers exactly134 female actors");
        bool rejected = false;
        try { SupportNaturalTierDelta.Write(source, evidence, output); } catch (IOException) { rejected = true; }
        Check(rejected, "Cannot overwrite existing candidate output");
        Console.WriteLine($"PASS {checks} real female skill tiers, equipment bonus and unchanged native stats/KEY checks");
    }
    static IEnumerable<ScriptToken> OutsideLearning(List<ScriptToken> tokens)
    {
        bool skip = false;
        foreach (var token in tokens)
        {
            if (token.Kind == 3) skip = token.Text is "[skill]" or "[quick skill]";
            if (!skip) yield return token;
        }
    }
}
