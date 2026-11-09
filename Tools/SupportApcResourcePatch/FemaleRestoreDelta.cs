using System.Text.Json;
using PvfLib;

// 只恢复两女专用资源；地图必须同时有原迁移证明、当前准入和未变动友方行。
internal static class FemaleRestoreDelta
{
    internal static void Write(PvfArchive source, string sourcePath, string configPath, string outputPath)
    {
        string output = Path.GetFullPath(outputPath);
        if (Directory.Exists(output) || File.Exists(output)) throw new IOException("Output already exists: " + output);
        using var config = JsonDocument.Parse(File.ReadAllBytes(configPath));
        var root = config.RootElement;
        var templates = new Dictionary<string, List<ScriptToken>>(StringComparer.Ordinal);
        var planEvidence = new List<object>();
        List<ScriptToken>? historicalRegistry = null, historicalProfiles = null;
        foreach (var value in root.GetProperty("template_plans").EnumerateArray())
        {
            string path = value.GetString()!;
            byte[] bytes = File.ReadAllBytes(path);
            using var document = JsonDocument.Parse(bytes);
            foreach (var entry in document.RootElement.GetProperty("entries").EnumerateArray())
            {
                string resource = entry.GetProperty("path").GetString()!;
                bool female = IsFemalePath(resource);
                if (!female && resource is not ("aicharacter/aicharacter.lst" or "etc/support_companion_profiles.etc")) continue;
                if (!entry.TryGetProperty("tokens", out var tokenData)) throw new InvalidDataException("Expected explicit tokens: " + resource);
                var tokens = JsonSerializer.Deserialize<List<ScriptToken>>(tokenData.GetRawText())!;
                // 旧资源的索引属于旧字符串池，必须按文本重新定位，绝不携带旧索引。
                tokens = tokens.Select(t => t.Kind is 3 or 6
                    ? t.Text is not null ? new ScriptToken(t.Kind, 0, t.Text, true) : throw new InvalidDataException("Missing historical text")
                    : t).ToList();
                if (female) templates[resource] = tokens;
                else if (resource == "aicharacter/aicharacter.lst") historicalRegistry = tokens;
                else historicalProfiles = tokens;
            }
            planEvidence.Add(new { path = Path.GetFullPath(path), sha256 = MapMigration.Hash(bytes) });
        }
        if (historicalRegistry is null || historicalProfiles is null) throw new InvalidDataException("Historical registry and profiles required");
        var entries = new Dictionary<string, object>(StringComparer.Ordinal);
        var registry = TemplateDelta.Read(source, "aicharacter/aicharacter.lst");
        var occupied = Registry(registry);
        var restored = Registry(historicalRegistry).Where(p => IsFemaleId(p.Key)).ToDictionary();
        if (restored.Count != 134 || restored.Keys.Any(occupied.ContainsKey) || restored.Values.Any(p => !IsFemalePath(p) || !templates.ContainsKey(p)))
            throw new InvalidDataException("Female registry is incomplete, occupied or outside dedicated resources");
        foreach (var pair in restored.OrderBy(p => p.Key))
        {
            registry.Add(ScriptToken.Int(pair.Key));
            registry.Add(ScriptToken.String(pair.Value["aicharacter/".Length..]));
        }
        Add("aicharacter/aicharacter.lst", registry);
        var profiles = TemplateDelta.Read(source, "etc/support_companion_profiles.etc");
        var femaleProfiles = new List<(int Base, string Tier, int Id)>();
        for (int i = 0; i < historicalProfiles.Count; i++)
        {
            if (historicalProfiles[i] is not { Kind: 3, Text: "[profile]" }) continue;
            if (i + 5 >= historicalProfiles.Count || historicalProfiles[i + 5] is not { Kind: 3, Text: "[/profile]" })
                throw new InvalidDataException("Malformed profile row");
            int id = historicalProfiles[i + 1].Value;
            if (id is not (74990 or 19973)) continue;
            var row = historicalProfiles.GetRange(i, 6);
            int variant = row[3].Value;
            string tier = row[2].Text ?? "";
            if (!restored.ContainsKey(variant) || tier is not ("entry" or "graduate" or "huge") ||
                (id == 74990 ? variant is < 74000 or > 74047 : variant is < 19000 or > 19083))
                throw new InvalidDataException("Invalid female tier registration");
            femaleProfiles.Add((id, tier, variant));
            profiles.AddRange(row);
        }
        if (femaleProfiles.Count != 132 || femaleProfiles.Select(r => r.Id).Distinct().Count() != 132 ||
            new[] {74990, 19973}.Any(id => new[] {"entry", "graduate", "huge"}.Any(t => !femaleProfiles.Any(r => r.Base == id && r.Tier == t))))
            throw new InvalidDataException("Missing or duplicate female tiers");
        Add("etc/support_companion_profiles.etc", profiles);
        var equipment = PvfItemIndex.Equipment(source.ReadTokens("equipment/equipment.lst"), source.ResolveString,
            resource => source.FindFile(resource)?.DataType == 1);
        var aicEvidence = new List<object>();
        foreach (var pair in restored.OrderBy(p => p.Key))
        {
            var definition = TemplateDelta.Definitions.Single(d => d.Id == (pair.Key is 74990 or >= 74000 and <= 74047 ? 74990 : 19973));
            var tokens = templates[pair.Value];
            var minimum = Section(tokens, "minimum info");
            if (minimum.Count < 6 || minimum[1].Value != (definition.Id == 74990 ? 14 : 3) || minimum[2].Value != definition.Grow || minimum[3].Value != 2 || minimum[5].Value != 95)
                throw new InvalidDataException("Female profession, awakening or level mismatch: " + pair.Value);
            var kit = Section(tokens, "equipment");
            if (kit.Count % 3 != 0 || kit.Any(t => t.Kind != 0) || Enumerable.Range(0, kit.Count / 3).Any(i => !equipment.ContainsKey(kit[i * 3].Value)))
                throw new InvalidDataException("Unregistered historical equipment: " + pair.Value);
            var keys = Section(tokens, "key stream").Select(t => t.Text).ToArray();
            var expectedKeys = definition.Active.SelectMany(id => new[] { "", $"support_{id}.key" }).ToArray();
            if (!keys.SequenceEqual(expectedKeys)) throw new InvalidDataException("Historical KEY order mismatch: " + pair.Value);
            Replace(tokens, "key stream", expectedKeys.Concat(["", "support_attack.key"]).Select(ScriptToken.String));
            string directory = pair.Value[..pair.Value.LastIndexOf('/')];
            foreach (var program in AiPrograms.Action(definition, includeBasicAttack: true)) templates[directory + "/ai/" + program.Key] = program.Value;
            templates[directory + "/key/support_attack.key"] = AiPrograms.BasicAttack();
            foreach (int skill in definition.Active)
                if (!templates.ContainsKey(directory + $"/key/support_{skill}.key")) throw new InvalidDataException("Missing paired KEY");
            // 目的地和移动都使用现行奶爸的受益者跟随链，不恢复旧PvP追击。
            foreach (string file in new[] {"destinationselect.ai", "movemethod.ai"})
                templates[directory + "/ai/" + file] = AiPrograms.Escort(source,
                    "aicharacter/priest/heal_soldier/ai/" + file, definition);
            aicEvidence.Add(new { id = pair.Key, path = pair.Value, job = minimum[1].Value, grow = minimum[2].Value,
                level = minimum[5].Value, main_buff = definition.Id == 74990 ? 14 : 335,
                skills = Section(tokens, "skill"), corrections = Section(tokens, "skill correction"), equipment = kit });
        }
        foreach (var pair in templates.OrderBy(p => p.Key)) Add(pair.Key, pair.Value);

        string eligibilityPath = root.GetProperty("map_eligibility").GetString()!;
        using var eligibility = JsonDocument.Parse(File.ReadAllBytes(eligibilityPath));
        if (!string.Equals(Path.GetFullPath(eligibility.RootElement.GetProperty("pvf").GetString()!), Path.GetFullPath(sourcePath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("MAP eligibility was computed on another PVF");
        var allowed = eligibility.RootElement.GetProperty("allowed").EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        var blocked = eligibility.RootElement.GetProperty("blocked").EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        string migrationPath = root.GetProperty("migration_audit").GetString()!;
        var migration = JsonSerializer.Deserialize<MigrationAudit>(File.ReadAllBytes(migrationPath)) ?? throw new InvalidDataException("Missing migration evidence");
        if (migration.Schema != "support-apc-map-migration-v1" || migration.Rows.Select(r => r.MapPath).Distinct().Count() != migration.Rows.Length)
            throw new InvalidDataException("Unsupported MAP evidence");
        var maps = new List<object>();
        var skipped = new List<object>();
        foreach (var proof in migration.Rows)
        {
            string path = proof.MapPath;
            string? reason = !allowed.Contains(path) ? "not-currently-approved" : blocked.Contains(path) ? "also-referenced-by-excluded-mode" :
                !source.Contains(path) ? "missing-current-resource" : null;
            if (reason is not null) { skipped.Add(new { path, reason }); continue; }
            if (proof.NewValues.Length != 6 || proof.NewValues[0] != 9986 || proof.Faction != "[character]" || proof.Rank != "[normal]")
                throw new InvalidDataException("Invalid migration proof: " + path);
            List<MapRow> rows;
            try { rows = MapMigration.Rows(source, path); }
            catch (InvalidDataException e) { skipped.Add(new { path, reason = "unsupported-current-shape", detail = e.Message }); continue; }
            var row = rows.SingleOrDefault(r => r.Ordinal == proof.NewOrdinal && r.Values.AsSpan().SequenceEqual(proof.NewValues) && r.Faction == proof.Faction && r.Rank == proof.Rank);
            if (row is null || rows.Count(r => r.Values[0] == 9986) != 1 || rows.Any(r => IsFemaleId(r.Values[0])))
            { skipped.Add(new { path, reason = "historical-row-no-longer-matches" }); continue; }
            var tokens = TemplateDelta.Read(source, path);
            int end = tokens.FindIndex(t => t is { Kind: 3, Text: "[/ai character]" });
            if (end < 0) throw new InvalidDataException("Missing actor block end: " + path);
            var additions = new List<ScriptToken>();
            foreach (int id in new[] {74990, 19973})
            {
                additions.AddRange(proof.NewValues.Take(4).Select((v, i) => ScriptToken.Int(i == 0 ? id : v)));
                additions.Add(ScriptToken.String(proof.Faction)); additions.Add(ScriptToken.String(proof.Rank));
                additions.AddRange(proof.NewValues.Skip(4).Select(ScriptToken.Int));
            }
            tokens.InsertRange(end, additions);
            Add(path, tokens);
            maps.Add(new { path, source_row = proof.NewOrdinal, unchanged_values = proof.NewValues, original_rows = rows.Count,
                appended_ordinals = new[] {rows.Count, rows.Count + 1}, source_sha256 = MapMigration.Hash(source.ReadRaw(path)) });
        }
        if (maps.Count == 0) throw new InvalidDataException("No currently proven eligible MAP");
        Directory.CreateDirectory(output);
        var options = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(Path.Combine(output, "delta.json"), JsonSerializer.Serialize(new { schema = "support-profile-delta-v1", entries = entries.Values }, options));
        File.WriteAllText(Path.Combine(output, "restore-report.json"), JsonSerializer.Serialize(new { source_pvf = Path.GetFullPath(sourcePath), source_read_only = true,
            template_plans = planEvidence, migration_audit = new { path = migrationPath, sha256 = MapMigration.Hash(File.ReadAllBytes(migrationPath)) },
            map_eligibility = new { path = eligibilityPath, sha256 = MapMigration.Hash(File.ReadAllBytes(eligibilityPath)) },
            registry_count = restored.Count, profile_count = femaleProfiles.Count, templates = aicEvidence, maps, skipped,
            resource_count = entries.Count, release_ready = false, pending = new[] { "candidate readback and full non-target raw audit", "native/wide world approval", "server tests and paired client DLL" } }, options));
        Console.WriteLine($"FEMALE_RESTORE templates={restored.Count} profiles={femaleProfiles.Count} maps={maps.Count} skipped={skipped.Count} resources={entries.Count}; source read-only; candidate not yet validated");

        void Add(string path, List<ScriptToken> tokens) => entries.Add(path, new { path, data_type = 1, append = !source.Contains(path),
            expected_before_sha256 = source.Contains(path) ? MapMigration.Hash(source.ReadRaw(path)) : "", tokens });
    }
    static bool IsFemalePath(string path) => path.StartsWith("aicharacter/support/femalecrusader/", StringComparison.Ordinal) || path.StartsWith("aicharacter/support/enchantress/", StringComparison.Ordinal);
    static bool IsFemaleId(int id) => id is 74990 or 19973 or >= 74000 and <= 74047 or >= 19000 and <= 19083;
    static Dictionary<int, string> Registry(List<ScriptToken> tokens)
    {
        if (tokens.Count % 2 != 0) throw new InvalidDataException("Invalid registry length");
        var result = new Dictionary<int, string>();
        for (int i = 0; i < tokens.Count; i += 2)
        {
            if (tokens[i].Kind != 0 || tokens[i + 1].Kind != 6 || tokens[i + 1].Text is null) throw new InvalidDataException("Invalid registry pair");
            result.Add(tokens[i].Value, PvfArchive.NormalizePath("aicharacter/" + tokens[i + 1].Text));
        }
        return result;
    }
    static List<ScriptToken> Section(List<ScriptToken> tokens, string name)
    {
        int start = tokens.FindIndex(t => t.Kind == 3 && t.Text == "[" + name + "]");
        if (start < 0) throw new InvalidDataException("Missing section: " + name);
        int end = start + 1;
        while (end < tokens.Count && tokens[end].Kind != 3) end++;
        return tokens.GetRange(start + 1, end - start - 1);
    }
    static void Replace(List<ScriptToken> tokens, string name, IEnumerable<ScriptToken> replacement)
    {
        var section = Section(tokens, name);
        int start = tokens.FindIndex(t => t.Kind == 3 && t.Text == "[" + name + "]");
        tokens.RemoveRange(start + 1, section.Count);
        tokens.InsertRange(start + 1, replacement);
    }
}
