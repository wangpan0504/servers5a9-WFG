using System.Text.Json;
using PvfLib;

internal static class ResourceEvidence
{
    internal static void Write(PvfArchive source, string planPath, string outputPath)
    {
        string output = Path.GetFullPath(outputPath);
        if (Directory.Exists(output) || File.Exists(output)) throw new IOException("Evidence output must not exist");
        using var plan = JsonDocument.Parse(File.ReadAllBytes(planPath));
        if (plan.RootElement.GetProperty("schema").GetString() != "support-apc-resource-delta-v1")
            throw new InvalidDataException("Unexpected template plan");
        var entries = plan.RootElement.GetProperty("entries").EnumerateArray()
            .ToDictionary(entry => entry.GetProperty("path").GetString()!, entry => entry.GetProperty("tokens").Deserialize<List<ScriptToken>>()!, StringComparer.Ordinal);
        var equipment = PvfItemIndex.Equipment(source.ReadTokens("equipment/equipment.lst"), source.ResolveString,
            path => source.FindFile(path)?.DataType == 1);
        var previews = new List<object>();
        var skills = new List<object>();
        var aiConditions = new List<object>();
        var layoutPaths = new HashSet<string>(StringComparer.Ordinal);
        Directory.CreateDirectory(output);
        foreach (var definition in TemplateDelta.Definitions)
        {
            if (!entries.TryGetValue(definition.Target, out var aic)) throw new InvalidDataException("Missing final AIC: " + definition.Target);
            var gear = new List<object>();
            var equipped = Values(aic, "[equipment]");
            if (equipped.Count % 3 != 0) throw new InvalidDataException("Invalid planned equipment");
            for (int at = 0; at < equipped.Count; at += 3)
            {
                int id = equipped[at].Value;
                if (!equipment.TryGetValue(id, out string? path)) throw new InvalidDataException("Missing preview equipment: " + id);
                var script = TemplateDelta.Read(source, path);
                string[] tags = ["[name]", "[equipment type]", "[icon]", "[field image]", "[animation job]", "[variation]", "[layer variation]", "[equipment ani script]", "[avatar type select]", "[part set index]"];
                var fields = Sections(script).Where(section => tags.Contains(section.Tag, StringComparer.OrdinalIgnoreCase)).ToArray();
                foreach (var section in fields.Where(section => section.Tag.Equals("[equipment ani script]", StringComparison.OrdinalIgnoreCase)))
                    foreach (var token in section.Tokens.Where(token => token.Kind == 6 && token.Text is not null))
                        layoutPaths.Add(PvfArchive.NormalizePath(token.Text!));
                gear.Add(new { equipment_id = id, path, raw_sha256 = MapMigration.Hash(source.ReadRaw(path)),
                    aic_auxiliary_integers = new[] { equipped[at + 1].Value, equipped[at + 2].Value }, fields });
            }
            var actions = new List<object>();
            foreach (int skill in definition.Active)
            {
                string path = SkillPath(source, definition.Job, skill);
                var script = TemplateDelta.Read(source, path);
                string fileName = definition.Id + "-" + skill + ".skl.txt";
                File.WriteAllText(Path.Combine(output, fileName), source.ReadText(path));
                var declarations = Sections(script).ToArray();
                var relevant = declarations.Where(section => section.Tag is "[static data]" or "[level info]" or "[level property]" or "[feature skill level info]" or "[executable states]" or "[consume item]" or "[consume MP]" or "[cool time]" or "[casting time]" || section.Tag.Contains("range", StringComparison.OrdinalIgnoreCase)).ToArray();
                skills.Add(new { template_id = definition.Id, job = definition.Job, skill_id = skill, path,
                    raw_sha256 = MapMigration.Hash(source.ReadRaw(path)), raw_text_file = fileName,
                    learned_level = Learned(aic, skill), fields = relevant,
                    range_interpretation = "Raw declarations only; values are not identified as geometric radius without native consumer evidence" });
                actions.Add(new { skill_id = skill, path, name = Values(script, "[name]"),
                    explain = Values(script, "[basic explain]"), key_file = $"support_{skill}.key",
                    image_and_animation_fields = declarations.Where(section => section.Tag.Contains("icon", StringComparison.OrdinalIgnoreCase) ||
                        section.Tag.Contains("image", StringComparison.OrdinalIgnoreCase) || section.Tag.Contains("ani", StringComparison.OrdinalIgnoreCase)).ToArray() });
            }
            previews.Add(new { definition.Id, definition.Name, source_aic = definition.Source, planned_aic = definition.Target,
                minimum_info = Values(aic, "[minimum info]"), source_aic_raw_sha256 = MapMigration.Hash(source.ReadRaw(definition.Source)),
                equipment = gear, actions, image_status = "PVF resource references and indices only; no rendered preview or invented bitmap",
                actor_animation_status = "Base profession/awakening actions are resolved by native APC factory; this inventory does not invent a single image for an actor" });
            string aiPrefix = definition.Source[..definition.Source.LastIndexOf('/')] + "/ai/";
            foreach (var file in source.Files.Where(file => file.Path.StartsWith(aiPrefix, StringComparison.Ordinal) && file.Path.EndsWith(".ai", StringComparison.Ordinal)))
            {
                var script = TemplateDelta.Read(source, file.Path);
                var found = Sections(script).Where(section => section.Tag.Contains("distance", StringComparison.OrdinalIgnoreCase) ||
                    section.Tag.Contains("party member", StringComparison.OrdinalIgnoreCase) || section.Tag.Contains("target", StringComparison.OrdinalIgnoreCase)).ToArray();
                if (found.Length != 0) aiConditions.Add(new { definition.Id, path = file.Path, fields = found });
            }
        }
        var layouts = layoutPaths.Order(StringComparer.Ordinal).Select(path => new
        {
            path, exists_in_pvf = source.Contains(path),
            raw_sha256 = source.Contains(path) ? MapMigration.Hash(source.ReadRaw(path)) : null,
            declarations = source.Contains(path) && source.FindFile(path)?.DataType == 1 ? Sections(TemplateDelta.Read(source, path)).ToArray() : []
        }).ToArray();
        WriteJson("preview-index.json", new { schema = "support-apc-preview-index-v1", templates = previews, layouts });
        WriteJson("skill-range-declarations.json", new { schema = "support-apc-skill-declarations-v1", skills,
            interpretation = "SKL raw arrays, names, token indices and level properties retained; no assumed radius columns or units" });
        WriteJson("source-ai-target-conditions.json", new { schema = "support-apc-source-ai-conditions-v1", sources = aiConditions,
            scope = "Only the four source-template AI directories; absence here does not prove global absence of a native condition" });
        Console.WriteLine($"EVIDENCE templates={previews.Count} skills={skills.Count} layouts={layouts.Length} targetAi={aiConditions.Count}; source PVF read-only, no generated images");

        void WriteJson(string name, object value) => File.WriteAllText(Path.Combine(output, name), JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
    }

    internal sealed record Declaration(string Tag, int TokenIndex, List<ScriptToken> Tokens);
    static IEnumerable<Declaration> Sections(List<ScriptToken> script)
    {
        for (int at = 0; at < script.Count; at++)
        {
            if (script[at].Kind != 3 || script[at].Text is not string tag || tag.StartsWith("[/", StringComparison.Ordinal)) continue;
            int end = at + 1;
            while (end < script.Count && script[end].Kind != 3) end++;
            yield return new(tag, at, script[(at + 1)..end]);
        }
    }
    static List<ScriptToken> Values(List<ScriptToken> script, string tag) => Sections(script).FirstOrDefault(section => section.Tag.Equals(tag, StringComparison.OrdinalIgnoreCase))?.Tokens ?? [];
    static int Learned(List<ScriptToken> aic, int skill)
    {
        var values = Values(aic, "[skill]");
        for (int at = 0; at + 1 < values.Count; at += 2) if (values[at].Value == skill) return values[at + 1].Value;
        throw new InvalidDataException("Skill missing from planned AIC");
    }
    static string SkillPath(PvfArchive source, string job, int skill)
    {
        var index = source.ReadTokens($"skill/{job}skill.lst");
        for (int at = 0; at + 1 < index.Length; at += 2)
            if (index[at].Value == skill) return PvfArchive.NormalizePath("skill/" + source.ResolveString(index[at + 1].Value));
        throw new InvalidDataException("Skill missing from real index");
    }
}
