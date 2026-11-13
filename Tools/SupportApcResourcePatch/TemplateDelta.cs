using System.Text.Json;
using PvfLib;

internal sealed record ScriptToken(byte Kind, int Value, string? Text = null, bool Unicode = false, bool Original = false)
{
    internal static ScriptToken Int(int value) => new(0, value);
    internal static ScriptToken Float(float value) => new(2, BitConverter.SingleToInt32Bits(value));
    internal static ScriptToken Tag(string text) => new(3, 0, text);
    internal static ScriptToken String(string text) => new(6, 0, text, true);
}
internal sealed record SkillDeclaration(string Tag, int TokenIndex, string[] Values);
internal sealed record SkillDependency(int Id, int Level, string Path, string RawSha256, SkillDeclaration[]? Declarations = null);
internal sealed record TemplateDefinition(int Id, string Name, string Source, string Target, string Job, int Grow,
    int[] Active, int[] Passive);

internal static class TemplateDelta
{
    internal static readonly TemplateDefinition[] Definitions =
    [
        new(20900, "王富贵", "aicharacter/priest/heal_soldier/heal_soldier.aic", "aicharacter/priest/heal_soldier/heal_soldier.aic", "priest", 1,
            [51,26,52,45,20,19,23,22,21,240,239,84,238,107], []),
        new(9986, "光兵", "aicharacter/fighter/28/28.aic", "aicharacter/support/guangbing/guangbing.aic", "fighter", 1,
            [30,36,42,67,26,12], [27,35,29,72,8,21,78,90,15,13,23,5,46,17,58,181,182,184,172,179,170,187,97,169]),
        new(74990, "女圣骑士", "aicharacter/pvp/atpriest/crusader/crusader[master].aic", "aicharacter/support/femalecrusader/femalecrusader.aic", "atpriest", 1,
            [3,6,14,21,23,24,32,28,30,33,93,16,31], [17,34,169,174,175,188,180]),
        new(19973, "小魔女", "aicharacter/_weresist/magician/firewitch/body.aic", "aicharacter/support/enchantress/enchantress.aic", "mage", 5,
            [367,339,335,330,337,332,345,343,350,333], [331,369,342,347,368,334,169,174])
    ];

