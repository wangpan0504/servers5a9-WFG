using System.Text.Json;
using PvfLib;
using static SupportBehaviorDelta;

internal static class SupportBuffBoostTests
{
    internal static void Run(string beforePath, string afterPath, string output)
    {
        using var before = PvfArchive.Open(beforePath);
        using var after = PvfArchive.Open(afterPath);
        int checks = 0, columns = 0, negative = 0, newlyAdded = 0;
        void Check(bool condition, string message) { checks++; if (!condition) throw new InvalidDataException(message); }
        var actors = SupportBuffBoostDelta.Actors(before);
        Check(actors.Count == 168, "All current registered base and variant templates included");
        Check(actors.OrderBy(p => p.Key).SequenceEqual(SupportBuffBoostDelta.Actors(after).OrderBy(p => p.Key)), "Registry and profile selection preserved");
        foreach (var (_, actor) in actors)
        {
            var old = TemplateDelta.Read(before, actor.Path);
            var current = TemplateDelta.Read(after, actor.Path);
            var a = Modifiers(old); var b = Modifiers(current);
            RemoveModifiers(old); RemoveModifiers(current);
            Check(Equal(old, current), "Every non-modifier token preserved: " + actor.Path);
            var targets = SupportBuffBoostDelta.Targets(actor.Base).ToHashSet();
            if (actor.Base == 9986)
                Check(!targets.Any(p => p.Skill == 30) && a.Where(p => p.Key.Skill == 30).SequenceEqual(b.Where(p => p.Key.Skill == 30)),
                    "光之兵刃全部增益列保持正式99原值");
            foreach (var target in targets)
            {
                double x = a.GetValueOrDefault(target), y = b[target];
                Check(Math.Abs((100 + y) / (100 + x) - 1.15) < 0.000001, "Actual BUFF multiplier differs from 1.15");
                if (x < 0) negative++;
                if (!a.ContainsKey(target)) newlyAdded++;
                columns++;
            }
            Check(a.Where(p => !targets.Contains(p.Key)).All(p => b.GetValueOrDefault(p.Key) == p.Value), "Unrelated modifier preserved");
            Check(b.Count == a.Keys.Union(targets).Count(), "No duplicate or extra BUFF modifiers");
        }
        Check(negative > 0 && newlyAdded > 0, "Negative normalization and previously absent modifier paths covered");
        Check(before.FileCount == after.FileCount, "No new or removed PVF resource");
        File.WriteAllText(output, JsonSerializer.Serialize(new { passed = true, checks, templates = actors.Count, columns,
            negative_normalizations = negative, previously_unmodified_columns = newlyAdded,
            equipment_skills_ai_other_tokens_preserved = true }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS support BUFF boost: {checks} checks, {actors.Count} templates, {columns} columns");
    }

    private static Dictionary<(int Skill, int Column), double> Modifiers(List<ScriptToken> tokens)
    {
        var result = new Dictionary<(int, int), double>();
        if (!tokens.Any(t => t.Kind == 3 && t.Text == "[skill data up]")) return result;
        var rows = Section(tokens, "skill data up");
        if (rows.Count % 7 != 0) throw new InvalidDataException("Malformed modifiers");
        for (int i = 0; i < rows.Count; i += 7)
        {
            if (rows[i + 2].Text != "[dungeon]" || rows[i + 3].Text != "[level]" || rows[i + 5].Text != "%" || rows[i + 6].Kind != 2)
                throw new InvalidDataException("Unexpected current modifier layout");
            result.Add((rows[i + 1].Value, rows[i + 4].Value), BitConverter.Int32BitsToSingle(rows[i + 6].Value));
        }
        return result;
    }
    private static void RemoveModifiers(List<ScriptToken> tokens)
    {
        int i = tokens.FindIndex(t => t.Kind == 3 && t.Text == "[skill data up]");
        if (i >= 0) tokens.RemoveRange(i, Section(tokens, "skill data up").Count + 1);
    }
    private static bool Equal(List<ScriptToken> a, List<ScriptToken> b) => a.Count == b.Count &&
        a.Zip(b).All(p => p.First.Kind == p.Second.Kind && (p.First.Kind is 3 or 6
            ? p.First.Text == p.Second.Text : p.First.Value == p.Second.Value));
}
