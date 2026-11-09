using System.Text.Json;
using PvfLib;

// 以当前注册模板为边界；新KEY只追加到旧普攻KEY之后，绝不重排已有下标。
internal static class SupportBehaviorDelta
{
    internal static int[] ExtraSkills(int baseId) => baseId switch
    {
        9986 => [46, 58], 19973 => [370, 372], _ => []
    };

    internal static Dictionary<int, int> Ordinals(TemplateDefinition definition) =>
        definition.Active.Select((id, ordinal) => (id, ordinal))
            .Concat(ExtraSkills(definition.Id).Select((id, i) => (id, ordinal: definition.Active.Length + 1 + i)))
            .ToDictionary(row => row.id, row => row.ordinal);

    internal static void Write(PvfArchive source, string outputPath)
    {
        if (Directory.Exists(outputPath) || File.Exists(outputPath)) throw new IOException("Output already exists: " + outputPath);
        var registry = TemplateDelta.Read(source, "aicharacter/aicharacter.lst");
        if (registry.Count % 2 != 0) throw new InvalidDataException("Malformed AIC registry");
        var index = new Dictionary<int, string>();
        for (int i = 0; i < registry.Count; i += 2)
            if (registry[i].Kind != 0 || registry[i + 1].Kind != 6 ||
                !index.TryAdd(registry[i].Value, PvfArchive.NormalizePath("aicharacter/" + registry[i + 1].Text)))
                throw new InvalidDataException("Malformed or duplicate AIC registry");
        var entries = new Dictionary<string, object>(StringComparer.Ordinal);
        var report = new List<object>();
        foreach (var definition in TemplateDelta.Definitions)
        {
            int[] ids = definition.Id switch
            {
                20900 => [20900, 20903, 20904, 20905],
                9986 => [9986, ..Enumerable.Range(9600, 29)],
                74990 => [74990, ..Enumerable.Range(74000, 48)],
                19973 => [19973, ..Enumerable.Range(19000, 84)],
                _ => throw new InvalidDataException("Unsupported support identity")
            };
            var ordinals = Ordinals(definition);
            var programs = AiPrograms.Action(definition, skillOrdinals: ordinals);
            foreach (int id in ids)
            {
                if (!index.TryGetValue(id, out string? path) ||
                    !path.StartsWith(definition.Target[..(definition.Target.LastIndexOf('/') + 1)], StringComparison.Ordinal))
                    throw new InvalidDataException("Unregistered or unexpected support path: " + id);
                var tokens = TemplateDelta.Read(source, path);
                var minimum = Section(tokens, "minimum info");
                int job = definition.Id switch { 20900 => 4, 9986 => 1, 74990 => 14, _ => 3 };
                if (minimum.Count < 6 || minimum[1].Value != job || minimum[2].Value != definition.Grow || minimum[5].Value != 95)
                    throw new InvalidDataException("Support class, growth or level changed: " + path);
                var expected = definition.Active.SelectMany(skill => new[] { "", $"support_{skill}.key" })
                    .Concat(["", "support_attack.key"]).ToArray();
                var keys = Section(tokens, "key stream");
                if (!keys.Select(t => t.Kind == 6 ? t.Text : null).SequenceEqual(expected))
                    throw new InvalidDataException("Unexpected KEY ordering: " + path);
                var learned = Section(tokens, "skill");
                if (learned.Count % 2 != 0 || learned.Any(t => t.Kind != 0)) throw new InvalidDataException("Malformed learned skills");
                var skills = Enumerable.Range(0, learned.Count / 2).ToDictionary(i => learned[2 * i].Value, i => learned[2 * i + 1].Value);
                foreach (int skill in ordinals.Keys)
                    if (skills.GetValueOrDefault(skill) is < 1 or > 255) throw new InvalidDataException("Required skill is not learned: " + skill);
                // 新增攻击技能必须确为主动且学习门槛低于50；不从被动表名猜技能性质。
                var skillIndex = TemplateDelta.Read(source, $"skill/{definition.Job}skill.lst");
                foreach (int skill in ExtraSkills(definition.Id))
                {
                    int at = skillIndex.FindIndex(t => t.Kind == 0 && t.Value == skill);
                    if (at < 0 || at % 2 != 0 || at + 1 >= skillIndex.Count || skillIndex[at + 1].Kind != 6)
                        throw new InvalidDataException("Unregistered attack skill");
                    string skillPath = PvfArchive.NormalizePath("skill/" + skillIndex[at + 1].Text);
                    var raw = TemplateDelta.Read(source, skillPath);
                    var required = Section(raw, "required level");
                    var type = Section(raw, "type");
                    if (required.Count != 1 || required[0].Kind != 0 || required[0].Value is < 1 or >= 50 ||
                        type.Count != 1 || type[0].Text != "[active]") throw new InvalidDataException("Attack skill must be active and below50");
                }
                Set(tokens, "key stream", keys.Concat(ExtraSkills(definition.Id)
                    .SelectMany(skill => new[] { ScriptToken.String(""), ScriptToken.String($"support_{skill}.key") })));
                Add(path, tokens);
                string directory = path[..path.LastIndexOf('/')];
                foreach (var program in programs) Add(directory + "/ai/" + program.Key, program.Value);
                foreach (int skill in ExtraSkills(definition.Id))
                    Add(directory + $"/key/support_{skill}.key", [ScriptToken.Tag("[input]"), ScriptToken.String($"skill:{skill}"),
                        ScriptToken.Int(30), ScriptToken.String("time"), ScriptToken.Int(100)]);
                report.Add(new { id, path, ordinals, basic_attack_ordinal = definition.Active.Length, basic_attack_scheduled = false });
            }
        }
        Directory.CreateDirectory(outputPath);
        File.WriteAllText(Path.Combine(outputPath, "delta.json"), JsonSerializer.Serialize(new { schema = "support-profile-delta-v1", entries = entries.Values }, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(outputPath, "behavior-report.json"), JsonSerializer.Serialize(new { templates = report,
            forbidden = new[] { new { job = 4, skill = 81 }, new { job = 1, skill = 12 }, new { job = 14, skill = 93 } },
            preserved = "Existing KEY ordinals, learned levels, equipment, native cooldown, action state and escort gates", room_range = AiPrograms.RoomRange,
            runtime_gameplay = "pending" }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"SUPPORT_BEHAVIOR_DELTA templates={report.Count} resources={entries.Count}; source PVF read-only");

        void Add(string path, List<ScriptToken> tokens)
        {
            var entry = new { path, data_type = 1, append = !source.Contains(path),
                expected_before_sha256 = source.Contains(path) ? MapMigration.Hash(source.ReadRaw(path)) : null, tokens };
            if (entries.TryGetValue(path, out object? previous))
            {
                if (JsonSerializer.Serialize(previous) != JsonSerializer.Serialize(entry)) throw new InvalidDataException("Conflicting shared AI: " + path);
            }
            else entries.Add(path, entry);
        }
    }

    internal static List<ScriptToken> Section(List<ScriptToken> tokens, string tag)
    {
        int start = tokens.FindIndex(t => t.Kind == 3 && t.Text == "[" + tag + "]");
        if (start < 0) throw new InvalidDataException("Missing AIC/skill section: " + tag);
        if (tokens.Count(t => t.Kind == 3 && t.Text == "[" + tag + "]") != 1) throw new InvalidDataException("Duplicate section: " + tag);
        int end = start + 1; while (end < tokens.Count && tokens[end].Kind != 3) end++;
        return tokens.GetRange(start + 1, end - start - 1);
    }
    internal static void Set(List<ScriptToken> tokens, string tag, IEnumerable<ScriptToken> values)
    {
        int start = tokens.FindIndex(t => t.Kind == 3 && t.Text == "[" + tag + "]");
        var old = Section(tokens, tag);
        tokens.RemoveRange(start + 1, old.Count); tokens.InsertRange(start + 1, values);
    }
}
