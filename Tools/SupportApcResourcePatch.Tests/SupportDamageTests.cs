using System.Text.Json;
using PvfLib;

internal static class SupportDamageTests
{
    internal static void Run(string pvf, string output)
    {
        using var source = PvfArchive.Open(pvf);
        int checks = 0;
        void Check(bool value, string message) { checks++; if (!value) throw new InvalidOperationException(message); }
        SupportDamageDelta.Write(source, output);
        using var delta = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(output, "delta.json")));
        var entries = delta.RootElement.GetProperty("entries").EnumerateArray().ToArray();
        Check(entries.Length == 168 && entries.Select(e => e.GetProperty("path").GetString()).Distinct().Count() == 168,
            "All dedicated identities must occur exactly once");
        foreach (var entry in entries)
        {
            string path = entry.GetProperty("path").GetString()!;
            var before = TemplateDelta.Read(source, path);
            var after = JsonSerializer.Deserialize<List<ScriptToken>>(entry.GetProperty("tokens").GetRawText())!;
            var rate = SupportBehaviorDelta.Section(after, "attack damage rate");
            Check(rate.Count == 1 && rate[0].Kind == 2 && rate[0].Value == BitConverter.SingleToInt32Bits(1f),
                "Dedicated actor must deal full native damage");
            Check(!entry.GetProperty("append").GetBoolean() && entry.GetProperty("data_type").GetInt32() == 1 &&
                entry.GetProperty("expected_before_sha256").GetString() == MapMigration.Hash(source.ReadRaw(path)),
                "Registered source raw identity must remain guarded");
            SupportBehaviorDelta.Set(before, "attack damage rate", [ScriptToken.Float(1f)]);
            Check(before.SequenceEqual(after), "Damage patch must preserve every other actor token");
        }
        bool rejected = false;
        try { SupportDamageDelta.Write(source, output); } catch (IOException) { rejected = true; }
        Check(rejected, "Existing output must remain protected");
        Console.WriteLine($"PASS {checks} support damage checks; 168 actors at full native damage; gameplay pending");
    }
}
