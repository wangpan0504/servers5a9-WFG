using System.Text.Json;
using PvfLib;
using static SupportBehaviorDelta;

// 取奶爸现行同档基础奶量，替换两女旧保底补正；偏爱和圣歌继续由原生另算。
internal static class SupportBalancedTierDelta
{
    internal static void Write(PvfArchive source, string equipmentEvidence, string output)
    {
        if (File.Exists(output) || Directory.Exists(output)) throw new IOException("Output already exists: " + output);
        using var evidence = JsonDocument.Parse(File.ReadAllBytes(equipmentEvidence));
        var verified = evidence.RootElement.GetProperty("templates").EnumerateArray()
            .Where(r => r.GetProperty("base_id").GetInt32() is 74990 or 19973)
            .ToDictionary(r => r.GetProperty("template_id").GetInt32());
        var registry = TemplateDelta.Read(source, "aicharacter/aicharacter.lst");
        var paths = Enumerable.Range(0, registry.Count / 2).ToDictionary(i => registry[2 * i].Value,
            i => PvfArchive.NormalizePath("aicharacter/" + registry[2 * i + 1].Text));
        var profiles = TemplateDelta.Read(source, "etc/support_companion_profiles.etc");
        var actors = new Dictionary<int, (int Base, int Tier)> { [74990] = (74990, 0), [19973] = (19973, 0) };
        for (int i = 0; i < profiles.Count; i++)
            if (profiles[i].Text == "[profile]" && profiles[i + 1].Value is 74990 or 19973)
            {
                if (profiles[i + 5].Text != "[/profile]") throw new InvalidDataException("Malformed profile");
                int tier = profiles[i + 2].Text switch { "entry" => 0, "graduate" => 1, "huge" => 2, _ => throw new InvalidDataException("Unknown tier") };
                actors.Add(profiles[i + 3].Value, (profiles[i + 1].Value, tier));
            }
        if (actors.Count != 134 || verified.Count != 134 || actors.Keys.Any(id => !verified.ContainsKey(id)))
            throw new InvalidDataException("All 134 female identities and equipment evidence are required");
        int[] intelligence = [7500, 9000, 11000], vitality = [5350, 6400, 8650];
        int[] strength = [4950, 7750, 14750], attack = [850, 1250, 2350];
        var fatherAwakening = Table("skill/priest/apocalypse.skl", 14);
        if (fatherAwakening.Constants.Skip(6).Take(4).Any(t => t.Value != 750))
            throw new InvalidDataException("Father awakening scaling changed");
        var entries = new List<object>();
        var report = new List<object>();
        foreach (var (id, actor) in actors)
        {
            bool female = actor.Base == 74990;
            string job = female ? "[at priest]" : "[mage]", path = paths[id];
            if (!path.StartsWith(female ? "aicharacter/support/femalecrusader/" : "aicharacter/support/enchantress/", StringComparison.Ordinal))
                throw new InvalidDataException("Not a dedicated female template");
            var tokens = TemplateDelta.Read(source, path);
            var minimum = Section(tokens, "minimum info");
            if (minimum.Count < 6 || minimum[1].Value != (female ? 14 : 3) || minimum[2].Value != (female ? 1 : 5) || minimum[5].Value != 95)
                throw new InvalidDataException("Female identity changed");
            var proof = verified[id];
            var kit = proof.GetProperty("kit").EnumerateArray().Select(v => v.GetInt32()).ToArray();
            var wear = Section(tokens, "equipment");
            if (wear.Count % 3 != 0 || !Enumerable.Range(0, wear.Count / 3).Select(i => wear[3 * i].Value).TakeLast(kit.Length).SequenceEqual(kit))
                throw new InvalidDataException("Equipment no longer matches verified skill-level evidence");
            int main = female ? 14 : 335, awakening = female ? 33 : 343;
            int level = new[] { 20, 30, 35 }[actor.Tier];
            var learned = Section(tokens, "skill");
            VerifyLevel(main, level);
            VerifyLevel(awakening, 40);
            var old = Section(tokens, "skill data up");
            if (old.Count != 35) throw new InvalidDataException("Unexpected existing AIC modifiers");
            for (int i = 0; i < 5; i++)
                if (old[7 * i].Text != job || old[7 * i + 1].Value != main || old[7 * i + 2].Text != "[dungeon]" ||
                    old[7 * i + 3].Text != "[level]" || old[7 * i + 4].Value != i + (female ? 1 : 2) ||
                    old[7 * i + 5].Text != "+" || old[7 * i + 6].Kind != 0 || old[7 * i + 6].Value < 0)
                    throw new InvalidDataException("Existing main-BUFF reconciliation requires review");
            var mainTable = Table(female ? "skill/atpriest/blessofcourage.skl" : "skill/mage/forbiddencurse.skl", 8);
            var awakeningTable = Table(female ? "skill/atpriest/cruxofvictoria.skl" : "skill/mage/marionette.skl", female ? 12 : 10);
            var modifiers = new List<ScriptToken>();
            var columns = new List<object>();
            for (int i = 0; i < 5; i++)
            {
                int column = i + (female ? 1 : 2), divisor = mainTable.Constants[i + 1].Value;
                if (divisor != (female ? 675 : 665)) throw new InvalidDataException("Main BUFF divisor changed");
                Add(main, column, mainTable.Values[1 + (level - 1) * 8 + column].Value,
                    i < 3 ? attack[actor.Tier] : strength[actor.Tier], divisor);
            }
            for (int i = 0; i < 2; i++)
            {
                int column = (female ? 4 : 3) + i, divisor = awakeningTable.Constants[(female ? 3 : 1) + i].Value;
                if (divisor != 750) throw new InvalidDataException("Female awakening divisor changed");
                double target = fatherAwakening.Values[1 + 39 * 14 + i].Value * (1.0 + vitality[actor.Tier] / 750.0);
                Add(awakening, column, awakeningTable.Values[1 + 39 * (female ? 12 : 10) + column].Value, target, divisor);
            }
            Set(tokens, "skill data up", modifiers);
            entries.Add(new { path, data_type = 1, append = false, expected_before_sha256 = MapMigration.Hash(source.ReadRaw(path)), tokens });
            report.Add(new { id, base_id = actor.Base, tier = actor.Tier, main_level = level, awakening_level = 40,
                applicable_intelligence = intelligence[actor.Tier], male_reference_vitality = vitality[actor.Tier], columns });

            void VerifyLevel(int skill, int expected)
            {
                var data = proof.GetProperty("skills").EnumerateArray().Single(r => r.GetProperty("skill").GetInt32() == skill);
                int at = Enumerable.Range(0, learned.Count / 2).Select(i => 2 * i).Single(i => learned[i].Value == skill);
                if (learned[at + 1].Value + data.GetProperty("equipment_bonus").GetInt32() != expected || data.GetProperty("table_levels").GetInt32() < expected)
                    throw new InvalidDataException("Current effective skill level differs from equipment proof");
            }
            void Add(int skill, int column, int baseline, double target, int divisor)
            {
                float percent = checked((float)(100 * (target / (baseline * (1.0 + intelligence[actor.Tier] / (double)divisor)) - 1)));
                if (baseline <= 0 || !float.IsFinite(percent) || percent <= -100 || percent > 1000)
                    throw new InvalidDataException("Unsupported normalization magnitude");
                modifiers.AddRange([ScriptToken.String(job), ScriptToken.Int(skill), ScriptToken.String("[dungeon]"),
                    ScriptToken.String("[level]"), ScriptToken.Int(column), ScriptToken.String("%"), ScriptToken.Float(percent)]);
                columns.Add(new { skill, column, baseline, divisor, percent, target });
            }
        }
        Directory.CreateDirectory(output);
        var options = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(Path.Combine(output, "delta.json"), JsonSerializer.Serialize(new { schema = "support-profile-delta-v1", entries }, options));
        File.WriteAllText(Path.Combine(output, "balanced-tier-report.json"), JsonSerializer.Serialize(new { templates = report,
            policy = "Male same-tier base BUFF reference before gear additive effects, song or favoritism; preserve those native effects",
            actual_game_panel = "pending" }, options));
        Console.WriteLine($"SUPPORT_BALANCED_TIERS templates={report.Count}; old additions replaced; main and awakening normalized before favoritism");

        (List<ScriptToken> Values, List<ScriptToken> Constants) Table(string path, int width)
        {
            var raw = TemplateDelta.Read(source, path);
            int start = raw.FindIndex(t => t.Text == "[dungeon]"), end = raw.FindIndex(t => t.Text == "[/dungeon]");
            if (start < 0)
            {
                // 奶爸一觉使用根表；只截取首张闭合表，不能误读后续PVP或活动模式。
                end = raw.FindIndex(t => t.Text == "[/level info]");
                if (end < 0 || raw.Take(end).Any(t => t.Text is "[pvp]" or "[dnfaos]" or "[arad royal rumble]"))
                    throw new InvalidDataException("Missing root ordinary skill table");
            }
            else if (end <= start) throw new InvalidDataException("Missing ordinary dungeon skill table");
            var dungeon = raw.GetRange(start + 1, end - start - 1);
            var values = Section(dungeon, "level info");
            if (values.Count < 1 + 40 * width || values[0].Value != width || (values.Count - 1) % width != 0 || values.Any(t => t.Kind != 0))
                throw new InvalidDataException("Skill table layout changed");
            return (values, Section(dungeon, "static data"));
        }
    }
}
