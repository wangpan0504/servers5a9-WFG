using System.Text.Json;
using PvfLib;
using static SupportBehaviorDelta;

// 对当前专用模板的有效增益作增量，保留装备、技能等级和原生偏爱/圣歌。
internal static class SupportBuffBoostDelta
{
    internal static (int Skill, int Column)[] Targets(int baseId) => baseId switch
    {
        20900 => [(45, 1), (45, 2), (45, 3), (45, 4), (45, 5), (84, 0), (84, 1)],
        74990 => [(14, 1), (14, 2), (14, 3), (14, 4), (14, 5), (33, 4), (33, 5)],
        19973 => [(335, 2), (335, 3), (335, 4), (335, 5), (335, 6), (343, 3), (343, 4)],
        9986 => [(36, 1), (36, 2), (36, 3), (36, 4), (42, 2)],
        _ => throw new InvalidDataException("Unknown support identity")
    };

    internal static Dictionary<int, (int Base, string Path)> Actors(PvfArchive source)
    {
        var registry = TemplateDelta.Read(source, "aicharacter/aicharacter.lst");
        if (registry.Count % 2 != 0) throw new InvalidDataException("Malformed AIC registry");
        var paths = Enumerable.Range(0, registry.Count / 2).ToDictionary(i => registry[2 * i].Value,
            i => PvfArchive.NormalizePath("aicharacter/" + registry[2 * i + 1].Text));
        var result = TemplateDelta.Definitions.ToDictionary(d => d.Id, d => (d.Id, paths[d.Id]));
        var profiles = new PvfTokenDocument(source.ReadTokens("etc/support_companion_profiles.etc"), source.ResolveString);
        if (!profiles.Diagnostics.IsEmpty) throw new InvalidDataException("Malformed support profiles");
        foreach (var row in profiles.Sections.Where(s => s.Name == "profile"))
        {
            if (!row.IsBlock || row.Values.Length != 4 || row.Values[0].Type != 0 || row.Values[2].Type != 0 ||
                !TemplateDelta.Definitions.Any(d => d.Id == row.Values[0].Value) ||
                !result.TryAdd(row.Values[2].Value, (row.Values[0].Value, paths[row.Values[2].Value])))
                throw new InvalidDataException("Malformed or duplicate support profile");
        }
        return result;
    }

    internal static List<ScriptToken> Boost(List<ScriptToken> tokens, int baseId)
    {
        var definition = TemplateDelta.Definitions.Single(d => d.Id == baseId);
        string job = "[" + (definition.Job == "atpriest" ? "at priest" : definition.Job) + "]";
        var modifiers = tokens.Any(t => t.Kind == 3 && t.Text == "[skill data up]")
            ? Section(tokens, "skill data up") : [];
        if (modifiers.Count % 7 != 0) throw new InvalidDataException("Malformed skill modifiers");
        var skills = Section(tokens, "skill");
        foreach (var (skill, column) in Targets(baseId))
        {
            if (!Enumerable.Range(0, skills.Count / 2).Any(i => skills[2 * i].Value == skill && skills[2 * i + 1].Value > 0))
                throw new InvalidDataException("BUFF must already be learned");
            var offsets = Enumerable.Range(0, modifiers.Count / 7).Select(i => i * 7)
                .Where(i => modifiers[i].Text == job && modifiers[i + 1].Value == skill &&
                    modifiers[i + 2].Text == "[dungeon]" && modifiers[i + 3].Text == "[level]" && modifiers[i + 4].Value == column).ToArray();
            if (offsets.Length > 1) throw new InvalidDataException("Duplicate BUFF modifiers require review");
            float percent = 0;
            if (offsets.Length == 1)
            {
                int at = offsets[0];
                if (modifiers[at + 5].Text != "%" || modifiers[at + 6].Kind != 2)
                    throw new InvalidDataException("Unsupported BUFF modifier operation");
                percent = BitConverter.Int32BitsToSingle(modifiers[at + 6].Value);
            }
            // 负归一系数也必须先恢复成有效倍率，再把实际增益乘1.15。
            float revised = checked((float)((100.0 + percent) * 1.15 - 100.0));
            if (!float.IsFinite(percent) || percent <= -100 || !float.IsFinite(revised) || revised > 1000)
                throw new InvalidDataException("Invalid BUFF normalization");
            if (offsets.Length == 1) modifiers[offsets[0] + 6] = ScriptToken.Float(revised);
            else modifiers.AddRange([ScriptToken.String(job), ScriptToken.Int(skill), ScriptToken.String("[dungeon]"),
                ScriptToken.String("[level]"), ScriptToken.Int(column), ScriptToken.String("%"), ScriptToken.Float(revised)]);
        }
        return modifiers;
    }

    internal static void Write(PvfArchive source, string output)
    {
        if (Directory.Exists(output) || File.Exists(output)) throw new IOException("Output already exists: " + output);
        var entries = new List<object>();
        var report = new List<object>();
        foreach (var (id, actor) in Actors(source).OrderBy(p => p.Key))
        {
            var tokens = TemplateDelta.Read(source, actor.Path);
            var definition = TemplateDelta.Definitions.Single(d => d.Id == actor.Base);
            var minimum = Section(tokens, "minimum info");
            if (minimum.Count < 6 || minimum[5].Value != 95 ||
                minimum[1].Value != (actor.Base == 20900 ? 4 : actor.Base == 74990 ? 14 : actor.Base == 19973 ? 3 : 1) ||
                minimum[2].Value != definition.Grow)
                throw new InvalidDataException("Support identity changed");
            var modifiers = Boost(tokens, actor.Base);
            if (tokens.Any(t => t.Kind == 3 && t.Text == "[skill data up]")) Set(tokens, "skill data up", modifiers);
            else { tokens.Add(ScriptToken.Tag("[skill data up]")); tokens.AddRange(modifiers); }
            entries.Add(new { path = actor.Path, data_type = 1, append = false,
                expected_before_sha256 = MapMigration.Hash(source.ReadRaw(actor.Path)), tokens });
            report.Add(new { id, base_id = actor.Base, columns = Targets(actor.Base) });
        }
        Directory.CreateDirectory(output);
        var options = new JsonSerializerOptions { WriteIndented = true, IncludeFields = true };
        File.WriteAllText(Path.Combine(output, "delta.json"), JsonSerializer.Serialize(new { schema = "support-profile-delta-v1", entries }, options));
        File.WriteAllText(Path.Combine(output, "buff-boost-report.json"), JsonSerializer.Serialize(new { multiplier = 1.15, templates = report,
            policy = "Current effective dedicated BUFF columns times 1.15; equipment, learned levels, AI, timers, range, song and favoritism preserved" }, options));
        Console.WriteLine($"SUPPORT_BUFF_BOOST templates={entries.Count} multiplier=1.15; current normalization retained");
    }
}
