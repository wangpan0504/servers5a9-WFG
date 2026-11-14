using System.Text;
using System.Text.Json;
using PvfLib;
using static SupportBehaviorDelta;

internal static class SupportGraduationTests
{
    internal static void Run(string pvf, string configuration, string output, bool balanceOnly = false)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        using var source = PvfArchive.Open(pvf);
        OverdriveDelta.Write(source, configuration, output, graduation: true, balanceOnly: balanceOnly);
        using var delta = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(output, "delta.json")));
        var list = TemplateDelta.Read(source, "aicharacter/aicharacter.lst");
        var tiers = new Dictionary<int, (int Base, string Tier)> { [9986] = (9986, "entry"), [74990] = (74990, "entry"), [19973] = (19973, "entry") };
        var profiles = TemplateDelta.Read(source, "etc/support_companion_profiles.etc");
        for (int i = 0; i < profiles.Count; i++)
            if (profiles[i].Text == "[profile]" && (profiles[i + 1].Value != 20900 || profiles[i + 2].Text == "huge"))
                tiers.Add(profiles[i + 3].Value, (profiles[i + 1].Value, profiles[i + 2].Text!));
        var paths = Enumerable.Range(0, list.Count / 2).Where(i => tiers.ContainsKey(list[2 * i].Value))
            .ToDictionary(i => PvfArchive.NormalizePath("aicharacter/" + list[2 * i + 1].Text), i => list[2 * i].Value);
        var entries = delta.RootElement.GetProperty("entries").EnumerateArray().ToArray();
        int checks = 0, graduates = 0;
        void Check(bool value, string message) { checks++; if (!value) throw new InvalidOperationException(message); }
        Check(entries.Length == (balanceOnly ? 45 : tiers.Count) && (balanceOnly || entries.Length == 165),
            "Exactly the selected support templates, with no other tiers in the balance-only delta");
        var seen = new HashSet<int>();
        int[] common = [100050794, 100150664, 100100695, 100250658, 100200639, 100300946, 100312640, 100322533, 100344897, 100353080, 100390020, 108000558];
        foreach (var entry in entries)
        {
            string path = entry.GetProperty("path").GetString()!;
            int id = paths[path];
            Check(seen.Add(id) && tiers.ContainsKey(id), "No extra resources or duplicate templates");
            var identity = tiers[id];
            bool father = identity.Base == 20900, female = identity.Base == 74990, mage = identity.Base == 19973;
            bool support = father || female || mage, graduation = identity.Tier == "huge" && support;
            var before = TemplateDelta.Read(source, path);
            var after = entry.GetProperty("tokens").Deserialize<List<ScriptToken>>()!;
            Check(!entry.GetProperty("append").GetBoolean() && entry.GetProperty("expected_before_sha256").GetString() == MapMigration.Hash(source.ReadRaw(path)), "Raw guards match actual source");
            var oldWear = Section(before, "equipment"); var wear = Section(after, "equipment");
            if (balanceOnly)
            {
                Check(graduation, "Balance-only mode cannot touch another tier or Guangbing");
                Check(oldWear.SequenceEqual(wear) && Section(before, "skill").SequenceEqual(Section(after, "skill")) &&
                    Section(before, "quick skill").SequenceEqual(Section(after, "quick skill")),
                    "Balance-only mode preserves all equipment triples and effective learned levels");
            }
            Check(oldWear.Take(oldWear.Count - 36).SequenceEqual(wear.Take(wear.Count - 36)), "All avatar triples unchanged");
            var kit = wear.TakeLast(36).ToArray();
            var expected = common.ToArray();
            if (mage) expected[^1] = 106040533;
            if (!support)
            {
                new[] { 100050817, 100150687, 100100718, 100250681, 100200662 }.CopyTo(expected, 0);
                expected[^1] = 102000570;
            }
            if (!balanceOnly)
                Check(Enumerable.Range(0, 12).All(i => kit[3 * i].Value == expected[i] && kit[3 * i + 1].Value == 0 && kit[3 * i + 2].Value == 0), "Combat IDs and all reference triple fields match Wang");
            if (support)
            {
                int main = father ? 45 : female ? 14 : 335, awakening = father ? 84 : female ? 33 : 343;
                var learnedBefore = Section(before, "skill"); var learnedAfter = Section(after, "skill");
                Check(learnedBefore.Count == learnedAfter.Count && Enumerable.Range(0, learnedBefore.Count / 2).All(i =>
                    learnedBefore[2 * i].Equals(learnedAfter[2 * i]) && (learnedBefore[2 * i].Value == main || learnedBefore[2 * i].Value == awakening || learnedBefore[2 * i + 1].Equals(learnedAfter[2 * i + 1]))), "Only two equipment-sensitive learned levels may change");
                if (father) Check(!Enumerable.Range(0, learnedAfter.Count / 2).Any(i => learnedAfter[2 * i].Value == 258 && learnedAfter[2 * i + 1].Value > 0), "Do not independently learn priest258");
                var modifiers = Section(after, "skill data up");
                if (!graduation) Check(modifiers.SequenceEqual(Section(before, "skill data up")), "Other tier parameters unchanged");
                else
                {
                    graduates++;
                    Check(modifiers.Count == 49, "Five main and two awakening stat columns only");
                    for (int i = 0; i < 7; i++)
                    {
                        int skill = i < 5 ? main : awakening;
                        int column = i < 5 ? i + (father || female ? 1 : 2) : i - 5 + (father ? 0 : female ? 4 : 3);
                        Check(modifiers[7 * i + 1].Value == skill && modifiers[7 * i + 4].Value == column && modifiers[7 * i + 5].Text == "%", "Only native stat columns, preserve duration and damage");
                        float correction = BitConverter.Int32BitsToSingle(modifiers[7 * i + 6].Value);
                        int baseline = i >= 5 ? 1668 : father ? (i < 2 ? 598 : 109) : i < 3 ? 80 : 434;
                        int stat = father ? 8650 : 11000;
                        int divisor = i >= 5 ? 750 : father ? 630 : female ? 675 : 665;
                        double target = balanceOnly
                            ? i >= 5 ? 12376.1152 : father ? (i < 2 ? 8732 : 1391.2) : i < 3 ? 1391.2 : 8732
                            : i >= 5 ? 15470.144 : father ? (i < 2 ? 10915 : 1739) : i < 3 ? 1739 : 10915;
                        float actual = baseline * (1 + correction / 100) * (1 + (float)stat / divisor);
                        if (balanceOnly && mage)
                        {
                            // 335的红关心有15%原生增幅，343不在偏爱增幅表，不能重复除或乘。
                            float favored = actual * (i < 5 ? 1.15f : 1);
                            Check(float.IsFinite(favored) && Math.Abs(favored - target * 1.05) < 0.01,
                                "Enchantress reaches exactly 105 percent after real favoritism; awakening has no favoritism multiplier");
                            Check(i >= 5 || actual < target, "Enchantress main base must not receive another extra 15 percent on top of the 105-percent target");
                        }
                        else Check(float.IsFinite(actual) && Math.Abs(actual - target) < 0.01,
                            "Independent native baseline formula reaches the selected cumulative reduction");
                    }
                }
                Set(before, "skill data up", []); Set(after, "skill data up", []);
            }
            foreach (string section in new[] { "equipment", "skill", "quick skill" })
            {
                if (!support && section != "equipment") Check(Section(before, section).SequenceEqual(Section(after, section)), "Guangbing skills unchanged");
                Set(before, section, []); Set(after, section, []);
            }
            Check(before.SequenceEqual(after), "Status, AI, KEY, damage, appearance and every unrelated field unchanged");
        }
        Check(graduates == 45, "All three graduation professions and variants reduced");
        bool rejected = false;
        try { OverdriveDelta.Write(source, configuration, output, graduation: true, balanceOnly: balanceOnly); } catch (IOException) { rejected = true; }
        Check(rejected, "Never overwrite prior outputs");
        Console.WriteLine($"PASS {checks} support equipment, native formula, graduation-only and preserved gameplay checks");
    }
}
