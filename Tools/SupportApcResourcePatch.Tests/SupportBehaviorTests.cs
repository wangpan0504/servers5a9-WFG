using System.Text.Json;
using PvfLib;

internal static class SupportBehaviorTests
{
    internal static void Run(string pvf, string output)
    {
        int checks = 0;
        void Check(bool value, string label) { checks++; if (!value) throw new InvalidOperationException(label); }
        foreach (var definition in TemplateDelta.Definitions)
        {
            var ordinals = SupportBehaviorDelta.Ordinals(definition);
            var programs = AiPrograms.Action(definition, skillOrdinals: ordinals);
            var replay = new DecisionReplay(programs, definition) { Party = true };
            replay.Buffs.UnionWith(ordinals.Keys);
            replay.Active.UnionWith(ordinals.Keys.Where(id => id != 42 && id != 24 && id != 32));
            int small = definition.Id switch { 20900 => 107, 9986 => 46, 74990 => 16, _ => 333 };
            int buff = definition.Id switch { 20900 => 45, 9986 => 30, 74990 => 14, _ => 335 };
            replay.Skills.UnionWith(ordinals.Keys);
            Check(replay.Run() == -1, "All sufficient buffs without enemy must stay idle");
            if (definition.Id is 74990 or 19973)
            {
                int first = definition.Id == 74990 ? 14 : 330;
                int second = definition.Id == 74990 ? 21 : 335;
                int heal = definition.Id == 74990 ? 3 : 367;
                replay.Hp = 20; replay.Buffs.Remove(first); replay.Buffs.Remove(second);
                Check(replay.Run() == ordinals[first], "Opening first buff preempts low-health healing");
                replay.Buffs.Add(first);
                Check(replay.Run() == ordinals[second], "Opening second buff preempts low-health healing");
                replay.Buffs.Add(second);
                Check(replay.Run() == ordinals[heal], "Healing resumes after opening buffs are sufficient");
                replay.Hp = 100;
                Check(replay.Run() == -1, "Opening completion does not repeat sufficient buffs");
            }
            // 攻击测试只开放小技能，避免已有增益觉醒合法分支遮挡。
            replay.Skills.Clear(); replay.Skills.Add(small);
            foreach (int rank in new[] { 0, 1, 2, 3 })
            {
                replay.Enemy = new(50, 20, rank);
                Check(replay.Run() == ordinals[small], "All enemy ranks reach below50 skill KEY");
                replay.Skills.Add(buff); replay.Buffs.Remove(buff);
                Check(replay.Run() == ordinals[buff], "Missing buff must preempt attack");
                replay.Buffs.Add(buff);
                Check(replay.Run() == ordinals[small], "Sufficient buff must not preempt attack");
                replay.Enemy = new(4000, 800, rank);
                Check(replay.Run() == -1, "Out of skill range cannot attack empty space");
            }
            foreach (var enemy in new[] { new Enemy(50, 20, 1, Hostile: false), new Enemy(50, 20, 1, Dead: true), new Enemy(50, 20, 1, Type: 0x100) })
            {
                replay.Enemy = enemy; Check(replay.Run() == -1, "Friendly/dead/noncombat objects cannot trigger attack");
            }
            replay.Enemy = new(50, 20, 0); replay.Skills.Clear();
            Check(replay.Run() == -1, "All skills cooling down cannot fall back to basic KEY");
            foreach (int forbidden in definition.Id switch { 20900 => new[] { 81, 23 }, 9986 => [12], 74990 => [93], _ => Array.Empty<int>() })
            {
                replay.Skills.Add(forbidden);
                // 原男圣骑23虽留历史调度，但客户端已有拒绝；本轮新增禁放项必须无AI引用。
                if (forbidden != 23) Check(!programs.Values.SelectMany(v => v).Any(t => t.Text == "[check use skill]") || replay.Run() == -1, "Forbidden skill has no executable action branch");
                replay.Skills.Clear();
            }
            var returns = programs.Values.SelectMany(p => p.Zip(p.Skip(1))).Where(p => p.First.Text == "[return]").Select(p => p.Second.Value).ToArray();
            Check(!returns.Contains(definition.Active.Length), "No basic attack ordinal in generated decision graph");
            Check(returns.All(i => i == -1 || ordinals.Values.Contains(i)), "Every returned ordinal resolves to registered skill KEY");
            if (definition.Id == 74990)
            {
                replay.Skills.Add(16); replay.Active.Add(24);
                Check(replay.Run() == -1, "Holy song maintained state blocks new action");
            }
            if (definition.Id == 9986)
            {
                replay.Skills.Add(46); replay.Active.Add(42);
                Check(replay.Run() == -1, "Nen guard maintained state blocks new action");
            }
        }
        using var source = PvfArchive.Open(pvf);
        SupportBehaviorDelta.Write(source, output);
        using var delta = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(output, "delta.json")));
        int aics = 0;
        foreach (var entry in delta.RootElement.GetProperty("entries").EnumerateArray())
        {
            string path = entry.GetProperty("path").GetString()!;
            Check(path.StartsWith("aicharacter/", StringComparison.Ordinal), "Behavior delta cannot alter MAP/SKL/equipment");
            if (!path.EndsWith(".aic", StringComparison.Ordinal)) continue;
            aics++;
            var before = TemplateDelta.Read(source, path);
            var after = JsonSerializer.Deserialize<List<ScriptToken>>(entry.GetProperty("tokens"))!;
            Check(OutsideKeys(before).SequenceEqual(OutsideKeys(after)), "All non-KEY AIC tokens preserved including skills, movement, tiers and equipment");
            Check(entry.GetProperty("expected_before_sha256").GetString() == MapMigration.Hash(source.ReadRaw(path)), "Actual source raw identity");
            var old = Keys(before); var revised = Keys(after);
            Check(revised.Take(old.Length).SequenceEqual(old), "All original KEY ordinals preserved");
            Check(revised.Count(t => t.Text == "support_attack.key") == 1, "Basic KEY preserved once");
        }
        Check(aics == 168, "All four classes and all168 templates covered");
        bool rejected = false;
        try { SupportBehaviorDelta.Write(source, output); } catch (IOException) { rejected = true; }
        Check(rejected, "Existing output must not be overwritten");
        Console.WriteLine($"PASS {checks} four-class support behavior checks; actual PVF168 templates; native gameplay pending");
    }
    static ScriptToken[] Keys(List<ScriptToken> tokens) => tokens.SkipWhile(t => t.Text != "[key stream]").Skip(1).TakeWhile(t => t.Kind != 3).ToArray();
    static IEnumerable<ScriptToken> OutsideKeys(List<ScriptToken> tokens)
    {
        bool skip = false;
        foreach (var token in tokens) { if (token.Kind == 3) skip = token.Text == "[key stream]"; if (!skip) yield return token; }
    }
}
