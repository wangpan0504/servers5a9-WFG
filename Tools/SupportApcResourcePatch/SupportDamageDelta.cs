using System.Text.Json;
using PvfLib;
using static SupportBehaviorDelta;

// 只撤销专用支援兵的额外伤害折扣，装备、技能、难度与原生伤害计算继续生效。
internal static class SupportDamageDelta
{
    internal static void Write(PvfArchive source, string output)
    {
        if (Directory.Exists(output) || File.Exists(output)) throw new IOException("Output already exists: " + output);
        var registry = TemplateDelta.Read(source, "aicharacter/aicharacter.lst");
        if (registry.Count % 2 != 0) throw new InvalidDataException("Malformed AIC registry");
        var paths = Enumerable.Range(0, registry.Count / 2).ToDictionary(i => registry[2 * i].Value,
            i => PvfArchive.NormalizePath("aicharacter/" + registry[2 * i + 1].Text));
        var entries = new List<object>();
        foreach (var definition in TemplateDelta.Definitions)
        {
            int[] identities = definition.Id switch
            {
                20900 => [20900, 20903, 20904, 20905],
                9986 => [9986, ..Enumerable.Range(9600, 29)],
                74990 => [74990, ..Enumerable.Range(74000, 48)],
                19973 => [19973, ..Enumerable.Range(19000, 84)],
                _ => throw new InvalidDataException("Unsupported support identity")
            };
            foreach (int identity in identities)
            {
                if (!paths.TryGetValue(identity, out string? path) ||
                    !path.StartsWith(definition.Target[..(definition.Target.LastIndexOf('/') + 1)], StringComparison.Ordinal))
                    throw new InvalidDataException("Unregistered support path: " + identity);
                var tokens = TemplateDelta.Read(source, path);
                var info = Section(tokens, "minimum info");
                int job = definition.Id switch { 20900 => 4, 9986 => 1, 74990 => 14, _ => 3 };
                if (info.Count < 6 || info[1].Value != job || info[2].Value != definition.Grow || info[5].Value != 95)
                    throw new InvalidDataException("Support class, growth or level changed: " + path);
                var rate = Section(tokens, "attack damage rate");
                if (rate.Count != 1 || rate[0].Kind != 2 ||
                    rate[0].Value != BitConverter.SingleToInt32Bits(0.10f) && rate[0].Value != BitConverter.SingleToInt32Bits(1.0f))
                    throw new InvalidDataException("Unexpected support damage rate: " + path);
                Set(tokens, "attack damage rate", [ScriptToken.Float(1.0f)]);
                entries.Add(new { path, data_type = 1, append = false,
                    expected_before_sha256 = MapMigration.Hash(source.ReadRaw(path)), tokens });
            }
        }
        if (entries.Count != 168) throw new InvalidDataException("Incomplete support damage identities");
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "delta.json"), JsonSerializer.Serialize(
            new { schema = "support-profile-delta-v1", entries }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"SUPPORT_DAMAGE actors={entries.Count} multiplier=1; only attack damage rate changed");
    }
}
