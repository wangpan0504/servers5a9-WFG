using System.Text.Json;
using PvfLib;

// 只改当前已注册支援兵，外观、AI、KEY 与无关资源继续使用累计输入。
internal static class OverdriveDelta
{
    internal static void Write(PvfArchive source, string configurationPath, string outputPath, bool graduation = false, bool balanceOnly = false)
    {
        using var configuration = JsonDocument.Parse(File.ReadAllBytes(configurationPath));
        string output = Path.GetFullPath(outputPath);
        if (File.Exists(output) || Directory.Exists(output)) throw new IOException("Output must not exist: " + output);
        var root = configuration.RootElement;
        if (root.GetProperty("schema").GetString() != "support-overdrive-configuration-v2")
            throw new InvalidDataException("Unsupported support overdrive configuration");
        if (root.GetProperty("vision").GetInt32() != 0) throw new InvalidDataException("Verified overdrive vision must use native unlimited value zero");
        var registry = PvfItemIndex.Equipment(source.ReadTokens("equipment/equipment.lst"), source.ResolveString,
            path => source.FindFile(path)?.DataType == 1);
        var aicIndex = new Dictionary<int, string>();
        var list = source.ReadTokens("aicharacter/aicharacter.lst");
        if (list.Length % 2 != 0) throw new InvalidDataException("Malformed AIC registry");
        for (int i = 0; i < list.Length; i += 2)
        {
            if (list[i].Type != 0 || list[i + 1].Type != 6 ||
                !aicIndex.TryAdd(list[i].Value, "aicharacter/" + source.ResolveString(list[i + 1].Value).Replace('\\', '/').ToLowerInvariant()))
                throw new InvalidDataException("Malformed or duplicate AIC registry entry");
        }
        const string profilePath = "etc/support_companion_profiles.etc";
        var profiles = new PvfTokenDocument(source.ReadTokens(profilePath), source.ResolveString);
        var profileRows = profiles.Sections.Where(row => row.Name == "profile").ToArray();
        var equalRows = profiles.Sections.Where(row => row.Name == "equal tiers").ToArray();
        if (!profiles.Diagnostics.IsEmpty || profileRows.Length is < 1 or > 512 ||
            profiles.Sections.Any(row => row.Name is not ("profile" or "equal tiers")) || equalRows.Length > 1 ||
            equalRows.Any(row => row.IsBlock || !row.Children.IsEmpty || row.Values.Length != 1 || row.Values[0].Type != 0 || row.Values[0].Value != 1) ||
            profileRows.Any(row => !row.IsBlock || !row.Children.IsEmpty || row.Values.Length != 4 || row.Values[0].Type != 0 ||
                row.Values[1].Type != 6 || row.Values[2].Type != 0 || row.Values[3].Type != 6) ||
            profileRows.Select(row => row.Values[2].Value).Distinct().Count() != profileRows.Length)
            throw new InvalidDataException("Unsupported support profiles");
        var entries = new Dictionary<string, object>(StringComparer.Ordinal);
        var report = new List<object>();
        var modified = new HashSet<int>();
        var chainCache = new Dictionary<string, List<List<ScriptToken>>>(StringComparer.Ordinal);
        var documentCache = new Dictionary<List<ScriptToken>, PvfTokenDocument>();
        PvfTokenDocument? setIndex = null;
        var definitions = root.GetProperty("definitions").EnumerateArray().ToArray();
        if (!definitions.Select(row => row.GetProperty("base_id").GetInt32()).Order().SequenceEqual(new[] { 9986, 19973, 20900, 74990 }))
            throw new InvalidDataException("Configuration must cover four supports exactly once");
        if (graduation)
        {
            SupportGraduationDelta.Write(source, output, aicIndex, registry, profileRows, ValidateKit,
                (wear, job, grow, skills, eligible, priest) => EquipmentBonuses(wear, job, grow,
                    skills.ToDictionary(id => id, id => ReadSkill(job, id)), eligible, priest), balanceOnly);
            return;
        }
        foreach (var definition in definitions)
        {
            int baseId = definition.GetProperty("base_id").GetInt32();
            if (baseId is not (20900 or 9986 or 74990 or 19973)) throw new InvalidDataException("Unsupported support identity");
            string job = definition.GetProperty("job").GetString()!;
            if (TemplateDelta.Definitions.Single(row => row.Id == baseId).Job != job)
                throw new InvalidDataException("Support profession mismatch");
            var baseTokens = Read(aicIndex[baseId]);
            var minimum = Section(baseTokens, "minimum info");
            if (minimum.Count < 6 || minimum[1].Kind != 0 || minimum[2].Kind != 0 || minimum[5].Kind != 0 || minimum[5].Value != 95)
                throw new InvalidDataException("Support base must be level 95");
            var targets = definition.GetProperty("skills").EnumerateArray()
                .ToDictionary(row => row.GetProperty("id").GetInt32(), row => row.GetProperty("maximum").GetBoolean());
            var learned = Section(baseTokens, "skill");
            if (learned.Count % 2 != 0 || learned.Any(token => token.Kind != 0)) throw new InvalidDataException("Malformed learned skills");
            foreach (var row in targets)
            {
                if (!learned.Where((_, i) => i % 2 == 0).Any(token => token.Value == row.Key))
                    throw new InvalidDataException("Overdrive skill is unlearned");
            }
            var baseline = Enumerable.Range(0, learned.Count / 2).ToDictionary(i => learned[i * 2].Value, i => learned[i * 2 + 1].Value);
            if (targets.Keys.Any(skill => baseline[skill] is < 1 or > 255)) throw new InvalidDataException("Overdrive targets must already be learned");
            var skillInfo = targets.Keys.ToDictionary(id => id, id => ReadSkill(job, id));
            bool priestBuff = minimum[1].Value == 4 && minimum[2].Value == 1 && baseline.GetValueOrDefault(258) <= 0;
            bool buffOption = priestBuff || minimum[1].Value == 14 && minimum[2].Value == 1 || minimum[1].Value == 3 && minimum[2].Value == 5;
            var bundles = root.GetProperty("bundles").EnumerateArray().Select(row => row.EnumerateArray().Select(x => x.GetInt32()).ToArray()).ToArray();
            var weapons = root.GetProperty("weapons").GetProperty(job).EnumerateArray().Select(row => row.GetInt32()).ToArray();
            if (bundles.Length != 3 || bundles.Any(row => row.Length != 11) || weapons.Length is < 1 or > 16 || weapons.Distinct().Count() != weapons.Length)
                throw new InvalidDataException("Expected three eleven-piece bundles and a bounded profession weapon pool");
            // 首三项覆盖三个不同套装，其后有限循环职业武器的完整组合。
            var kits = Enumerable.Range(0, bundles.Length * weapons.Length)
                .Select(i => bundles[i % 3].Append(weapons[(i / 3 + i % 3) % weapons.Length]).ToArray()).ToArray();
            if (kits.Length is < 2 or > 64) throw new InvalidDataException("Support requires a bounded random equipment pool");
            foreach (var kit in kits) ValidateKit(kit, job);
            var rows = profileRows.Where(row => row.Values[0].Value == baseId).ToArray();
            if (rows.Length < 3) throw new InvalidDataException("Support requires at least three registered variants");
            foreach (int id in new[] { baseId }.Concat(rows.Select(row => row.Values[2].Value)).Distinct())
            {
                if (!modified.Add(id) || !aicIndex.TryGetValue(id, out string? path)) throw new InvalidDataException("Unknown or duplicate support variant");
                var tokens = Read(path);
                var info = Section(tokens, "minimum info");
                if (info.Count < 6 || info[1].Value != minimum[1].Value || info[2].Value != minimum[2].Value || info[5].Value != 95)
                    throw new InvalidDataException("Support variant class or level changed");
                var currentLearned = Section(tokens, "skill");
                if (currentLearned.Count % 2 != 0 || currentLearned.Any(token => token.Kind != 0) || targets.Keys.Any(skill =>
                    Enumerable.Range(0, currentLearned.Count / 2).Count(i => currentLearned[i * 2].Value == skill && currentLearned[i * 2 + 1].Value == baseline[skill]) != 1))
                    throw new InvalidDataException("Support variant learned targets differ from the current base");
                if (priestBuff && Enumerable.Range(0, currentLearned.Count / 2).Any(i => currentLearned[i * 2].Value == 258 && currentLearned[i * 2 + 1].Value > 0))
                    throw new InvalidDataException("Support variant independently learned priest skill258");
                // 三档强度统一；旧档次对技能列的百分比修正必须整体移除，不能叠加。
                Drop(tokens, "skill data up");
                if (root.TryGetProperty("vision", out var vision)) Set(tokens, "vision", [ScriptToken.Int(vision.GetInt32())]);
                Set(tokens, "warlike", [ScriptToken.Int(100)]);
                var rates = Section(tokens, "character status rate");
                const int moveIndex = 37;
                if (rates.Count != 41 || rates[moveIndex].Kind != 0)
                    throw new InvalidDataException("Unsupported AIC movement rate layout");
                rates[moveIndex] = ScriptToken.Int(checked(Section(baseTokens, "character status rate")[moveIndex].Value * 2));
                Set(tokens, "character status rate", rates);
                var wear = Section(tokens, "equipment");
                if (wear.Count % 3 != 0 || wear.Any(token => token.Kind != 0)) throw new InvalidDataException("Malformed equipment triples");
                var equipment = new List<ScriptToken>();
                for (int i = 0; i < wear.Count; i += 3)
                {
                    if (!registry.TryGetValue(wear[i].Value, out string? itemPath)) throw new InvalidDataException("Unknown equipped resource");
                    if (itemPath.Contains("/avatar/", StringComparison.Ordinal) || itemPath.Contains("/at_avatar/", StringComparison.Ordinal))
                        equipment.AddRange(wear.GetRange(i, 3));
                }
                int ordinal = id == baseId ? 0 : Array.FindIndex(rows, row => row.Values[2].Value == id);
                var selected = kits[ordinal % kits.Length];
                foreach (int item in selected) equipment.AddRange([ScriptToken.Int(item), ScriptToken.Int(0), ScriptToken.Int(0)]);
                var bonuses = EquipmentBonuses(equipment, job, minimum[2].Value, skillInfo, buffOption, priestBuff);
                var skills = new Dictionary<int, int>();
                var levels = new List<object>();
                foreach (var (skill, maximum) in targets)
                {
                    var data = skillInfo[skill];
                    int requested = maximum ? data.Levels : checked(baseline[skill] + 15 + bonuses[skill]);
                    int effective = Math.Min(requested, data.Levels), level = effective - bonuses[skill];
                    if (level is < 1 or > 255) throw new InvalidDataException("Equipment bonus leaves no safe learned level");
                    skills.Add(skill, level);
                    levels.Add(new { skill, data.Path, baseline = baseline[skill], requested_effective = requested, table_levels = data.Levels,
                        equipment_bonus = bonuses[skill], learned = level, effective, capped = requested > data.Levels, target = maximum ? "maximum" : "current-plus-15" });
                }
                foreach (string tag in new[] { "skill", "quick skill" })
                {
                    var values = Section(tokens, tag);
                    if (values.Count % 2 != 0 || values.Any(token => token.Kind != 0)) throw new InvalidDataException("Malformed skill pairs");
                    for (int i = 0; i < values.Count; i += 2)
                        if (skills.TryGetValue(values[i].Value, out int level)) values[i + 1] = ScriptToken.Int(level);
                    Set(tokens, tag, values);
                }
                Set(tokens, "equipment", equipment);
                Add(path, tokens);
                report.Add(new { template_id = id, base_id = baseId, skills = levels, kit = selected, preserved_avatar_count = equipment.Count / 3 - selected.Length,
                    move_rate_index = moveIndex, move_rate = rates[moveIndex].Value, buff_option_eligible = buffOption,
                    priest_skill258_base = baseline.GetValueOrDefault(258), priest_skill258_grant_guard = priestBuff });
            }
        }
        if (modified.Count != profileRows.Length + 4) throw new InvalidDataException("Configuration does not cover all support variants");
        var profileTokens = Read(profilePath);
        Set(profileTokens, "equal tiers", [ScriptToken.Int(1)]);
        Add(profilePath, profileTokens);
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "delta.json"), JsonSerializer.Serialize(new { schema = "support-profile-delta-v1", entries = entries.Values }, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(output, "overdrive-report.json"), JsonSerializer.Serialize(new { templates = report,
            configuration_sha256 = MapMigration.Hash(File.ReadAllBytes(configurationPath)), source_read_only = true,
            vision = "Native normal-APC unlimited distance (0); status 5 native fixed range retained", warlike = "Known safe high value 100; probability maximum not asserted",
            excluded_weapons = root.GetProperty("excluded_weapons") }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"OVERDRIVE_DELTA templates={modified.Count} resources={entries.Count} tiers=equal source_read_only=true");

        List<ScriptToken> Read(string path) => source.ReadTokens(path).Select(token => new ScriptToken(token.Type, token.Value,
            token.Type is 3 or 6 ? source.ResolveString(token.Value) : null, false, true)).ToList();
        void Add(string path, List<ScriptToken> tokens) => entries.Add(path, new { path, data_type = 1, append = false,
            expected_before_sha256 = MapMigration.Hash(source.ReadRaw(path)), tokens });
        void ValidateKit(int[] kit, string expectedJob)
        {
            if (kit.Length != 12 || kit.Distinct().Count() != 12) throw new InvalidDataException("Epic kit must contain 12 unique combat items");
            var types = new HashSet<string>(StringComparer.Ordinal);
            var sets = new Dictionary<int, int>();
            foreach (int item in kit)
            {
                if (!registry.TryGetValue(item, out string? path)) throw new InvalidDataException("Epic equipment is unregistered");
                var chain = EquipmentChain(path);
                var rarity = Effective(chain, "rarity");
                var level = Effective(chain, "minimum level");
                var jobs = Effective(chain, "usable job");
                var required = Effective(chain, "required skill");
                var type = Effective(chain, "equipment type");
                if (rarity.Count != 1 || rarity[0].Value != 4 || level.Count != 1 || level[0].Value != 95 ||
                    !jobs.Any(token => token.Text is "[all]" || token.Text == "[" + expectedJob.Replace("atpriest", "at priest") + "]") ||
                    required.Count != 0 || type.Count < 1 || type[0].Text is not { } slot || !types.Add(slot))
                    throw new InvalidDataException("Epic kit level, class, prerequisite or slot mismatch: " + path);
                var set = Effective(chain, "part set index");
                if (slot != "[weapon]")
                {
                    if (set.Count == 0 || set[0].Kind != 0 || set[0].Value <= 0) throw new InvalidDataException("Epic combat item lacks a real set");
                    sets[set[0].Value] = sets.GetValueOrDefault(set[0].Value) + 1;
                }
            }
            string[] wanted = ["[weapon]", "[coat]", "[shoulder]", "[pants]", "[shoes]", "[waist]", "[amulet]", "[wrist]", "[ring]", "[support]", "[magic stone]", "[earring]"];
            if (!types.SetEquals(wanted) || !sets.Values.Order().SequenceEqual(new[] { 3, 3, 5 }))
                throw new InvalidDataException("Epic kit must contain complete five/three/three sets");
        }
        PvfTokenDocument Document(List<ScriptToken> tokens)
        {
            if (documentCache.TryGetValue(tokens, out var found)) return found;
            var document = new PvfTokenDocument(tokens.Select(token => new PvfToken(token.Kind, token.Value)).ToArray(), source.ResolveString);
            if (!document.Diagnostics.IsEmpty) throw new InvalidDataException("Malformed equipment or skill declarations");
            documentCache.Add(tokens, document);
            return document;
        }
        PvfTokenSection[] Rows(List<List<ScriptToken>> chain, string tag) => chain
            .Select(tokens => Document(tokens).Sections.Where(row => row.Name == tag).ToArray()).FirstOrDefault(rows => rows.Length != 0) ?? [];
        SkillInfo ReadSkill(string job, int id)
        {
            var index = source.ReadTokens($"skill/{job}skill.lst");
            if (index.Length % 2 != 0) throw new InvalidDataException("Malformed skill index");
            var matches = Enumerable.Range(0, index.Length / 2).Where(i => index[i * 2].Type == 0 && index[i * 2].Value == id).ToArray();
            if (matches.Length != 1 || index[matches[0] * 2 + 1].Type != 6) throw new InvalidDataException("Missing or duplicate skill identity");
            string path = PvfArchive.NormalizePath("skill/" + source.ResolveString(index[matches[0] * 2 + 1].Value));
            var raw = Read(path);
            int tableStart = Find(raw, "level info"), tableEnd = Find(raw, "/level info");
            if (tableStart < 0 || tableEnd <= tableStart) throw new InvalidDataException("Missing ordinary skill table: " + path);
            // 只严格解析实际普通表及其前置声明；某些旧SKL的末尾另有无关坏说明块，不借其填充等级数据。
            var closed = raw.Where(token => token.Kind == 3 && token.Text!.StartsWith("[/", StringComparison.Ordinal))
                .Select(token => token.Text![2..^1]).ToHashSet(StringComparer.Ordinal);
            var context = new Stack<string>();
            foreach (var token in raw.Take(tableStart).Where(token => token.Kind == 3))
            {
                string tag = token.Text![1..^1];
                if (tag.StartsWith('/'))
                {
                    if (context.Count == 0 || context.Pop() != tag[1..]) throw new InvalidDataException("Malformed skill prefix: " + path);
                }
                else if (closed.Contains(tag)) context.Push(tag);
            }
            var prefix = raw.GetRange(0, tableEnd + 1);
            if (context.Count == 1 && context.Peek() == "dungeon")
                prefix.Add(raw.Single(token => token.Kind == 3 && token.Text == "[/dungeon]"));
            else if (context.Count != 0) throw new InvalidDataException("First level table is not ordinary dungeon data: " + path);
            PvfTokenDocument document;
            try { document = Document(prefix); }
            catch (InvalidDataException error) { throw new InvalidDataException("Malformed skill declarations: " + path, error); }
            var tables = document.Sections.Where(row => row.Name == "level info").ToArray();
            var dungeon = document.Sections.Where(row => row.Name == "dungeon").ToArray();
            if (dungeon.Length > 1) throw new InvalidDataException("Duplicate ordinary-dungeon mode: " + path);
            var dungeonTables = dungeon.SelectMany(row => row.Children.Where(child => child.Name == "level info")).ToArray();
            // 普通副本显式表优先；没有显式覆盖才读根表，绝不借用PVP等其它模式的表。
            if (dungeonTables.Length != 0) tables = dungeonTables;
            if (tables.Length != 1 || !tables[0].IsBlock || !tables[0].Children.IsEmpty || tables[0].Values.Length < 2 ||
                tables[0].Values[0].Type != 0 || tables[0].Values[0].Value <= 0 ||
                (tables[0].Values.Length - 1) % tables[0].Values[0].Value != 0 || tables[0].Values.Skip(1).Any(token => token.Type is not (0 or 2)))
                throw new InvalidDataException("Malformed or unsupported root level table: " + path);
            var required = document.Sections.Where(row => row.Name == "required level").ToArray();
            var type = document.Sections.Where(row => row.Name == "type").ToArray();
            if (required.Length != 1 || type.Length != 1 || raw.Any(token => token.Kind == 3 && token.Text is "[ex skill]" or "[tp skill]"))
                throw new InvalidDataException("Unsupported skill applicability: " + path);
            Label(type[0], "[active]");
            return new(path, (tables[0].Values.Length - 1) / tables[0].Values[0].Value, Scalar(required[0]));
        }
        Dictionary<int, int> EquipmentBonuses(List<ScriptToken> equipment, string job, int grow, Dictionary<int, SkillInfo> skills, bool buffOption, bool priestBuff)
        {
            string jobLabel = "[" + (job == "atpriest" ? "at priest" : job) + "]";
            var bonus = skills.Keys.ToDictionary(id => id, _ => 0);
            var sets = new Dictionary<int, int>();
            for (int i = 0; i < equipment.Count; i += 3)
            {
                var chain = EquipmentChain(registry[equipment[i].Value]);
                try
                {
                    foreach (var script in chain)
                    {
                        RejectConditional(Document(script).Sections, true);
                        if (priestBuff) RejectPriestSkillGrant(script);
                    }
                }
                catch (InvalidDataException error) { throw new InvalidDataException("Unsupported equipped level effect: " + registry[equipment[i].Value], error); }
                Apply(Rows(chain, "skill levelup"));
                Apply(Rows(chain, "all skill item container"));
                Apply(Rows(chain, "buff option"));
                var set = Rows(chain, "part set index");
                if (set.Length != 0)
                {
                    int id = Scalar(set.Single());
                    if (id <= 0) throw new InvalidDataException("Invalid equipped set identity");
                    sets[id] = sets.GetValueOrDefault(id) + 1;
                }
            }
            setIndex ??= Document(Read("etc/equipmentpartset.etc"));
            foreach (var (id, count) in sets)
            {
                var found = setIndex.Sections.Where(row => row.Name == "equipment part set" && row.Values.Length > 0 && row.Values[0].Type == 0 && row.Values[0].Value == id).ToArray();
                if (found.Length != 1 || !found[0].IsBlock || !found[0].Children.IsEmpty || found[0].Values.Length < 3 || found[0].Values[1].Type != 6)
                    throw new InvalidDataException("Missing, duplicate or unsupported equipped set");
                string setPath = source.ResolveString(found[0].Values[1].Value).Replace('\\', '/').TrimStart('/').ToLowerInvariant();
                if (!setPath.StartsWith("equipment/", StringComparison.Ordinal)) setPath = "equipment/" + setPath;
                var setChain = EquipmentChain(setPath);
                if (priestBuff) foreach (var script in setChain) RejectPriestSkillGrant(script);
                var tiers = Rows(setChain, "piece set ability");
                if (tiers.Length == 0) throw new InvalidDataException("Missing equipped set thresholds");
                var thresholds = new HashSet<int>();
                foreach (var tier in tiers)
                {
                    if (!tier.IsBlock || tier.Values.Length != 1 || tier.Values[0].Type != 0 || tier.Values[0].Value <= 0 || !thresholds.Add(tier.Values[0].Value))
                        throw new InvalidDataException("Malformed or duplicate set threshold");
                    RejectConditional(tier.Children, true);
                    if (count >= tier.Values[0].Value) Apply(tier.Children);
                }
            }
            return bonus;

            void Apply(IEnumerable<PvfTokenSection> rows)
            {
                foreach (var row in rows)
                {
                    if (row.Name == "buff option")
                    {
                        if (!row.IsBlock || !row.Values.IsEmpty) throw new InvalidDataException("Malformed native buff option");
                        // 原生B74门禁：女圣骑士14/1、奶萝3/5；奶爸4/1还要求258未学习且没有独立授予来源。
                        if (buffOption) Apply(row.Children);
                    }
                    else if (row.Name == "skill levelup")
                    {
                        if (!row.IsBlock || !row.Children.IsEmpty || row.Values.Length % 3 != 0) throw new InvalidDataException("Malformed direct skill bonus");
                        for (int at = 0; at < row.Values.Length; at += 3)
                        {
                            if (row.Values[at].Type != 6 || row.Values[at + 1].Type != 0 || row.Values[at + 2].Type != 0 || row.Values[at + 2].Value < 0)
                                throw new InvalidDataException("Unsupported direct skill bonus");
                            if (source.ResolveString(row.Values[at].Value) == jobLabel && bonus.ContainsKey(row.Values[at + 1].Value))
                                bonus[row.Values[at + 1].Value] = checked(bonus[row.Values[at + 1].Value] + row.Values[at + 2].Value);
                        }
                    }
                    else if (row.Name == "all skill item container")
                    {
                        if (!row.IsBlock || !row.Values.IsEmpty || row.Children.IsEmpty) throw new InvalidDataException("Malformed ranged skill bonus");
                        foreach (var item in row.Children)
                        {
                            Fields(item, "all skill item", "item growtype", "skill apply condition", "skill phase");
                            var growth = One(item, "item growtype");
                            if (growth.IsBlock || !growth.Children.IsEmpty || growth.Values.Length != 2 || growth.Values[0].Type != 6 || growth.Values[1].Type != 0)
                                throw new InvalidDataException("Unsupported skill bonus profession");
                            string profession = source.ResolveString(growth.Values[0].Value);
                            bool applies = profession == "[all]" && growth.Values[1].Value == 0 || profession == jobLabel && growth.Values[1].Value == grow;
                            Label(One(item, "skill phase"), "[all]");
                            var condition = One(item, "skill apply condition");
                            Fields(condition, "skill apply condition", "skill group", "lower bound level", "upper bound level", "value", "extra condition");
                            Label(One(condition, "skill group"), "[all]", "[active]");
                            Label(One(condition, "extra condition"), "[except for ex skill]", block: true);
                            int lower = Scalar(One(condition, "lower bound level")), upper = Scalar(One(condition, "upper bound level")), value = Scalar(One(condition, "value"));
                            if (lower < 0 || upper < lower || value < 0) throw new InvalidDataException("Unsupported skill bonus interval");
                            foreach (var (skill, data) in skills)
                                if (applies && data.Required >= lower && data.Required <= upper) bonus[skill] = checked(bonus[skill] + value);
                        }
                    }
                }
            }
        }
        void Label(PvfTokenSection row, string first, string? second = null, bool block = false)
        {
            if (row.IsBlock != block || !row.Children.IsEmpty || row.Values.Length != 1 || row.Values[0].Type != 6 ||
                !(source.ResolveString(row.Values[0].Value) == first || second is not null && source.ResolveString(row.Values[0].Value) == second))
                throw new InvalidDataException("Unsupported skill bonus label: " + row.Name);
        }
        List<List<ScriptToken>> EquipmentChain(string path)
        {
            if (chainCache.TryGetValue(path, out var cached)) return cached;
            string initialPath = path;
            var chain = new List<List<ScriptToken>>();
            var visited = new HashSet<string>(StringComparer.Ordinal);
            while (true)
            {
                if (chain.Count >= 16 || !visited.Add(path)) throw new InvalidDataException("Equipment inheritance cycle or depth");
                var tokens = Read(path); chain.Add(tokens);
                var inherited = Section(tokens, "import script");
                if (inherited.Count == 0) { chainCache.Add(initialPath, chain); return chain; }
                if (inherited.Count != 1 || inherited[0].Kind != 6 || inherited[0].Text is not { } parent)
                    throw new InvalidDataException("Unsupported equipment inheritance");
                path = parent.Replace('\\', '/').TrimStart('/').ToLowerInvariant();
                if (!path.StartsWith("equipment/", StringComparison.Ordinal)) path = "equipment/" + path;
            }
        }
    }

