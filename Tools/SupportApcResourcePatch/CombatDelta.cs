using System.Text.Json;
using PvfLib;

// 在当前累计资源上只更新两职业的动作脚本及KEY，不重新生成装备、技能等级或三档属性。
internal static class CombatDelta
{
    internal static void Write(PvfArchive source, string outputPath)
    {
        string output = Path.GetFullPath(outputPath);
        if (File.Exists(output) || Directory.Exists(output)) throw new IOException("Output must not exist: " + output);
        var index = new Dictionary<int, string>();
        var registry = source.ReadTokens("aicharacter/aicharacter.lst");
        if (registry.Length % 2 != 0) throw new InvalidDataException("Malformed AIC registry");
        for (int i = 0; i < registry.Length; i += 2)
        {
            if (registry[i].Type != 0 || registry[i + 1].Type != 6 ||
                !index.TryAdd(registry[i].Value, "aicharacter/" + source.ResolveString(registry[i + 1].Value).Replace('\\', '/').ToLowerInvariant()))
                throw new InvalidDataException("Malformed or duplicate AIC registry entry");
        }
        var entries = new Dictionary<string, object>(StringComparer.Ordinal);
        var templates = new List<object>();
        foreach (var definition in TemplateDelta.Definitions.Where(AiPrograms.HasBasicAttack))
        {
            int[] ids = definition.Id == 20900 ? [20900,20903,20904,20905] : [9986, ..Enumerable.Range(9600,29)];
            foreach (int id in ids)
            {
                if (!index.TryGetValue(id, out string? path)) throw new InvalidDataException("Missing registered support template: " + id);
                var tokens = TemplateDelta.Read(source, path);
                var doc = new PvfTokenDocument(source.ReadTokens(path), source.ResolveString);
                var header = doc.Sections.Single(row => row.Name == "minimum info").Values;
                if (header.Length < 6 || header[1].Type != 0 || header[1].Value != (definition.Id == 20900 ? 4 : 1))
                    throw new InvalidDataException("Support profession mismatch: " + path);
                var keys = doc.Sections.Single(row => row.Name == "key stream").Values;
                var expected = definition.Active.SelectMany(skill => new[] { "", $"support_{skill}.key" }).ToArray();
                var actual = keys.Select(token => token.Type == 6 ? source.ResolveString(token.Value) : throw new InvalidDataException("Malformed support KEY stream")).ToArray();
                if (!actual.SequenceEqual(expected) && !actual.SequenceEqual(expected.Concat(new[] { "", "support_attack.key" })))
                    throw new InvalidDataException("Unexpected support KEY ordering: " + path);
                int start = tokens.FindIndex(token => token.Kind == 3 && token.Text == "[key stream]");
                int end = start + 1;
                while (end < tokens.Count && tokens[end].Kind != 3) end++;
                tokens.RemoveRange(start + 1, end - start - 1);
                tokens.InsertRange(start + 1, expected.Concat(new[] { "", "support_attack.key" }).Select(value => ScriptToken.String(value)));
                Add(path, tokens);
                string directory = path[..path.LastIndexOf('/')];
                foreach (var program in AiPrograms.Action(definition)) Add(directory + "/ai/" + program.Key, program.Value);
                Add(directory + "/key/support_attack.key", AiPrograms.BasicAttack());
                templates.Add(new { id, path, basic_attack_key = definition.Active.Length, disabled_skill = definition.Id == 9986 ? 12 : 0 });
            }
        }
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "delta.json"), JsonSerializer.Serialize(new { schema = "support-profile-delta-v1", entries = entries.Values }, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(output, "combat-report.json"), JsonSerializer.Serialize(new { templates, room_range = AiPrograms.RoomRange,
            enemy_filter = "native scene objects/type mask 0x11/alive/hostile; no rank filter", source_read_only = true,
            preserved = "follow5, equipment, learned skills, tiers, native damage ranges; no light skill12 action" }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"COMBAT_DELTA templates={templates.Count} resources={entries.Count} source_read_only=true");

        void Add(string path, List<ScriptToken> tokens)
        {
            var value = new { path, data_type = 1, append = !source.Contains(path),
                expected_before_sha256 = source.Contains(path) ? MapMigration.Hash(source.ReadRaw(path)) : null, tokens };
            if (entries.TryGetValue(path, out object? previous))
            {
                if (JsonSerializer.Serialize(previous) != JsonSerializer.Serialize(value)) throw new InvalidDataException("Conflicting shared support program: " + path);
            }
            else entries.Add(path, value);
        }
    }
}
