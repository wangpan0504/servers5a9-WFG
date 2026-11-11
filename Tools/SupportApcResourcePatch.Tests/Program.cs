using System.Text.Json;
using PvfLib;

if (args is ["--balanced-tiers", var balancedPvf, var balancedEvidence, var balancedOutput])
{
    SupportBalancedTierTests.Run(balancedPvf, balancedEvidence, balancedOutput);
    return;
}

if (args is ["--damage", var damagePvf, var damageOutput])
{
    SupportDamageTests.Run(damagePvf, damageOutput);
    return;
}

if (args is ["--opening", var openingPvf, var openingOutput])
{
    SupportOpeningTests.Run(openingPvf, openingOutput);
    return;
}

if (args is ["--effective-tiers", var effectivePvf, var effectiveEvidence, var effectiveOutput])
{
    SupportEffectiveTierTests.Run(effectivePvf, effectiveEvidence, effectiveOutput);
    return;
}
if (args is ["--natural-tiers", var naturalPvf, var naturalEvidence, var naturalOutput])
{
    SupportNaturalTierTests.Run(naturalPvf, naturalEvidence, naturalOutput);
    return;
}
if (args is ["--auxiliary", var auxiliaryPvf, var auxiliaryOutput])
{
    SupportAuxiliaryTests.Run(auxiliaryPvf, auxiliaryOutput);
    return;
}
if (args is ["--paired-maps", var pairedPvf, var pairedEligibility, var pairedOutput])
{
    PairedCompanionMapTests.Run(pairedPvf, pairedEligibility, pairedOutput);
    return;
}
if (args is ["--behavior", var behaviorPvf, var behaviorOutput])
{
    SupportBehaviorTests.Run(behaviorPvf, behaviorOutput);
    return;
}
if (args is ["--female-beast", var pvf, var approval, var output])
{
    FemaleBeastMapTests.Run(pvf, approval, output);
    return;
}
if (args.Length != 2) throw new ArgumentException("Usage: SupportApcResourcePatch.Tests <cumulative-pvf> <new-output>");
int checks = 0;
void Check(bool value, string name) { checks++; if (!value) throw new InvalidOperationException(name); }
foreach (var definition in TemplateDelta.Definitions.Where(AiPrograms.HasBasicAttack))
{
    var programs = AiPrograms.Action(definition);
    var runtime = new DecisionReplay(programs, definition);
    Check(runtime.Run() == -1, "No enemy or missing support candidate must stay idle");
    runtime.Party = true;
    runtime.Skills.UnionWith(definition.Active);
    runtime.Buffs.UnionWith(definition.Active);
    runtime.Active.UnionWith(definition.Active.Where(skill => skill != 42));
    Check(runtime.Run() == -1, "No enemy must not trigger an offensive action");
    runtime.Skills.Clear();
    foreach (int rank in new[] { 0, 1, 2, 3 })
    {
        runtime.Enemy = new(50, 20, rank);
        Check(runtime.Run() == definition.Active.Length, "Normal/elite/named/boss close enemy must reach basic KEY");
        runtime.Enemy = new(4000, 800, rank);
        Check(runtime.Run() == -1, "Remote enemy must not cause melee attacks into empty space");
        int awakening = definition.Id == 20900 ? 84 : 26;
        runtime.Skills.Add(awakening);
        Check(runtime.Run() == Array.IndexOf(definition.Active, awakening), "Distant rank-independent enemy must reach awakening without old600/1200 gate");
        runtime.Skills.Clear();
    }
    runtime.Enemy = new(50, 20, 1, Hostile: false);
    Check(runtime.Run() == -1, "Friendly actor must not start combat");
    runtime.Enemy = new(50, 20, 2, Dead: true);
    Check(runtime.Run() == -1, "Dead actor must not start combat");
    runtime.Enemy = new(50, 20, 1, Type: 0x100);
    Check(runtime.Run() == -1, "Noncombat object must not start combat");
    runtime.Enemy = new(50, 20, 1);
    int buff = definition.Id == 20900 ? 45 : 30;
    runtime.Skills.Add(buff);
    runtime.Buffs.Remove(buff);
    Check(runtime.Run() == Array.IndexOf(definition.Active, buff), "Missing main BUFF takes priority over basic attack");
    runtime.Buffs.Add(buff);
    Check(runtime.Run() == definition.Active.Length, "Permanent main BUFF permits basic fallback without recast");
    if (definition.Id == 9986)
    {
        runtime.Skills.Add(12);
        Check(runtime.Run() != Array.IndexOf(definition.Active, 12), "Light skill12 must have no action branch");
        runtime.Active.Add(42);
        Check(runtime.Run() == -1, "Nen guard channel keeps its original action guard");
    }
    Check(AiPrograms.BasicAttack().Any(t => t.Text == "x") && !AiPrograms.BasicAttack().Any(t => t.Text?.StartsWith("skill:", StringComparison.Ordinal) == true), "Basic KEY uses native X input");
}
using var source = PvfArchive.Open(args[0]);
CombatDelta.Write(source, args[1]);
using var delta = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(args[1], "delta.json")));
int aics = 0;
var changedPaths = new HashSet<string>(StringComparer.Ordinal);
foreach (var entry in delta.RootElement.GetProperty("entries").EnumerateArray())
{
    string path = entry.GetProperty("path").GetString()!;
    Check(changedPaths.Add(path), "Unique delta path");
    Check(path.StartsWith("aicharacter/", StringComparison.Ordinal), "No map/skill/equipment or unrelated resource changes");
    if (!path.EndsWith(".aic", StringComparison.Ordinal)) continue;
    aics++;
    var after = JsonSerializer.Deserialize<List<ScriptToken>>(entry.GetProperty("tokens"))!;
    var before = TemplateDelta.Read(source, path);
    Check(OutsideKeys(before).SequenceEqual(OutsideKeys(after)), "Every non-KEY AIC token must remain byte-equivalent including all tiers/skills/equipment/vision/follow references");
    Check(after.Count(token => token.Text == "support_attack.key") == 1, "Exactly one basic KEY registered");
    Check(!entry.GetProperty("append").GetBoolean(), "Registered AIC must not be appended");
    Check(entry.GetProperty("expected_before_sha256").GetString() == MapMigration.Hash(source.ReadRaw(path)), "AIC baseline identity");
}
Check(aics == 34, "All34 registered support templates covered");
bool rejected = false;
try { CombatDelta.Write(source, args[1]); } catch (IOException) { rejected = true; }
Check(rejected, "Existing output cannot be overwritten");
Console.WriteLine($"PASS {checks} support combat checks; all34 AIC non-KEY tokens unchanged; source PVF read-only; native gameplay QA pending");
return;

