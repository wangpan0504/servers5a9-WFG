using DfoServer.Game.Raids;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Raids;
using PvfLib;
using DfoServer.Testing;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class LukeRaidResourceTests
{
    public static void RunCore()
    {
        int before = Count;
        List<string> strings = [];
        PvfToken[] Tokens(object[] values) => values.Select(value =>
        {
            if (value is int number) return new PvfToken(0, number);
            string text = (string)value; strings.Add(text);
            return new PvfToken(text.StartsWith('[') ? (byte)3 : (byte)6, strings.Count - 1);
        }).ToArray();
        object[] symbols = [1, "[HP]", 2, "[OBJECT]", 3, "[COUNT]"];
        object[] input = ["[PHASE MAX]", 1, "[RAID MEMBER MAX]", 8, "[RAID START MINIMUM MEMBER]", 1,
            "[WAITING ROOM]", 32, 4, "[ENTRY COST]", 100, 1, "[/ENTRY COST]", "[RETRY ITEM]", 100, 1,
            "[CARD SET MAP INFO]", 0, 0, 0, 3, 3, 3, 6, 6, 6, 9, 9, 9, 12, 12, 12, "[/CARD SET MAP INFO]",
            "[1PHASE OBJ DESTROY]", 0, 0, 5, 1, 6, 10, 2, 11, 15, "[/1PHASE OBJ DESTROY]",
            "[DEFAULT OBJECT COUNT]", "[BIRTH SANCTUM]", 5, 5054, "[EXTINCTION SANCTUM]", 5, 5054,
            "[SPREAD SANCTUM]", 5, 5054, "[/DEFAULT OBJECT COUNT]",
            "[FORCE CHANGE DIFFICULTY DUNGEON LIST]", "[/FORCE CHANGE DIFFICULTY DUNGEON LIST]",
            "[PHASE]", "[PHASE NAME]", "测试阶段", "[PHASE COIN LIMIT]", 20,
            "[CLEAR REWARD ITEM]", "[STATE REWARD]", "pcroom_card_a", -1, 100, 100, 0, "[/CLEAR REWARD ITEM]",
            "[DUNGEON INFO]", "[INDEX]", 5054, "[INIT STATE]", "open", "[HP INFO]", "[HP]", 3,
            "[PARTY MAX]", 1, "[LOCKOUT TYPE]", 0, 0, "[DESTROY OBJECT INFO]", "[OBJECT]", "[/DUNGEON INFO]",
            "[TRIGGER]", "[ON ENTER MAP]", 55845, "[/TRIGGER]",
            "[BEHAVIOR]", "[SET]", "[COUNT]", "[+]", 1, "[FAIL PHASE]", "[/BEHAVIOR]", "[/PHASE]"];
        RaidScript Parse(object[] values, RaidScriptKind kind = RaidScriptKind.Luke) =>
            RaidScriptParser.Parse("etc/raid/luke/luke.etc", Tokens(values), Tokens(symbols), id => strings[id], kind);
        var script = Parse(input);
        That(script.Issues.IsEmpty, "卢克最小资源声明完整：" + string.Join(';', script.Issues));
        var phase = script.Phases.Single(); var rule = phase.Rules.Single();
        That(script.WaitingRoom == new RaidWaitingRoom(32, 4) && phase.Dungeons.Single().HpRaw == 3 &&
            phase.Dungeons.Single().Declarations.Any(f => f.Tag == "[lockout type]" && f.Values.Length == 2),
            "候战室、初始HP和锁定类型只冻结资源声明");
        That(rule.Trigger.Single().MapId == 55845 && rule.Behavior[0] is { Symbol: "[count]", Operator: "[+]" } &&
            rule.Behavior[0].RawIntegers.SequenceEqual(new[] { 1 }) && phase.Rewards.Single().Kind == "pcroom_card_a",
            "地图触发、符号加法与独立奖励种类保序，不解释运行语义");
        That(Parse(input, RaidScriptKind.Anton).Issues.Any(i => i.Code == "dungeon-declaration-invalid") &&
            Parse(input, RaidScriptKind.Anton).Issues.Any(i => i.Code == "unsupported-reward"), "卢克专属语法不扩大安徒恩接受范围");
        object[] Change(string tag, int offset, object value)
        {
            var copy = (object[])input.Clone(); copy[Array.IndexOf(copy, tag) + offset] = value; return copy;
        }
        foreach (var (values, code) in new (object[], string)[]
        {
            (Change("[LOCKOUT TYPE]", 1, "unknown"), "dungeon-declaration-invalid"),
            (Change("[OBJECT]", 0, "[MISSING SYMBOL]"), "symbol-reference-missing"),
            (Change("[+]", 1, "unknown"), "unsupported-clause"),
            (Change("[ON ENTER MAP]", 1, -1), "unsupported-clause"),
            (Change("[BIRTH SANCTUM]", 2, 999), "header-dungeon-reference-missing"),
            (Change("[/DEFAULT OBJECT COUNT]", 0, "[UNKNOWN END]"), "header-block-incomplete"),
            (Change("[PHASE COIN LIMIT]", 1, -1), "phase-clear-count-invalid"),
            (Change("[STATE REWARD]", 1, "unknown"), "unsupported-reward"),
            (Change("[FAIL PHASE]", 0, "[OPEN RANDOM HATCHERY]"), "unsupported-clause"),
            ([.. input, "[HATCHERY INFO]", 4, 3, 5054, 5054, 5054, 5054, "[/HATCHERY INFO]"], "unsupported-header"),
            ([.. input, "[WEEKLY CLEAR COUNT]", 2, "[WEEKLY CLEAR COUNT]", 3], "duplicate-header")
        }) That(Parse(values).Issues.Any(i => i.Code == code && i.TokenIndex >= 0), "卢克未知/损坏声明保留来源并拒绝：" + code);
        int behavior = Array.IndexOf(input, "[BEHAVIOR]"), end = Array.IndexOf(input, "[/BEHAVIOR]");
        var unresolved = Parse([.. input[..(behavior + 1)], "[RESET TIMER]", 1, 999, .. input[end..]]);
        That(unresolved.Issues.Any(i => i.Code == "phase-dungeon-reference-missing" && i.Detail == "999") &&
            unresolved.Issues.Any(i => i.Code == "phase-timer-reference-missing" && i.Detail == "1:999") &&
            unresolved.Phases.Single().Rules.Single().Behavior.Single().RawIntegers.SequenceEqual(new[] { 1, 999 }),
            "未闭合timer作用域保留原值和问题，不吞掉引用或补造SET");
        var resource = RaidResourceLoader.Check(script, new Dictionary<int, string>(), _ => "dungeon/test.dgn", _ => "map/test.map",
            _ => true, _ => Tokens(["[map specification]", "boss", 0, 0, 55845, "[/map specification]"]), id => strings[id], _ => true);
        That(resource.ResourcesReady && RaidStartCheck.Evaluate(true, 8,
            new(1, script.Path, 8, 1, 1) { Resources = resource }) is { Error: 19, Reason: "formal-raid-flow-unavailable" },
            "卢克资源齐全仍不能越过正式开战流程guard");
        var missingItem = RaidResourceLoader.Check(script, new Dictionary<int, string>(), _ => "dungeon/test.dgn", _ => "map/test.map",
            _ => true, _ => Tokens(["[map specification]", "boss", 0, 0, 55845, "[/map specification]", "[name]", "测试图"]), id => strings[id], _ => false);
        That(missingItem.MissingItems.SequenceEqual(new[] { 100 }) && missingItem.StartBlocker == "formal-raid-item-resource-missing" &&
            resource.ResourcesReady && resource.MissingItems.IsEmpty, "费用/奖励缺物品诊断只属于本次装配，不污染其它只读结果");
        Console.WriteLine($"PASS {Count - before} 卢克只读资源合同及隔离断言");
    }

    public static void RunResources(PvfArchive pvf, RaidCatalog catalog, ItemCatalog items)
    {
        int before = Count;
        var resources = catalog.PrepareResources(pvf, SelfTestCatalogs.Dungeons(pvf), items).Find(1)!.Resources!;
        var script = resources.Script;
        That(script.WaitingRoom == new RaidWaitingRoom(32, 4) && script.EntryCost == new RaidCost(10159649, 1) &&
            script.RetryItem == script.EntryCost, "真实卢克候战室32/4与黑白的守护声明，非扣票许可");
        That(script.Symbols.Count == 34 && script.Phases.Select(p => p.Dungeons.Length).SequenceEqual(new[] { 3, 12 }) &&
            script.Phases.Select(p => p.Rules.Length).SequenceEqual(new[] { 13, 27 }) &&
            script.Phases.Select(p => p.Rewards.Length).SequenceEqual(new[] { 4, 12 }), "两阶段15图40规则34符号16奖励完整保序");
        That(script.Headers.Single(f => f.Tag == "[phase time over]").Values.Select(v => v.Number).SequenceEqual(new[] { 2100, 2400 }) &&
            script.Headers.Single(f => f.Tag == "[weekly clear count]").Values.Single().Number == 2 &&
            script.Phases[1].Declarations.Single(f => f.Tag == "[phase coin limit]").Values.Single().Number == 20,
            "当前资源阶段时长原值2100/2400、周清2和二阶段币20，不套历史35/35或每日讨伐战");
        That(resources.MissingFormalDungeons.IsEmpty && resources.MissingFormalMaps.IsEmpty && resources.MissingItems.IsEmpty &&
            resources.Dungeons.Length == 15 && resources.Dungeons.All(d => d.FormalFileExists) &&
            resources.Dungeons.SelectMany(d => d.MapIds).Distinct().Count() == 141,
            "主目录完整登记15地下城及141张显式地图，物品齐全，不借普通卢克5101系列");
        That(resources.Issues.Length == 3 && resources.Issues.Count(i => i.Code == "phase-dungeon-reference-missing" && i.Detail == "5054") == 2 &&
            resources.Issues.Count(i => i.Code == "phase-timer-reference-missing" && i.Detail == "1:5063") == 1,
            "跨阶段timer(1,5054)及无同阶段set的reset(1,5063)保留未闭合诊断：" + string.Join(';', resources.Issues));
        That(resources.StartBlocker == "formal-raid-definition-unsupported", "真实卢克拒绝原因来自精确声明问题，而非未装配");
        Console.WriteLine($"PASS {Count - before} 卢克真实资源与未闭合引用断言");
    }
}