    sealed record SkillInfo(string Path, int Levels, int Required);
    static PvfTokenSection One(PvfTokenSection row, string name) => row.Children.Single(child => child.Name == name);
    static void Fields(PvfTokenSection row, string name, params string[] children)
    {
        if (row.Name != name || !row.IsBlock || !row.Values.IsEmpty || row.Children.Length != children.Length ||
            !row.Children.Select(child => child.Name).Order().SequenceEqual(children.Order())) throw new InvalidDataException("Unsupported skill bonus fields: " + row.Name);
    }
    static int Scalar(PvfTokenSection row)
    {
        if (row.IsBlock || !row.Children.IsEmpty || row.Values.Length != 1 || row.Values[0].Type != 0) throw new InvalidDataException("Expected integer field: " + row.Name);
        return row.Values[0].Value;
    }
    static void RejectConditional(IEnumerable<PvfTokenSection> rows, bool direct, bool insideBuffOption = false)
    {
        foreach (var row in rows)
        {
            bool level = row.Name is "skill levelup" or "all skill item container";
            if (row.Name == "skill level up" || level && !direct) throw new InvalidDataException("Conditional or unsupported skill-level effect: " + row.Name);
            if (row.Name == "buff option")
            {
                if (!direct || insideBuffOption || !row.IsBlock || !row.Values.IsEmpty) throw new InvalidDataException("Unsupported nested buff option");
                RejectConditional(row.Children, true, true);
            }
            else if (!level) RejectConditional(row.Children, false, insideBuffOption);
        }
    }
    static void RejectPriestSkillGrant(List<ScriptToken> script)
    {
        foreach (var token in script.Where(token => token.Kind == 3))
            if (token.Text is "[skill]" or "[learn skill]" or "[give skill]" or "[grant skill]" or "[append skill]" or "[add skill]")
                throw new InvalidDataException("Unsupported independent equipment skill grant");
        // 现有普通区间向量不会新学基础0的258；任何显式258或Avatar选择来源都须单独核证，不能静默忽略。
        for (int i = 0; i + 1 < script.Count; i++)
            if (script[i].Kind == 6 && script[i].Text == "[priest]" && script[i + 1].Kind == 0 && script[i + 1].Value == 258)
                throw new InvalidDataException("Explicit priest skill258 equipment source requires independent proof");
    }