    // 此命令只形成可审阅资源计划；原生查询合同未闭合时没有可安装的空动作替身。
    internal static void WritePlan(PvfArchive source, string outputPath)
    {
        string output = Path.GetFullPath(outputPath);
        if (File.Exists(output) || Directory.Exists(output)) throw new IOException("Output must not exist: " + output);
        var entries = new List<object>();
        var dependencies = new List<object>();
        var equipmentDependencies = new List<object>();
        var removedEquipment = new List<object>();
        var pending = new List<string>
        {
            "W01-W04: verified recipient, effective buff instance, remaining timer, mutual exclusion and fail-closed client contract",
            "W05-W09: per-skill utility state, effective recipient range and actual movement interruption guard using verified native contracts",
            "W16: actual enchantress puppet availability and stable real-player favoritism selection"
        };
        var registry = Read(source, "aicharacter/aicharacter.lst");
        foreach (var definition in Definitions)
        {
            var aic = Read(source, definition.Source);
            var nativeSkills = Pairs(source, aic, "skill");
            var index = SkillIndex(source, definition.Job);
            var skills = new SortedDictionary<int, SkillDependency>();
            foreach (int skill in definition.Active.Concat(definition.Passive)) AddSkill(skill, 1);
            if (definition.Id == 20900)
            {
                // 已有专用模板学习配置保留；攻击分支在新的动作决策中移除。
                foreach (var pair in nativeSkills)
                    if (!skills.ContainsKey(pair.Key) && index.TryGetValue(pair.Key, out string? path))
                        skills.Add(pair.Key, new(pair.Key, pair.Value, path, MapMigration.Hash(source.ReadRaw(path))));
            }
            int minimum = Find(source, aic, "minimum info");
            if (minimum < 0 || aic.Count <= minimum + 6 || aic[minimum + 1].Kind != 6 || aic[minimum + 6].Kind != 0)
                throw new InvalidDataException("Unsupported minimum info: " + definition.Source);
            aic[minimum + 1] = ScriptToken.String(definition.Name);
            aic[minimum + 6] = ScriptToken.Int(95);
            // 第二整数经原生 EA4 setter 写入 actor+4504，并选择 0x59 步长成长属性行。
            // 第三整数经 EA8 独立写入 actor+4508；2 进入二觉职业资源链。
            // 第四整数只控制成长外观，保留来源，不能用它替代觉醒阶段。
            if (aic[minimum + 4].Kind != 0 || aic[minimum + 5].Kind != 0)
                throw new InvalidDataException("Unsupported awakening/appearance layout: " + definition.Source);
            aic[minimum + 4] = ScriptToken.Int(2);
            if (definition.Id == 19973)
            {
                if (aic[minimum + 2] != ScriptToken.Int(3) || aic[minimum + 3].Kind != 0)
                    throw new InvalidDataException("Unsupported enchantress source class/growth layout");
                aic[minimum + 3] = ScriptToken.Int(5);
            }
            Replace(source, aic, "skill", skills.Values.SelectMany(s => new[] { ScriptToken.Int(s.Id), ScriptToken.Int(s.Level) }));
            Replace(source, aic, "quick skill", definition.Active.Take(6).SelectMany(id => new[] { ScriptToken.Int(id), ScriptToken.Int(skills[id].Level) }));
            Replace(source, aic, "key stream", definition.Active.SelectMany(id => new[] { ScriptToken.String(""), ScriptToken.String($"support_{id}.key") }));
            if (AiPrograms.HasBasicAttack(definition))
                Replace(source, aic, "key stream", Section(source, aic, "key stream").Concat([ScriptToken.String(""), ScriptToken.String("support_attack.key")]));
            Replace(source, aic, "ai pattern", new[] { "Event.ai", "DestinationSelect.ai", "Action.ai", "MoveMethod.ai" }.Select(ScriptToken.String));
            Set(source, aic, "vision", ScriptToken.Int(1200));
            Set(source, aic, "warlike", ScriptToken.Int(0));
            Set(source, aic, "think term", ScriptToken.Int(definition.Id == 9986 ? 200 : 100));
            Set(source, aic, "attack damage rate", ScriptToken.Float(1.0f));
            Drop(source, aic, "USE FSM AI");
            Drop(source, aic, "team status rate");
            Drop(source, aic, "speech on situation");
            Drop(source, aic, "limit passiveobject");
            Replace(source, aic, "skill correction", definition.Id == 20900 ? Section(source, Read(source, definition.Source), "skill correction") : []);
            if (definition.Id != 20900)
            {
                CopySection(source, aic, Definitions[0].Source, "party member");
                CopySection(source, aic, Definitions[0].Source, "additional character status");
                CopySection(source, aic, Definitions[0].Source, "character status rate");
            }
            if (definition.Id != 20900)
            {
                var equipment = PvfItemIndex.Equipment(source.ReadTokens("equipment/equipment.lst"), source.ResolveString,
                    path => source.FindFile(path)?.DataType == 1);
                var equipped = Section(source, aic, "equipment");
                if (equipped.Count < 3 || equipped.Count % 3 != 0 || equipped.Any(t => t.Kind != 0))
                    throw new InvalidDataException("Unsupported support source equipment");
                var safe = new List<ScriptToken>();
                var selectedDirectories = new Dictionary<string, (int Id, string Path)>(StringComparer.Ordinal);
                for (int at = 0; at < equipped.Count; at += 3)
                {
                    int oldId = equipped[at].Value;
                    if (!equipment.TryGetValue(oldId, out string? oldPath))
                    {
                        removedEquipment.Add(new { template_id = definition.Id, equipment_id = oldId, reason = "not registered in current equipment index" });
                        continue;
                    }
                    bool avatar = oldPath.Contains("/avatar/", StringComparison.Ordinal) || oldPath.Contains("/at_avatar/", StringComparison.Ordinal);
                    if (oldPath.Contains("/title/", StringComparison.Ordinal))
                    {
                        removedEquipment.Add(new { template_id = definition.Id, equipment_id = oldId, reason = "source combat title excluded from support template" });
                        continue;
                    }
                    string slotDirectory = oldPath[..(oldPath.LastIndexOf('/') + 1)];
                    if (definition.Id == 19973 && at == 0) slotDirectory = "equipment/character/mage/weapon/broom/";
                    (int Id, string Path) selected;
                    if (avatar)
                    {
                        if (!NoTriggers(source, Read(source, oldPath)))
                            throw new InvalidDataException("Support source avatar contains active effects: " + oldPath);
                        selected = (oldId, oldPath);
                    }
                    else if (!selectedDirectories.TryGetValue(slotDirectory, out selected))
                    {
                        selected = equipment.Where(pair => pair.Value.StartsWith(slotDirectory, StringComparison.Ordinal))
                            .OrderBy(pair => pair.Key).Select(pair => (pair.Key, pair.Value, Script: Read(source, pair.Value)))
                            .Where(item => Number(source, item.Script, "minimum level", int.MaxValue) <= 95 &&
                                Number(source, item.Script, "rarity", int.MaxValue) <= 1 && NoTriggers(source, item.Script))
                            .Select(item => (item.Key, item.Value)).FirstOrDefault();
                        if (selected.Path is null) throw new InvalidDataException("Missing registered plain equipment: " + slotDirectory);
                        selectedDirectories.Add(slotDirectory, selected);
                    }
                    safe.Add(ScriptToken.Int(selected.Id));
                    safe.Add(avatar ? equipped[at + 1] : ScriptToken.Int(0));
                    safe.Add(avatar ? equipped[at + 2] : ScriptToken.Int(0));
                    equipmentDependencies.Add(new { template_id = definition.Id, source_equipment_id = oldId,
                        equipment_id = selected.Id, equipment_path = selected.Path,
                        raw_sha256 = MapMigration.Hash(source.ReadRaw(selected.Path)) });
                }
                if (safe.Count == 0) throw new InvalidDataException("Missing safe support equipment");
                Replace(source, aic, "equipment", safe);
            }
            AddEntry(definition.Target, aic, definition.Source);
            string directory = definition.Target[..definition.Target.LastIndexOf('/')];
            string originalDirectory = definition.Source[..definition.Source.LastIndexOf('/')];
            var nativeKeys = source.Files.Where(file => file.Path.StartsWith(originalDirectory + "/key/", StringComparison.Ordinal) && file.DataType == 1)
                .Select(file => (file.Path, Tokens: Read(source, file.Path))).ToArray();
            foreach (int id in definition.Active)
            {
                var nativeKey = nativeKeys.Where(key => key.Tokens.Any(token => token.Kind == 6 && source.ResolveString(token.Value) == $"skill:{id}"))
                    .OrderBy(key => key.Tokens.Count).ThenBy(key => key.Path, StringComparer.Ordinal).FirstOrDefault();
                List<ScriptToken> program;
                string copiedFrom;
                if (nativeKey.Tokens is not null)
                {
                    program = nativeKey.Tokens;
                    copiedFrom = nativeKey.Path;
                }
                else if (definition.Id == 74990)
                {
                    copiedFrom = originalDirectory + "/key/prayerofheal.key";
                    program = Read(source, copiedFrom);
                    int directive = program.FindIndex(token => token.Kind == 6 && source.ResolveString(token.Value) == "skill:3");
                    if (directive < 0) throw new InvalidDataException("Unsupported native female-crusader input template");
                    program[directive] = ScriptToken.String($"skill:{id}");
                }
                else
                {
                    copiedFrom = "aicharacter/priest/heal_soldier/key/1.key";
                    program = [ScriptToken.Tag("[input]"), ScriptToken.String($"skill:{id}"), ScriptToken.Int(30), ScriptToken.String("time"), ScriptToken.Int(100)];
                }
                AddEntry($"{directory}/key/support_{id}.key",
                    program, copiedFrom);
            }
            if (AiPrograms.HasBasicAttack(definition))
                AddEntry(directory + "/key/support_attack.key", AiPrograms.BasicAttack(), "aicharacter/_bizarre/priest/mirror_priest/key/x.key");
            // Event无动作是既有事件合同；其余三个决策脚本不能用空返回冒充实现。
            AddEntry(directory + "/ai/event.ai", Read(source, "aicharacter/priest/heal_soldier/ai/event.ai"), "aicharacter/priest/heal_soldier/ai/event.ai");
            foreach (var program in AiPrograms.Action(definition))
                AddEntry(directory + "/ai/" + program.Key, program.Value, "aicharacter/priest/heal_soldier/ai/action.ai");
            foreach (string leaf in new[] { "destinationselect.ai", "movemethod.ai" })
            {
                string escortSource = "aicharacter/priest/heal_soldier/ai/" + leaf;
                AddEntry(directory + "/ai/" + leaf, AiPrograms.Escort(source, escortSource, definition), escortSource);
            }
            dependencies.Add(new { definition.Id, definition.Source, source_raw_sha256 = MapMigration.Hash(source.ReadRaw(definition.Source)),
                skills = skills.Values, active_priority_pending = definition.Active });
            if (definition.Id != 20900)
            {
                for (int i = 0; i < registry.Count; i += 2)
                    if (registry[i].Kind != 0 || registry[i + 1].Kind != 6 || registry[i].Value == definition.Id)
                        throw new InvalidDataException("Invalid registry or reserved support ID occupied: " + definition.Id);
                registry.Add(ScriptToken.Int(definition.Id));
                registry.Add(ScriptToken.String(definition.Target["aicharacter/".Length..]));
            }

            void AddSkill(int id, int minimumLevel)
            {
                if (skills.TryGetValue(id, out var existing) && existing.Level >= minimumLevel) return;
                if (!index.TryGetValue(id, out string? path) || !source.Contains(path))
                {
                    if (nativeSkills.TryGetValue(id, out int nativeLevel) && !definition.Active.Contains(id))
                    {
                        skills[id] = new(id, nativeLevel, definition.Source + "#[skill]:" + id,
                            MapMigration.Hash(source.ReadRaw(definition.Source)));
                        return;
                    }
                    throw new InvalidDataException($"Missing real skill {definition.Job}:{id}");
                }
                var script = Read(source, path);
                int required = Number(source, script, "required level", 1);
                int range = Number(source, script, "required level range", 0);
                int maximum = Number(source, script, "maximum level", 1);
                var growCaps = Section(source, script, "growtype maximum level").Where(t => t.Kind == 0).ToArray();
                if (growCaps.Length > definition.Grow && growCaps[definition.Grow].Value > 0)
                    maximum = Math.Min(maximum, growCaps[definition.Grow].Value);
                int level = nativeSkills.GetValueOrDefault(id, Math.Min(maximum, range > 0 ? Math.Max(1, (95 - required) / range + 1) : 1));
                level = Math.Max(level, minimumLevel);
                if (level < 1 || level > 255 || required > 95) throw new InvalidDataException("Invalid learned skill level: " + path);
                skills[id] = new(id, level, path, MapMigration.Hash(source.ReadRaw(path)), Describe(source, script));
                var prerequisites = Section(source, script, "pre required skill").ToArray();
                if (prerequisites.Length % 2 != 0 || prerequisites.Any(t => t.Kind != 0))
                    throw new InvalidDataException("Invalid prerequisite declaration: " + path);
                for (int at = 0; at < prerequisites.Length; at += 2)
                    if (prerequisites[at].Value > 0) AddSkill(prerequisites[at].Value, prerequisites[at + 1].Value);
            }
        }
        AddEntry("aicharacter/aicharacter.lst", registry, "aicharacter/aicharacter.lst");
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "template-plan.json"), JsonSerializer.Serialize(new
        {
            schema = "support-apc-resource-delta-v1", release_ready = false,
            requires_paired_aic_ai_key_and_client = true, pending, entries, dependencies,
            equipment_dependencies = equipmentDependencies, removed_equipment = removedEquipment,
            survival_source = new { path = Definitions[0].Source,
                sections = new[] { "party member", "additional character status", "character status rate" },
                copied_equipment = false, invincibility_added = false }
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"TEMPLATE_PLAN entries={entries.Count} releaseReady=false pending={pending.Count}; original doll and shared SKL unchanged");

        void AddEntry(string path, List<ScriptToken> tokens, string sourcePath)
        {
            bool exists = source.Contains(path);
            entries.Add(new { path, data_type = 1, append = !exists,
                expected_before_sha256 = exists ? MapMigration.Hash(source.ReadRaw(path)) : "", tokens,
                copied_from = sourcePath, expected_copy_sha256 = MapMigration.Hash(source.ReadRaw(sourcePath)) });
        }
    }