static IEnumerable<ScriptToken> OutsideKeys(List<ScriptToken> tokens)
{
    bool skip = false;
    foreach (var token in tokens)
    {
        if (token.Kind == 3) skip = token.Text == "[key stream]";
        if (!skip) yield return token;
    }
}

internal sealed record Enemy(int X, int Y, int Rank, bool Hostile = true, bool Dead = false, int Type = 0x211);

// 解释实际生成的分支/导入及KEY下标；范围/敌对门禁按已核原生消费合同重放，不启动游戏。
internal sealed class DecisionReplay(Dictionary<string, List<ScriptToken>> programs, TemplateDefinition definition)
{
    public bool Party;
    public float Hp = 100;
    public Enemy? Enemy;
    public HashSet<int> Skills = [];
    public HashSet<int> Buffs = [];
    public HashSet<int> Active = [];
    float lastResult;
    public int Run() => Evaluate("action.ai", 0);
    int Evaluate(string name, int depth)
    {
        if (depth > 64) throw new InvalidDataException("Unbounded AI import chain");
        int offset = 1;
        return Parse(programs[name], ref offset).Run(this, depth);
    }
    abstract record Node { public abstract int Run(DecisionReplay state, int depth); }
    sealed record Result(int Value) : Node { public override int Run(DecisionReplay state, int depth) => Value; }
    sealed record Import(string Name) : Node { public override int Run(DecisionReplay state, int depth) => state.Evaluate(Name, depth + 1); }
    sealed record Branch(string Condition, ScriptToken[] Args, Node Yes, Node? No) : Node
    {
        public override int Run(DecisionReplay state, int depth)
        {
            bool value = state.Condition(Condition, Args);
            return No == null || value ? Yes.Run(state, depth) : No.Run(state, depth);
        }
    }
    static Node Parse(List<ScriptToken> tokens, ref int offset)
    {
        string tag = tokens[offset++].Text!;
        if (tag == "[return]") return new Result(tokens[offset++].Value);
        if (tag == "[import ai]") return new Import(tokens[offset++].Text!);
        if (tag != "[think]") throw new InvalidDataException("Unsupported generated instruction: " + tag);
        string condition = tokens[offset++].Text!;
        int first = offset;
        while (offset < tokens.Count && tokens[offset].Kind != 3) offset++;
        ScriptToken[] args = tokens[first..offset].ToArray();
        if (tokens[offset].Text == "[think]")
        {
            var body = Parse(tokens, ref offset);
            if (tokens[offset++].Text != "[/think]") throw new InvalidDataException("Malformed numeric condition");
            return new Branch(condition, args, body, null);
        }
        if (tokens[offset++].Text != "[true]") throw new InvalidDataException("Missing true branch");
        var yes = Parse(tokens, ref offset);
        if (tokens[offset++].Text != "[false]") throw new InvalidDataException("Missing false branch");
        var no = Parse(tokens, ref offset);
        if (tokens[offset++].Text != "[/think]") throw new InvalidDataException("Unclosed branch");
        return new Branch(condition, args, yes, no);
    }
    bool Condition(string condition, ScriptToken[] args)
    {
        float Number(int i) => args[i].Kind == 2 ? BitConverter.Int32BitsToSingle(args[i].Value) : args[i].Value;
        switch (condition)
        {
            case "[set party member]": return Party && args[0].Value == -1;
            case "[check use skill]": return Skills.Contains(args[0].Value);
            case "[check party buff on]": return Buffs.Contains(args[1].Value);
            case "[check skill on]": return Active.Contains(args[0].Value) && (args[0].Value != 42 || definition.Id == 9986);
            case "[party member hp]": return args[0].Text == "%<" && Hp < Number(1);
            case "[last result]": return args[0].Text == ">=" && lastResult >= Number(1);
            case "[void]":
                bool eligible = Enemy is { Hostile: true, Dead: false } && (Enemy.Type & 0x11) == 0x11;
                bool inside = eligible && Enemy!.X >= -Number(1) && Enemy.X <= Number(2) && Enemy.Y >= -Number(3) && Enemy.Y <= Number(4);
                if (args[0].Text == "enemy number of in attack area()") { lastResult = inside ? 1 : 0; return inside; }
                if (args[0].Text == "is target in attack area()") return inside;
                throw new InvalidDataException("Unsupported generated void");
            default: throw new InvalidDataException("Unsupported generated condition: " + condition);
        }
    }
}