    static List<ScriptToken> Effective(List<List<ScriptToken>> chain, string tag)
    {
        foreach (var tokens in chain) if (Find(tokens, tag) >= 0) return Section(tokens, tag);
        return [];
    }
    static int Find(List<ScriptToken> tokens, string tag) => tokens.FindIndex(token => token.Kind == 3 && token.Text == "[" + tag + "]");
    static List<ScriptToken> Section(List<ScriptToken> tokens, string tag)
    {
        int start = Find(tokens, tag);
        if (start < 0) return [];
        int end = start + 1; while (end < tokens.Count && tokens[end].Kind != 3) end++;
        return tokens.GetRange(start + 1, end - start - 1);
    }
    static void Set(List<ScriptToken> tokens, string tag, IEnumerable<ScriptToken> values)
    {
        int start = Find(tokens, tag);
        if (start < 0) { tokens.Add(ScriptToken.Tag("[" + tag + "]")); tokens.AddRange(values); return; }
        int end = start + 1; while (end < tokens.Count && tokens[end].Kind != 3) end++;
        tokens.RemoveRange(start + 1, end - start - 1); tokens.InsertRange(start + 1, values);
    }
    static void Drop(List<ScriptToken> tokens, string tag)
    {
        int start = Find(tokens, tag); if (start < 0) return;
        int closing = Find(tokens, "/" + tag);
        if (closing <= start) throw new InvalidDataException("Missing complete block: " + tag);
        tokens.RemoveRange(start, closing - start + 1);
        if (Find(tokens, tag) >= 0) throw new InvalidDataException("Duplicate block: " + tag);
    }
}