    static Dictionary<int, string> SkillIndex(PvfArchive source, string job)
    {
        var tokens = source.ReadTokens($"skill/{job}skill.lst");
        var index = new Dictionary<int, string>();
        if (tokens.Length % 2 != 0) throw new InvalidDataException("Invalid skill index");
        for (int at = 0; at < tokens.Length; at += 2)
        {
            if (tokens[at].Type != 0 || tokens[at + 1].Type != 6) throw new InvalidDataException("Invalid skill index token");
            index.Add(tokens[at].Value, PvfArchive.NormalizePath("skill/" + source.ResolveString(tokens[at + 1].Value)));
        }
        return index;
    }
    static bool NoTriggers(PvfArchive source, List<ScriptToken> tokens)
    {
        string[] forbidden = ["[if]", "[then]", "[appendage]", "[skill data up]", "[special function]", "[active skill]", "[passive skill]"];
        return !tokens.Any(token => Tag(source, token) is string tag && forbidden.Contains(tag, StringComparer.OrdinalIgnoreCase));
    }
    internal static List<ScriptToken> Read(PvfArchive source, string path) => source.ReadTokens(path)
        .Select(t => t.Type is 3 or 6 ? new ScriptToken(t.Type, t.Value, source.ResolveString(t.Value), (t.Value & 1) != 0, true)
            : new ScriptToken(t.Type, t.Value)).ToList();
    static string? Tag(PvfArchive source, ScriptToken token) => token.Kind == 3 ? token.Text ?? source.ResolveString(token.Value) : null;
    static int Find(PvfArchive source, List<ScriptToken> tokens, string tag) => tokens.FindIndex(t => string.Equals(Tag(source, t), "[" + tag + "]", StringComparison.OrdinalIgnoreCase));
    static List<ScriptToken> Section(PvfArchive source, List<ScriptToken> tokens, string tag)
    {
        int at = Find(source, tokens, tag);
        if (at < 0) return [];
        int end = at + 1;
        while (end < tokens.Count && tokens[end].Kind != 3) end++;
        return tokens[(at + 1)..end];
    }
    static Dictionary<int, int> Pairs(PvfArchive source, List<ScriptToken> tokens, string tag)
    {
        var values = Section(source, tokens, tag);
        if (values.Count % 2 != 0 || values.Any(t => t.Kind != 0)) throw new InvalidDataException("Invalid AIC pairs: " + tag);
        var result = new Dictionary<int, int>();
        for (int at = 0; at < values.Count; at += 2) result[values[at].Value] = values[at + 1].Value;
        return result;
    }
    static int Number(PvfArchive source, List<ScriptToken> tokens, string tag, int fallback) => Section(source, tokens, tag).FirstOrDefault(t => t.Kind == 0)?.Value ?? fallback;
    static SkillDeclaration[] Describe(PvfArchive source, List<ScriptToken> tokens)
    {
        string[] relevant = ["[required level]", "[required level range]", "[maximum level]", "[growtype maximum level]",
            "[pre required skill]", "[skill fitness growtype]", "[executable states]", "[consume item]", "[consume MP]", "[cool time]", "[casting time]"];
        var result = new List<SkillDeclaration>();
        for (int at = 0; at < tokens.Count; at++)
        {
            string? tag = Tag(source, tokens[at]);
            if (tag is null || !relevant.Contains(tag, StringComparer.OrdinalIgnoreCase)) continue;
            int end = at + 1;
            while (end < tokens.Count && tokens[end].Kind != 3) end++;
            result.Add(new(tag, at, tokens[(at + 1)..end].Select(token => token.Kind == 6
                ? token.Text ?? source.ResolveString(token.Value)
                : token.Kind == 2 ? BitConverter.Int32BitsToSingle(token.Value).ToString(System.Globalization.CultureInfo.InvariantCulture)
                : token.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray()));
        }
        return result.ToArray();
    }
    static void Replace(PvfArchive source, List<ScriptToken> tokens, string tag, IEnumerable<ScriptToken> values)
    {
        var replacement = values.ToList();
        int at = Find(source, tokens, tag);
        if (at < 0) { tokens.Add(ScriptToken.Tag("[" + tag + "]")); tokens.AddRange(replacement); tokens.Add(ScriptToken.Tag("[/" + tag + "]")); return; }
        int end = at + 1;
        while (end < tokens.Count && tokens[end].Kind != 3) end++;
        tokens.RemoveRange(at + 1, end - at - 1);
        tokens.InsertRange(at + 1, replacement);
    }
    static void Set(PvfArchive source, List<ScriptToken> tokens, string tag, ScriptToken value)
    {
        int at = Find(source, tokens, tag);
        if (at < 0) { tokens.Add(ScriptToken.Tag("[" + tag + "]")); tokens.Add(value); return; }
        if (at + 1 >= tokens.Count || tokens[at + 1].Kind == 3) throw new InvalidDataException("Missing scalar: " + tag);
        tokens[at + 1] = value;
    }
    static void Drop(PvfArchive source, List<ScriptToken> tokens, string tag)
    {
        int at = Find(source, tokens, tag);
        if (at < 0) return;
        int end = tokens.FindIndex(at + 1, t => string.Equals(Tag(source, t), "[/" + tag + "]", StringComparison.OrdinalIgnoreCase));
        if (end < 0) { end = at + 1; while (end < tokens.Count && tokens[end].Kind != 3) end++; }
        else end++;
        tokens.RemoveRange(at, end - at);
    }
    static void CopySection(PvfArchive source, List<ScriptToken> tokens, string path, string tag) => Replace(source, tokens, tag, Section(source, Read(source, path), tag));
}
