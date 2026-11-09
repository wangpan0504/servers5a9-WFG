using System.Text.Json;
using System.Text.Json.Nodes;
using PvfLib;

internal static class PairedCompanionMapTests
{
    internal static void Run(string pvf, string eligibility, string output)
    {
        int checks = 0;
        void Check(bool value, string label) { checks++; if (!value) throw new InvalidOperationException(label); }
        using var source = PvfArchive.Open(pvf);
        PairedCompanionMapDelta.Write(source, pvf, eligibility, output);
        using var delta = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(output, "delta.json")));
        var entries = delta.RootElement.GetProperty("entries").EnumerateArray().ToArray();
        Check(entries.Length == 11, "Actual cumulative source lacks exactly11 event maps");
        foreach (var entry in entries)
        {
            string path = entry.GetProperty("path").GetString()!;
            Check(path.StartsWith("map/gcontents/191210_tayberrs_road/", StringComparison.Ordinal), "No unrelated map changed");
            var before = TemplateDelta.Read(source, path);
            var after = JsonSerializer.Deserialize<List<ScriptToken>>(entry.GetProperty("tokens"))!;
            int end = before.FindIndex(t => t.Text == "[/ai character]");
            Check(end >= 0 && after.Count == before.Count + 16, "Exactly two six-integer actor rows appended");
            Check(before.Take(end).SequenceEqual(after.Take(end)) && before.Skip(end).SequenceEqual(after.Skip(end + 16)), "All old tokens and ordinals preserved");
            Check(after[end].Value == 74990 && after[end + 8].Value == 19973, "Both female support IDs in append order");
            Check(entry.GetProperty("expected_before_sha256").GetString() == MapMigration.Hash(source.ReadRaw(path)), "Actual raw before identity");
            Check(!entry.GetProperty("append").GetBoolean(), "Existing MAP replaced only");
        }
        var wrong = JsonNode.Parse(File.ReadAllBytes(eligibility))!;
        wrong["pvf"] = Path.Combine(output, "unrelated.pvf");
        string wrongPath = Path.Combine(output, "wrong-source.json"); File.WriteAllText(wrongPath, wrong.ToJsonString());
        bool rejected = false;
        try { PairedCompanionMapDelta.Write(source, pvf, wrongPath, output + "-rejected"); } catch (InvalidDataException) { rejected = true; }
        Check(rejected && !Directory.Exists(output + "-rejected"), "Wrong source identity rejected without partial output");
        rejected = false;
        try { PairedCompanionMapDelta.Write(source, pvf, eligibility, output); } catch (IOException) { rejected = true; }
        Check(rejected, "Output cannot be overwritten");
        Console.WriteLine($"PASS {checks} paired companion MAP checks; eleven actual event MAPs, existing tokens preserved");
    }
}
