using System.Text.Json;
using PvfLib;

internal static class SupportAuxiliaryTests
{
    internal static void Run(string pvf, string output)
    {
        int count = 0;
        void Check(bool value, string message) { count++; if (!value) throw new InvalidOperationException(message); }
        using var source = PvfArchive.Open(pvf);
        SupportAuxiliaryDelta.Write(source, output);
        using var report = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(output, "auxiliary-report.json")));
        foreach (var row in report.RootElement.GetProperty("templates").EnumerateArray())
        {
            int id = row.GetProperty("id").GetInt32();
            var definition = TemplateDelta.Definitions.Single(d => d.Id == (id is >= 19000 and <= 19973 ? 19973 : 74990));
            var ordinals = row.GetProperty("ordinals").EnumerateObject().ToDictionary(p => int.Parse(p.Name), p => p.Value.GetInt32());
            var replay = new DecisionReplay(AiPrograms.Action(definition, skillOrdinals: ordinals), definition) { Party = true };
            if (definition.Id == 74990)
            {
                replay.Skills.Add(36);
                Check(replay.Run() == ordinals[36], "Missing wings triggers original skill KEY");
                replay.Buffs.Add(36);
                Check(replay.Run() == -1, "Sufficient wings must not be recast");
                replay.Skills.Clear(); replay.Skills.Add(91);
                Check(replay.Run() == -1, "Protection field requires real combat");
                replay.Enemy = new(50, 20, 0);
                Check(replay.Run() == ordinals[91], "Missing combat protection uses registered KEY");
            }
            else
            {
                replay.Skills.Add(341);
                Check(replay.Run() == -1, "Shelter does not consume dolls at full HP");
                replay.Hp = 30;
                Check(replay.Run() == ordinals[341], "Shelter is available for critical HP");
                replay.Hp = 100; replay.Enemy = new(50, 20, 0);
                foreach (int skill in new[] {344, 349})
                {
                    replay.Skills.Clear(); replay.Skills.Add(skill);
                    Check(replay.Run() == ordinals[skill], "Control or renewal uses actual auxiliary KEY");
                    replay.Enemy = new(4000, 800, 0);
                    Check(replay.Run() == -1, "Auxiliary attacks preserve actual target proximity");
                    replay.Enemy = new(50, 20, 0);
                }
            }
            replay.Skills.Clear();
            Check(replay.Run() == -1, "Native cooldown refusal cannot fall back to normal attack");
        }
        using var delta = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(output, "delta.json")));
        int actors = 0;
        foreach (var row in delta.RootElement.GetProperty("entries").EnumerateArray())
        {
            string path = row.GetProperty("path").GetString()!;
            Check(path.StartsWith("aicharacter/", StringComparison.Ordinal), "Auxiliary delta cannot modify shared skills/equipment/maps");
            if (!path.EndsWith(".aic", StringComparison.Ordinal)) continue;
            actors++;
            var before = TemplateDelta.Read(source, path);
            var after = row.GetProperty("tokens").Deserialize<List<ScriptToken>>()!;
            var oldKeys = SupportBehaviorDelta.Section(before, "key stream");
            Check(SupportBehaviorDelta.Section(after, "key stream").Take(oldKeys.Count).SequenceEqual(oldKeys), "Every old KEY ordinal is preserved");
            Check(SupportBehaviorDelta.Section(before, "equipment").SequenceEqual(SupportBehaviorDelta.Section(after, "equipment")), "Auxiliary learning preserves equipped resources");
        }
        Check(actors == 134, "Both female professions and all variants covered");
        Console.WriteLine($"PASS {count} female auxiliary decisions, cooldown and KEY/resource safety checks");
    }
}
