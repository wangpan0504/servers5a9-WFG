using System.Text.Json;
using PvfLib;
using static SupportBehaviorDelta;

// 档次由真实学习等级及职业被动形成，不写固定智力或修改共享技能系数。
internal static class SupportNaturalTierDelta
{
    internal static void Write(PvfArchive source, string evidencePath, string output)
    {
        if (Directory.Exists(output) || File.Exists(output)) throw new IOException("Output already exists: " + output);
        using var evidence = JsonDocument.Parse(File.ReadAllBytes(evidencePath));
        var oldRows = evidence.RootElement.GetProperty("templates").EnumerateArray()
            .Where(r => r.GetProperty("base_id").GetInt32() is 74990 or 19973)
            .ToDictionary(r => r.GetProperty("template_id").GetInt32());
        if (oldRows.Count != 134) throw new InvalidDataException("Expected full female equipment/skill evidence");
        const string profilesPath = "etc/support_companion_profiles.etc";
        var profiles = TemplateDelta.Read(source, profilesPath);
        var tiers = new Dictionary<int, (int Base, string Tier)> { [74990] = (74990, "entry"), [19973] = (19973, "entry") };
        for (int i = 0; i < profiles.Count; i++)
        {
            if (profiles[i].Text != "[profile]") continue;
            if (i + 5 >= profiles.Count || profiles[i + 5].Text != "[/profile]") throw new InvalidDataException("Malformed profile row");
            int baseId = profiles[i + 1].Value;
            if (baseId is 74990 or 19973)
                tiers.Add(profiles[i + 3].Value, (baseId, profiles[i + 2].Text!));
        }
        if (tiers.Count != 134 || tiers.Keys.Any(id => !oldRows.ContainsKey(id))) throw new InvalidDataException("Tier and evidence identities differ");
        var registry = TemplateDelta.Read(source, "aicharacter/aicharacter.lst");
        var paths = Enumerable.Range(0, registry.Count / 2).ToDictionary(i => registry[2 * i].Value,
            i => PvfArchive.NormalizePath("aicharacter/" + registry[2 * i + 1].Text));
        var entries = new List<object>();
        var report = new List<object>();
        foreach (var (id, profile) in tiers)
        {
            int ordinal = profile.Tier switch { "entry" => 0, "graduate" => 1, "huge" => 2, _ => throw new InvalidDataException("Unknown support tier") };
            var definition = TemplateDelta.Definitions.Single(d => d.Id == profile.Base);
            string path = paths[id];
            var tokens = TemplateDelta.Read(source, path);
            var minimum = Section(tokens, "minimum info");
            if (minimum.Count < 6 || minimum[1].Value != (profile.Base == 74990 ? 14 : 3) ||
                minimum[2].Value != definition.Grow || minimum[5].Value != 95)
                throw new InvalidDataException("Natural tier actor identity changed");
            var wear = Section(tokens, "equipment");
            var expectedKit = oldRows[id].GetProperty("kit").EnumerateArray().Select(r => r.GetInt32()).ToArray();
            if (wear.Count % 3 != 0 || !Enumerable.Range(0, wear.Count / 3).Select(i => wear[3 * i].Value).TakeLast(expectedKit.Length).SequenceEqual(expectedKit))
                throw new InvalidDataException("Equipped kit differs from verified skill bonus evidence");
            int main = profile.Base == 74990 ? 14 : 335;
            var originalMain = oldRows[id].GetProperty("skills").EnumerateArray().Single(r => r.GetProperty("skill").GetInt32() == main);
            int bonus = originalMain.GetProperty("equipment_bonus").GetInt32();
            int effective = ordinal switch { 0 => 20, 1 => 30, _ => 35 };
            int requested = effective - bonus;
            var learned = Section(tokens, "skill");
            if (learned.Count % 2 != 0 || learned.Any(t => t.Kind != 0)) throw new InvalidDataException("Malformed learned skills");
            int mainAt = Pair(main);
            if (mainAt < 0 || learned[mainAt + 1].Value != originalMain.GetProperty("learned").GetInt32() || bonus < 0 || requested < 1 || effective > originalMain.GetProperty("table_levels").GetInt32())
                throw new InvalidDataException("Main skill differs from verified baseline or exceeds real table");
            learned[mainAt + 1] = ScriptToken.Int(requested);
            var index = TemplateDelta.Read(source, $"skill/{definition.Job}skill.lst");
            var skillPaths = Enumerable.Range(0, index.Count / 2).ToDictionary(i => index[2 * i].Value,
                i => PvfArchive.NormalizePath("skill/" + index[2 * i + 1].Text));
            var passiveReport = new List<object>();
            foreach (int skill in profile.Base == 74990 ? new[] {17, 34, 90} : new[] {369, 342, 347})
            {
                var raw = TemplateDelta.Read(source, skillPaths[skill]);
                if (Section(raw, "type") is not [{ Text: "[passive]" }]) throw new InvalidDataException("Tier source must be a real passive");
                int at = Pair(skill), maximum = Scalar(raw, "maximum level");
                int baseline = at >= 0 ? learned[at + 1].Value : Math.Min(maximum,
                    (95 - Scalar(raw, "required level")) / Scalar(raw, "required level range") + 1);
                int level = Math.Min(maximum, checked(baseline + ordinal * 5));
                if (baseline < 1 || baseline > maximum || level > 255) throw new InvalidDataException("Passive tier exceeds actual skill maximum");
                if (at >= 0) learned[at + 1] = ScriptToken.Int(level);
                else learned.AddRange([ScriptToken.Int(skill), ScriptToken.Int(level)]);
                passiveReport.Add(new { skill, baseline, level, maximum, path = skillPaths[skill] });
            }
            Set(tokens, "skill", learned);
            var quick = Section(tokens, "quick skill");
            for (int i = 0; i < quick.Count; i += 2) if (quick[i].Value == main) quick[i + 1] = ScriptToken.Int(requested);
            Set(tokens, "quick skill", quick);
            Add(path, tokens);
            report.Add(new { id, base_id = profile.Base, tier = profile.Tier, main_skill = main,
                equipment_bonus = bonus, learned = requested, effective, passives = passiveReport,
                intelligence = "native profession/equipment/passive calculation; no fixed override", final_runtime_panel = "pending" });

            int Pair(int skill) => Enumerable.Range(0, learned.Count / 2).Select(i => i * 2).FirstOrDefault(i => learned[i].Value == skill, -1);
        }
        // 现行三档已经分开，不能继续在目录里声明所有档次一致。
        int equal = profiles.FindIndex(t => t.Text == "[equal tiers]");
        if (profiles.Count(t => t.Text == "[equal tiers]") > 1) throw new InvalidDataException("Duplicate equal tiers marker");
        if (equal >= 0)
        {
            if (equal + 1 >= profiles.Count || profiles[equal + 1] is not { Kind: 0, Value: 1 }) throw new InvalidDataException("Malformed equal tiers marker");
            profiles.RemoveRange(equal, 2);
        }
        Add(profilesPath, profiles);
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "delta.json"), JsonSerializer.Serialize(new { schema = "support-profile-delta-v1", entries }, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(output, "natural-tier-report.json"), JsonSerializer.Serialize(new { templates = report,
            policy = "Main effective20/30/35; profession passives baseline/+5/+10 within real maximum; no shared SKL coefficient or fixed INT", gameplay = "pending" }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"SUPPORT_NATURAL_TIERS templates={report.Count}; native intelligence retained; runtime panel pending");
        void Add(string path, List<ScriptToken> tokens) => entries.Add(new { path, data_type = 1, append = false, expected_before_sha256 = MapMigration.Hash(source.ReadRaw(path)), tokens });
    }
    static int Scalar(List<ScriptToken> tokens, string tag) => Section(tokens, tag) is [{ Kind: 0 } token] ? token.Value : throw new InvalidDataException("Invalid skill scalar: " + tag);
}
