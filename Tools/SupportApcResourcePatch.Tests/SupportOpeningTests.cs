using System.Text.Json;
using PvfLib;

internal static class SupportOpeningTests
{
    internal static void Run(string pvf, string output)
    {
        int checks = 0, actors = 0;
        void Check(bool value, string message) { checks++; if (!value) throw new InvalidOperationException(message); }
        using var source = PvfArchive.Open(pvf);
        foreach (var actor in SupportOpeningDelta.Actors(source))
        {
            actors++;
            var ordinals = SupportOpeningDelta.Ordinals(source, actor.Path);
            var replay = new DecisionReplay(AiPrograms.Action(actor.Definition, skillOrdinals: ordinals), actor.Definition) { Party = true, Hp = 20 };
            replay.Skills.UnionWith(ordinals.Keys);
            int first = actor.Definition.Id == 74990 ? 14 : 330;
            int second = actor.Definition.Id == 74990 ? 21 : 335;
            int heal = actor.Definition.Id == 74990 ? 3 : 367;
            Check(replay.Run() == ordinals[first], "Opening first buff must precede healing");
            replay.Buffs.Add(first);
            Check(replay.Run() == ordinals[second], "Opening second buff must precede healing");
            replay.Buffs.Add(second);
            Check(replay.Run() == ordinals[heal], "Healing resumes when opening buffs are sufficient");
            replay.Hp = 100; replay.Buffs.UnionWith(ordinals.Keys); replay.Active.UnionWith(ordinals.Keys.Where(s => s is not 24 and not 32));
            Check(replay.Run() == -1, "Sufficient buffs must not recast in empty room");
            int attack = actor.Definition.Id == 74990 ? 16 : 333;
            replay.Skills.Clear(); replay.Skills.Add(attack); replay.Enemy = new Enemy(50, 20, 0);
            Check(replay.Run() == ordinals[attack], "Attack continues when buffs are sufficient");
            replay.Skills.Add(second); replay.Buffs.Remove(second);
            Check(replay.Run() == ordinals[second], "Missing main buff preempts attack after death or expiry");
            replay.Buffs.Add(second);
            Check(replay.Run() == ordinals[attack], "Buff refill yields to attack");
            replay.Skills.Clear();
            Check(replay.Run() == -1, "No cooldown-ready skills cannot become ordinary attack");
        }
        Check(actors == 134, "All female templates covered");
        SupportOpeningDelta.Write(source, output);
        using var delta = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(output, "delta.json")));
        foreach (var entry in delta.RootElement.GetProperty("entries").EnumerateArray())
        {
            string path = entry.GetProperty("path").GetString()!;
            Check(path.StartsWith("aicharacter/support/", StringComparison.Ordinal) && path.Contains("/ai/", StringComparison.Ordinal) && path.EndsWith(".ai", StringComparison.Ordinal), "Only support AI resources");
            Check(!entry.GetProperty("append").GetBoolean(), "All referenced AI already registered");
            Check(entry.GetProperty("expected_before_sha256").GetString() == MapMigration.Hash(source.ReadRaw(path)), "Source identity retained");
        }
        bool rejected = false;
        try { SupportOpeningDelta.Write(source, output); } catch (IOException) { rejected = true; }
        Check(rejected, "Existing output protected");
        Console.WriteLine($"PASS {checks} female support opening checks; 134 templates; gameplay pending");
    }
}
