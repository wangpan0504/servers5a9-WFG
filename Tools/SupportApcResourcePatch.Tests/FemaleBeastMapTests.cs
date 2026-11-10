using System.Text.Json;
using System.Text.Json.Nodes;
using PvfLib;

internal static class FemaleBeastMapTests
{
    internal static void Run(string pvf, string approval, string output)
    {
        using var source = PvfArchive.Open(pvf);
        int checks = 0;
        void Check(bool condition) { checks++; if (!condition) throw new InvalidOperationException("Beast MAP regression failed: " + checks); }
        FemaleRestoreDelta.WriteBeastMaps(source, approval, output);
        using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(output, "delta.json")));
        var entries = document.RootElement.GetProperty("entries").EnumerateArray().ToArray();
        Check(entries.Length == 20);
        foreach (var entry in entries)
        {
            string path = entry.GetProperty("path").GetString()!;
            var before = TemplateDelta.Read(source, path);
            var after = JsonSerializer.Deserialize<List<ScriptToken>>(entry.GetProperty("tokens"))!;
            int end = before.FindIndex(t => t.Text == "[/ai character]");
            Check(end >= 0 && after.Count == before.Count + 16);
            Check(before.Take(end).SequenceEqual(after.Take(end)) && before.Skip(end).SequenceEqual(after.Skip(end + 16)));
            var rows = MapMigration.Rows(source, path);
            foreach (var pair in new[] { (Id:74990, Offset:0), (Id:19973, Offset:8) })
            {
                var row = after.Skip(end + pair.Offset).Take(8).ToArray();
                Check(row[0].Value == pair.Id && row[4].Text == "[character]" && row[5].Text == "[normal]");
                Check(new[] {row[1].Value,row[2].Value,row[3].Value,row[6].Value,row[7].Value}.SequenceEqual(rows[1].Values.Skip(1)));
            }
            Check(!entry.GetProperty("append").GetBoolean() && entry.GetProperty("expected_before_sha256").GetString() == MapMigration.Hash(source.ReadRaw(path)));
        }
        bool rejected = false;
        try { FemaleRestoreDelta.WriteBeastMaps(source, approval, output); } catch (IOException) { rejected = true; }
        Check(rejected);
        var mutations = new Action<JsonNode>[] {
            root => root["entries"]![0]!["expected_before_sha256"] = "changed",
            root => root["entries"]![0]!["path"] = "map/devildom_beast/legacy_mirror/not-approved.map",
            root => root["entries"]![0]!["owners"] = new JsonArray(),
            root => root["entries"]![0]!["owners"] = new JsonArray("5335/3"),
            root => root["entries"]![1] = root["entries"]![0]!.DeepClone(),
            root => root["entries"]!.AsArray().RemoveAt(0)
        };
        for (int i = 0; i < mutations.Length; i++)
        {
            var root = JsonNode.Parse(File.ReadAllText(approval))!;
            mutations[i](root);
            string invalid = Path.Combine(output, $"invalid-{i}.json"), target = Path.Combine(output, $"rejected-{i}");
            File.WriteAllText(invalid, root.ToJsonString());
            rejected = false;
            try { FemaleRestoreDelta.WriteBeastMaps(source, invalid, target); } catch (InvalidDataException) { rejected = true; }
            Check(rejected && !Directory.Exists(target));
        }
        Console.WriteLine($"PASS {checks} beast female MAP checks; original ordinals and non-actor tokens preserved; invalid approval rejected before output");
    }
}
