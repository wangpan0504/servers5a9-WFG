using System.Text.Json;
using PvfLib;
using static SupportBehaviorDelta;

// 在已验证行为候选上追加辅助技能；原KEY序号、共享技能定义及非目标资源不变。
internal static class SupportAuxiliaryDelta
{
    internal static int[] Extras(int id) => id == 74990 ? [36, 91] : [341, 344, 349];

    internal static void Write(PvfArchive source, string output)
    {
        if (File.Exists(output) || Directory.Exists(output)) throw new IOException("Output already exists: " + output);
        var registry = TemplateDelta.Read(source, "aicharacter/aicharacter.lst");
        if (registry.Count % 2 != 0) throw new InvalidDataException("Malformed AIC registry");
        var actors = Enumerable.Range(0, registry.Count / 2).ToDictionary(i => registry[2 * i].Value,
            i => PvfArchive.NormalizePath("aicharacter/" + registry[2 * i + 1].Text));
        var entries = new Dictionary<string, object>(StringComparer.Ordinal);
        var report = new List<object>();
        foreach (var definition in TemplateDelta.Definitions.Where(d => d.Id is 74990 or 19973))
        {
            var index = TemplateDelta.Read(source, $"skill/{definition.Job}skill.lst");
            if (index.Count % 2 != 0) throw new InvalidDataException("Malformed skill registry");
            var skillPaths = Enumerable.Range(0, index.Count / 2).ToDictionary(i => index[2 * i].Value,
                i => PvfArchive.NormalizePath("skill/" + index[2 * i + 1].Text));
            int[] ids = definition.Id == 74990 ? [74990, ..Enumerable.Range(74000, 48)] : [19973, ..Enumerable.Range(19000, 84)];
            foreach (int id in ids)
            {
                string path = actors[id];
                if (!path.StartsWith(definition.Target[..(definition.Target.LastIndexOf('/') + 1)], StringComparison.Ordinal))
                    throw new InvalidDataException("Unexpected female support path");
                var tokens = TemplateDelta.Read(source, path);
                var minimum = Section(tokens, "minimum info");
                if (minimum.Count < 6 || minimum[1].Value != (definition.Id == 74990 ? 14 : 3) ||
                    minimum[2].Value != definition.Grow || minimum[5].Value != 95)
                    throw new InvalidDataException("Female support identity changed");
                var keys = Section(tokens, "key stream");
                var expected = definition.Active.SelectMany(skill => new[] { "", $"support_{skill}.key" })
                    .Concat(["", "support_attack.key"]).Concat(ExtraSkills(definition.Id).SelectMany(skill => new[] { "", $"support_{skill}.key" }));
                if (!keys.Select(t => t.Kind == 6 ? t.Text : null).SequenceEqual(expected))
                    throw new InvalidDataException("Input must be verified behavior candidate with original KEY prefix");
                var skills = Section(tokens, "skill");
                if (skills.Count % 2 != 0 || skills.Any(t => t.Kind != 0)) throw new InvalidDataException("Malformed learned skills");
                var learned = Enumerable.Range(0, skills.Count / 2).ToDictionary(i => skills[2 * i].Value, i => skills[2 * i + 1].Value);
                var oldSkills = new Dictionary<int, int>(learned);
                var visiting = new HashSet<int>();
                foreach (int skill in Extras(definition.Id)) Learn(skill, 1);
                // 原已学技能保持有效等级；只在缺失或前置不足时追加/提高到真实前置要求。
                foreach (var pair in learned)
                {
                    if (!oldSkills.ContainsKey(pair.Key)) skills.AddRange([ScriptToken.Int(pair.Key), ScriptToken.Int(pair.Value)]);
                    else if (oldSkills[pair.Key] != pair.Value)
                    {
                        int at = Enumerable.Range(0, skills.Count / 2).Single(i => skills[2 * i].Value == pair.Key) * 2;
                        skills[at + 1] = ScriptToken.Int(pair.Value);
                    }
                }
                Set(tokens, "skill", skills);
                var ordinals = Ordinals(definition);
                foreach (int skill in Extras(definition.Id))
                {
                    var raw = TemplateDelta.Read(source, skillPaths[skill]);
                    if (Section(raw, "type") is not [{ Text: "[active]" }]) throw new InvalidDataException("Auxiliary skill must be active");
                    ordinals.Add(skill, keys.Count / 2);
                    keys.AddRange([ScriptToken.String(""), ScriptToken.String($"support_{skill}.key")]);
                }
                Set(tokens, "key stream", keys);
                Add(path, tokens);
                string directory = path[..path.LastIndexOf('/')];
                foreach (var program in AiPrograms.Action(definition, skillOrdinals: ordinals)) Add(directory + "/ai/" + program.Key, program.Value);
                foreach (int skill in Extras(definition.Id))
                    Add(directory + $"/key/support_{skill}.key", [ScriptToken.Tag("[input]"), ScriptToken.String($"skill:{skill}"), ScriptToken.Int(30), ScriptToken.String("time"), ScriptToken.Int(100)]);
                report.Add(new { id, path, auxiliary = Extras(definition.Id), learned = learned.Where(p => !oldSkills.ContainsKey(p.Key)).ToDictionary(), ordinals });

                void Learn(int skill, int minimumLevel)
                {
                    if (learned.GetValueOrDefault(skill) >= minimumLevel) return;
                    if (!visiting.Add(skill) || !skillPaths.TryGetValue(skill, out string? skillPath)) throw new InvalidDataException("Missing or cyclic auxiliary prerequisite");
                    var raw = TemplateDelta.Read(source, skillPath);
                    int required = Scalar(raw, "required level"), range = Scalar(raw, "required level range"), maximum = Scalar(raw, "maximum level");
                    var caps = Section(raw, "growtype maximum level");
                    if (caps.Count > definition.Grow && caps[definition.Grow].Value > 0) maximum = Math.Min(maximum, caps[definition.Grow].Value);
                    int level = Math.Max(minimumLevel, Math.Min(maximum, range > 0 ? (95 - required) / range + 1 : 1));
                    if (required is < 1 or > 95 || level is < 1 or > 255 || level > maximum) throw new InvalidDataException("Invalid auxiliary learned level");
                    learned[skill] = level;
                    var tag = raw.FindIndex(t => t.Text == "[pre required skill]");
                    if (tag >= 0)
                    {
                        var prerequisites = Section(raw, "pre required skill");
                        if (prerequisites.Count % 2 != 0 || prerequisites.Any(t => t.Kind != 0)) throw new InvalidDataException("Malformed prerequisite");
                        for (int i = 0; i < prerequisites.Count; i += 2) Learn(prerequisites[i].Value, prerequisites[i + 1].Value);
                    }
                    visiting.Remove(skill);
                }
            }
        }
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "delta.json"), JsonSerializer.Serialize(new { schema = "support-profile-delta-v1", entries = entries.Values }, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(output, "auxiliary-report.json"), JsonSerializer.Serialize(new { templates = report, gameplay = "pending" }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"SUPPORT_AUXILIARY templates={report.Count} resources={entries.Count}; shared skill definitions unchanged");

        void Add(string path, List<ScriptToken> tokens)
        {
            var entry = new { path, data_type = 1, append = !source.Contains(path), expected_before_sha256 = source.Contains(path) ? MapMigration.Hash(source.ReadRaw(path)) : null, tokens };
            if (entries.TryGetValue(path, out var old) && JsonSerializer.Serialize(old) != JsonSerializer.Serialize(entry)) throw new InvalidDataException("Conflicting auxiliary resource");
            entries[path] = entry;
        }
    }
    static int Scalar(List<ScriptToken> tokens, string tag) => Section(tokens, tag) is [{ Kind: 0 } token] ? token.Value : throw new InvalidDataException("Invalid skill scalar: " + tag);
}
