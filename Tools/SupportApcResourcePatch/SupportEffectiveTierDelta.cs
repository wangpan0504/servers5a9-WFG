using System.Text.Json;
using PvfLib;
using static SupportBehaviorDelta;

// 只对专用两女AIC补足主BUFF基础列，仍由原生技能读取及智力公式生成真实增益。
internal static class SupportEffectiveTierDelta
{
    internal static (int Intelligence, int Strength, int Attack) Target(string tier) => tier switch
    {
        "entry" => (6000, 4950, 850),
        "graduate" => (7200, 7750, 1250),
        "huge" => (9000, 14750, 2350),
        _ => throw new InvalidDataException("Unknown female support tier")
    };

    internal static void Write(PvfArchive source, string evidencePath, string output)
    {
        if (File.Exists(output) || Directory.Exists(output)) throw new IOException("Output already exists: " + output);
        using var evidence = JsonDocument.Parse(File.ReadAllBytes(evidencePath));
        var rows = evidence.RootElement.GetProperty("templates").EnumerateArray().ToArray();
        if (rows.Length != 134 || rows.Select(r => r.GetProperty("id").GetInt32()).Distinct().Count() != 134)
            throw new InvalidDataException("Expected all current female tier identities");
        var registry = TemplateDelta.Read(source, "aicharacter/aicharacter.lst");
        var paths = Enumerable.Range(0, registry.Count / 2).ToDictionary(i => registry[i * 2].Value,
            i => PvfArchive.NormalizePath("aicharacter/" + registry[i * 2 + 1].Text));
        var profiles = TemplateDelta.Read(source, "etc/support_companion_profiles.etc");
        var actual = new Dictionary<int, (int Base, string Tier)> { [74990] = (74990, "entry"), [19973] = (19973, "entry") };
        for (int i = 0; i < profiles.Count; i++)
            if (profiles[i].Text == "[profile]" && profiles[i + 1].Value is 74990 or 19973)
            {
                if (profiles[i + 5].Text != "[/profile]") throw new InvalidDataException("Malformed support profile");
                actual.Add(profiles[i + 3].Value, (profiles[i + 1].Value, profiles[i + 2].Text!));
            }
        if (actual.Count != rows.Length) throw new InvalidDataException("Incomplete female tier evidence");
        var entries = new List<object>();
        var report = new List<object>();
        foreach (var row in rows)
        {
            int id = row.GetProperty("id").GetInt32(), baseId = row.GetProperty("base_id").GetInt32();
            string tier = row.GetProperty("tier").GetString()!;
            if (!actual.TryGetValue(id, out var profile) || profile != (baseId, tier))
                throw new InvalidDataException("Evidence and registered tier differ");
            bool female = baseId == 74990;
            string prefix = female ? "aicharacter/support/femalecrusader/" : "aicharacter/support/enchantress/";
            string path = paths[id];
            if (!path.StartsWith(prefix, StringComparison.Ordinal)) throw new InvalidDataException("Not a dedicated female support resource");
            int skill = female ? 14 : 335;
            var tokens = TemplateDelta.Read(source, path);
            var minimum = Section(tokens, "minimum info");
            if (minimum.Count < 6 || minimum[1].Value != (female ? 14 : 3) || minimum[2].Value != (female ? 1 : 5) || minimum[5].Value != 95)
                throw new InvalidDataException("Female profession/level changed");
            var learning = Section(tokens, "skill");
            var pairs = Enumerable.Range(0, learning.Count / 2).Where(i => learning[i * 2].Value == skill).ToArray();
            int effective = row.GetProperty("effective").GetInt32();
            if (pairs.Length != 1 || learning[pairs[0] * 2 + 1].Value != row.GetProperty("learned").GetInt32() ||
                learning[pairs[0] * 2 + 1].Value + row.GetProperty("equipment_bonus").GetInt32() != effective ||
                effective != (tier == "entry" ? 20 : tier == "graduate" ? 30 : 35))
                throw new InvalidDataException("Primary learned/equipped level differs from verified current source");
            if (tokens.Any(t => t.Text == "[skill data up]"))
                throw new InvalidDataException("Existing AIC coefficients require explicit reconciliation");
            string skillPath = female ? "skill/atpriest/blessofcourage.skl" : "skill/mage/forbiddencurse.skl";
            var raw = TemplateDelta.Read(source, skillPath);
            int start = raw.FindIndex(t => t.Text == "[dungeon]"), end = raw.FindIndex(t => t.Text == "[/dungeon]");
            if (start < 0 || end <= start) throw new InvalidDataException("Missing ordinary dungeon skill table");
            var dungeon = raw.GetRange(start + 1, end - start - 1);
            var table = Section(dungeon, "level info");
            var constants = Section(dungeon, "static data");
            if (table.Count < 1 || table[0].Value != 8 || (table.Count - 1) % 8 != 0 || table.Any(t => t.Kind != 0) ||
                effective < 1 || effective > (table.Count - 1) / 8 || constants.Count < 6 ||
                constants.Skip(1).Take(5).Any(t => t.Kind != 0 || t.Value != (female ? 675 : 665)))
                throw new InvalidDataException("Unsupported main BUFF coefficient or divisor layout");
            var target = Target(tier);
            var modifiers = new List<ScriptToken> { ScriptToken.Tag("[skill data up]") };
            var columns = new List<object>();
            for (int column = female ? 1 : 2; column <= (female ? 5 : 6); column++)
            {
                int baseline = table[1 + (effective - 1) * 8 + column].Value;
                int floor = column <= (female ? 3 : 4) ? target.Attack : target.Strength;
                int divisor = constants[column - (female ? 0 : 1)].Value;
                // 先按原生整数基础列留一格裕量，再经真实INT放大；不依赖圣歌、偏爱或装备正增幅才能达标。
                int required = checked((int)Math.Ceiling((double)floor * divisor / (divisor + target.Intelligence)) + 1);
                int addition = Math.Max(0, checked(required - baseline));
                modifiers.AddRange([ScriptToken.String(female ? "[at priest]" : "[mage]"), ScriptToken.Int(skill),
                    ScriptToken.String("[dungeon]"), ScriptToken.String("[level]"), ScriptToken.Int(column),
                    ScriptToken.String("+"), ScriptToken.Int(addition)]);
                columns.Add(new { column, baseline, addition, minimum_base = baseline + addition, divisor, floor });
            }
            modifiers.Add(ScriptToken.Tag("[/skill data up]"));
            tokens.AddRange(modifiers);
            entries.Add(new { path, data_type = 1, append = false, expected_before_sha256 = MapMigration.Hash(source.ReadRaw(path)), tokens });
            report.Add(new { id, base_id = baseId, tier, skill, effective, applicable_intelligence = target.Intelligence,
                minimum_strength = target.Strength, minimum_attack = target.Attack, columns });
        }
        Directory.CreateDirectory(output);
        var options = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(Path.Combine(output, "delta.json"), JsonSerializer.Serialize(new { schema = "support-profile-delta-v1", entries }, options));
        File.WriteAllText(Path.Combine(output, "effective-tier-report.json"), JsonSerializer.Serialize(new { templates = report,
            policy = "Dedicated final INT6000/7200/9000; main BUFF minimum4950/850,7750/1250,14750/2350 before optional bonuses",
            actual_game_panel = "pending; resource and isolated native bridge verification do not replace game QA" }, options));
        Console.WriteLine($"SUPPORT_EFFECTIVE_TIERS templates={report.Count}; dedicated AIC only; final native INT companion bridge required");
    }
}
