using System.Text.Json;
using PvfLib;
using static SupportBehaviorDelta;

// 装备沿王富贵模板逐字段继承；毕业档奶量由原生技能表反算，不修改最终属性读取。
internal static class SupportGraduationDelta
{
    internal static void Write(PvfArchive source, string output, Dictionary<int, string> actors,
        IReadOnlyDictionary<int, string> equipment, PvfTokenSection[] profiles, Action<int[], string> validateKit,
        Func<List<ScriptToken>, string, int, int[], bool, bool, Dictionary<int, int>> bonuses)
    {
        var reference = TemplateDelta.Read(source, actors[20905]);
        var combat = Combat(reference);
        int[] referenceIds = [100050794, 100150664, 100100695, 100250658, 100200639,
            100300946, 100312640, 100322533, 100344897, 100353080, 100390020, 108000558];
        if (!Enumerable.Range(0, combat.Count / 3).Select(i => combat[3 * i].Value).SequenceEqual(referenceIds))
            throw new InvalidDataException("Wang reference equipment changed; review the current kit");
        int[] cloth = [100050817, 100150687, 100100718, 100250681, 100200662];
        foreach (int id in cloth)
        {
            var item = TemplateDelta.Read(source, equipment[id]);
            if (Section(item, "name") is not [{ Text: { } name }] || !name.Contains("超界", StringComparison.Ordinal) ||
                !name.Contains("苍天", StringComparison.Ordinal))
                throw new InvalidDataException("Expected actual super Tayberrs cloth armor");
        }
        var identities = new Dictionary<int, (int Base, string Tier)>
        {
            [20900] = (20900, "entry"), [9986] = (9986, "entry"),
            [74990] = (74990, "entry"), [19973] = (19973, "entry")
        };
        foreach (var row in profiles)
            if (!identities.TryAdd(row.Values[2].Value, (row.Values[0].Value, source.ResolveString(row.Values[1].Value))))
                throw new InvalidDataException("Duplicate support profile");
        var entries = new List<object>();
        var report = new List<object>();
        foreach (var (id, identity) in identities.OrderBy(row => row.Key))
        {
            int tier = identity.Tier switch { "entry" => 0, "graduate" => 1, "huge" => 2,
                _ => throw new InvalidDataException("Unknown support tier") };
            bool father = identity.Base == 20900, female = identity.Base == 74990, enchantress = identity.Base == 19973;
            if (father && tier != 2) continue;
            string job = father ? "priest" : female ? "atpriest" : enchantress ? "mage" : "fighter";
            var tokens = TemplateDelta.Read(source, actors[id]);
            var minimum = Section(tokens, "minimum info");
            int jobId = father ? 4 : female ? 14 : enchantress ? 3 : 1;
            if (minimum.Count < 6 || minimum[1].Value != jobId || minimum[2].Value != (enchantress ? 5 : 1) || minimum[5].Value != 95)
                throw new InvalidDataException("Support identity changed");
            var wear = Section(tokens, "equipment");
            if (!father)
            {
                var replacement = new List<ScriptToken>();
                for (int at = 0; at < wear.Count; at += 3)
                    if (Avatar(wear[at].Value)) replacement.AddRange(wear.GetRange(at, 3));
                var newCombat = combat.ToList();
                if (enchantress) newCombat[^3] = ScriptToken.Int(106040533);
                if (!female && !enchantress)
                {
                    for (int at = 0; at < cloth.Length; at++) newCombat[at * 3] = ScriptToken.Int(cloth[at]);
                    newCombat[^3] = ScriptToken.Int(102000570);
                }
                replacement.AddRange(newCombat);
                Set(tokens, "equipment", replacement);
                wear = replacement;
            }
            var kit = Combat(tokens);
            validateKit(Enumerable.Range(0, kit.Count / 3).Select(i => kit[3 * i].Value).ToArray(), job);
            var skills = Section(tokens, "skill");
            if (father && Enumerable.Range(0, skills.Count / 2).Any(i => skills[2 * i].Value == 258 && skills[2 * i + 1].Value > 0))
                throw new InvalidDataException("Priest buff-option eligibility requires unlearned skill258");
            var levels = new List<object>();
            var columns = new List<object>();
            if (father || female || enchantress)
            {
                int main = father ? 45 : female ? 14 : 335, awakening = father ? 84 : female ? 33 : 343;
                var oldWear = Section(TemplateDelta.Read(source, actors[id]), "equipment");
                var oldBonus = bonuses(oldWear, job, minimum[2].Value, [main, awakening], true, father);
                var newBonus = bonuses(wear, job, minimum[2].Value, [main, awakening], true, father);
                int mainLevel = father ? 40 : new[] { 20, 30, 35 }[tier];
                foreach (int skill in new[] { main, awakening })
                {
                    int at = Enumerable.Range(0, skills.Count / 2).Select(i => i * 2).Single(i => skills[i].Value == skill);
                    int effective = skill == main ? mainLevel : 40;
                    if (skills[at + 1].Value + oldBonus[skill] != effective || effective - newBonus[skill] is < 1 or > 255)
                        throw new InvalidDataException("Current effective skill levels changed");
                    skills[at + 1] = ScriptToken.Int(effective - newBonus[skill]);
                    var quick = Section(tokens, "quick skill");
                    for (int q = 0; q < quick.Count; q += 2) if (quick[q].Value == skill) quick[q + 1] = skills[at + 1];
                    Set(tokens, "quick skill", quick);
                    levels.Add(new { skill, old_equipment_bonus = oldBonus[skill], equipment_bonus = newBonus[skill], learned = skills[at + 1].Value, effective });
                }
                Set(tokens, "skill", skills);
                if (tier == 2)
                {
                    var modifiers = Section(tokens, "skill data up");
                    int expected = father ? 35 : 49;
                    if (modifiers.Count != expected) throw new InvalidDataException("Unexpected existing dedicated modifiers");
                    for (int i = 0; i < expected / 7; i++)
                    {
                        int oldSkill = i < 5 ? main : awakening;
                        int oldColumn = i < 5 ? i + (father || female ? 1 : 2) : i - 5 + (female ? 4 : 3);
                        if (modifiers[7 * i].Text != "[" + (female ? "at priest" : job) + "]" ||
                            modifiers[7 * i + 1].Value != oldSkill || modifiers[7 * i + 2].Text != "[dungeon]" ||
                            modifiers[7 * i + 3].Text != "[level]" || modifiers[7 * i + 4].Value != oldColumn ||
                            modifiers[7 * i + 5].Text != "%" || modifiers[7 * i + 6].Kind != 2)
                            throw new InvalidDataException("Existing modifiers require review before replacement");
                    }
                    var mainTable = Table(father ? "skill/priest/gloriousbless.skl" : female ? "skill/atpriest/blessofcourage.skl" : "skill/mage/forbiddencurse.skl", 8);
                    var wakeTable = Table(father ? "skill/priest/apocalypse.skl" : female ? "skill/atpriest/cruxofvictoria.skl" : "skill/mage/marionette.skl", father ? 14 : female ? 12 : 10);
                    int stat = father ? 8650 : 11000, mainDivisor = father ? 630 : female ? 675 : 665;
                    var revised = new List<ScriptToken>();
                    for (int i = 0; i < 5; i++)
                    {
                        int column = i + (father || female ? 1 : 2);
                        bool strength = father ? i < 2 : i >= 3;
                        if (mainTable.Constants[(father ? 2 : 1) + i].Value != mainDivisor)
                            throw new InvalidDataException("Main buff scaling denominator changed");
                        Add(main, column, mainTable.Values[1 + (mainLevel - 1) * 8 + column].Value,
                            (strength ? 14750 : 2350) * 0.74, mainDivisor);
                    }
                    for (int i = 0; i < 2; i++)
                    {
                        int column = (father ? 0 : female ? 4 : 3) + i;
                        if (wakeTable.Constants[(father ? 6 : female ? 3 : 1) + i].Value != 750)
                            throw new InvalidDataException("Awakening scaling denominator changed");
                        Add(awakening, column, wakeTable.Values[1 + 39 * (father ? 14 : female ? 12 : 10) + column].Value,
                            1668 * (1.0 + 8650 / 750.0) * 0.74, 750);
                    }
                    // 旧百分比是同一组列的归一参数，整体替换，禁止在旧值上追加叠乘。
                    Set(tokens, "skill data up", revised);

                    void Add(int skill, int column, int baseline, double target, int divisor)
                    {
                        float percent = (float)(100 * (target / (baseline * (1.0 + stat / (double)divisor)) - 1));
                        if (baseline <= 0 || !float.IsFinite(percent) || percent <= -100 || percent > 1000)
                            throw new InvalidDataException("Invalid native skill normalization");
                        revised.AddRange([ScriptToken.String("[" + (female ? "at priest" : job) + "]"), ScriptToken.Int(skill),
                            ScriptToken.String("[dungeon]"), ScriptToken.String("[level]"), ScriptToken.Int(column), ScriptToken.String("%"), ScriptToken.Float(percent)]);
                        columns.Add(new { skill, column, baseline, stat, divisor, percent, target });
                    }
                }
            }
            entries.Add(new { path = actors[id], data_type = 1, append = false,
                expected_before_sha256 = MapMigration.Hash(source.ReadRaw(actors[id])), tokens });
            report.Add(new { id, base_id = identity.Base, tier = identity.Tier,
                equipment_triples = kit, reference_id = 20905, skills = levels, columns });
        }
        Directory.CreateDirectory(output);
        var options = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(Path.Combine(output, "delta.json"), JsonSerializer.Serialize(new { schema = "support-profile-delta-v1", entries }, options));
        File.WriteAllText(Path.Combine(output, "graduation-report.json"), JsonSerializer.Serialize(new { templates = report,
            policy = "Native dedicated skill-column inverse formula; huge tier only; preserve song and favoritism; combat triples copied from Wang20905, avatars unchanged",
            game_QA = "pending" }, options));
        Console.WriteLine($"SUPPORT_GRADUATION resources={entries.Count} read_only_source=true");

        bool Avatar(int id) => equipment.TryGetValue(id, out string? path) &&
            (path.Contains("/avatar/", StringComparison.Ordinal) || path.Contains("/at_avatar/", StringComparison.Ordinal));
        List<ScriptToken> Combat(List<ScriptToken> tokens)
        {
            var wear = Section(tokens, "equipment");
            if (wear.Count % 3 != 0 || wear.Any(t => t.Kind != 0)) throw new InvalidDataException("Malformed combat triples");
            var result = new List<ScriptToken>();
            for (int i = 0; i < wear.Count; i += 3)
            {
                if (!equipment.ContainsKey(wear[i].Value)) throw new InvalidDataException("Unregistered equipment");
                if (!Avatar(wear[i].Value)) result.AddRange(wear.GetRange(i, 3));
            }
            if (result.Count != 36) throw new InvalidDataException("Expected 12 actual combat equipment items");
            return result;
        }
        (List<ScriptToken> Values, List<ScriptToken> Constants) Table(string path, int width)
        {
            var raw = TemplateDelta.Read(source, path);
            int start = raw.FindIndex(t => t.Text == "[dungeon]"), end = raw.FindIndex(t => t.Text == "[/dungeon]");
            if (start >= 0)
            {
                if (end <= start) throw new InvalidDataException("Missing dungeon skill table");
                raw = raw.GetRange(start + 1, end - start - 1);
            }
            else raw = raw.Take(raw.FindIndex(t => t.Text == "[/level info]") + 1).ToList();
            var values = Section(raw, "level info");
            if (values.Count < 1 + 40 * width || values[0].Value != width || (values.Count - 1) % width != 0 || values.Any(t => t.Kind != 0))
                throw new InvalidDataException("Native ordinary skill table changed");
            return (values, Section(raw, "static data"));
        }
    }
}
