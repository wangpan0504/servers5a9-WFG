using System.Text.Json;
using PvfLib;

// 只重建现有女性支援AI，读取当前KEY下标；不改装备、技能、三档和共享职业资源。
internal static class SupportOpeningDelta
{
    internal static IEnumerable<(TemplateDefinition Definition, int Id, string Path)> Actors(PvfArchive source)
    {
        var registry = TemplateDelta.Read(source, "aicharacter/aicharacter.lst");
        if (registry.Count % 2 != 0) throw new InvalidDataException("Malformed AIC registry");
        var paths = Enumerable.Range(0, registry.Count / 2).ToDictionary(i => registry[2 * i].Value,
            i => PvfArchive.NormalizePath("aicharacter/" + registry[2 * i + 1].Text));
        foreach (var definition in TemplateDelta.Definitions.Where(d => d.Id is 74990 or 19973))
        foreach (int id in definition.Id == 74990 ? new[] { 74990 }.Concat(Enumerable.Range(74000, 48)) : new[] { 19973 }.Concat(Enumerable.Range(19000, 84)))
        {
            if (!paths.TryGetValue(id, out string? path) || !path.StartsWith(definition.Target[..(definition.Target.LastIndexOf('/') + 1)], StringComparison.Ordinal))
                throw new InvalidDataException("Unexpected female support registry: " + id);
            yield return (definition, id, path);
        }
    }

    internal static Dictionary<int, int> Ordinals(PvfArchive source, string path)
    {
        var keys = SupportBehaviorDelta.Section(TemplateDelta.Read(source, path), "key stream");
        if (keys.Count % 2 != 0) throw new InvalidDataException("Malformed KEY stream");
        var result = new Dictionary<int, int>();
        for (int i = 0; i < keys.Count; i += 2)
        {
            if (keys[i].Text != "" || keys[i + 1].Kind != 6) throw new InvalidDataException("Unexpected KEY stream");
            string key = keys[i + 1].Text!;
            if (key == "support_attack.key") continue;
            if (!key.StartsWith("support_", StringComparison.Ordinal) || !key.EndsWith(".key", StringComparison.Ordinal) ||
                !int.TryParse(key[8..^4], out int skill) || !result.TryAdd(skill, i / 2))
                throw new InvalidDataException("Unexpected or duplicate skill KEY: " + key);
        }
        return result;
    }

    internal static void Write(PvfArchive source, string output)
    {
        if (Directory.Exists(output) || File.Exists(output)) throw new IOException("Output already exists: " + output);
        var entries = new Dictionary<string, object>(StringComparer.Ordinal);
        int actors = 0;
        foreach (var actor in Actors(source))
        {
            var programs = AiPrograms.Action(actor.Definition, skillOrdinals: Ordinals(source, actor.Path));
            foreach (var program in programs)
            {
                string path = actor.Path[..actor.Path.LastIndexOf('/')] + "/ai/" + program.Key;
                if (!source.Contains(path)) throw new InvalidDataException("Opening patch cannot introduce unregistered AI: " + path);
                var entry = new { path, data_type = 1, append = false, expected_before_sha256 = MapMigration.Hash(source.ReadRaw(path)), tokens = program.Value };
                if (entries.TryGetValue(path, out var previous) && JsonSerializer.Serialize(previous) != JsonSerializer.Serialize(entry))
                    throw new InvalidDataException("Conflicting shared female AI: " + path);
                entries[path] = entry;
            }
            actors++;
        }
        if (actors != 134) throw new InvalidDataException("Incomplete female support registry");
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "delta.json"), JsonSerializer.Serialize(new { schema = "support-profile-delta-v1", entries = entries.Values }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"SUPPORT_OPENING actors={actors} ai={entries.Count}; AIC/KEY/MAP/SKL/equipment unchanged");
    }
}
