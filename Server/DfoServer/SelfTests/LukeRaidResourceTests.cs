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
        That(script.LukeCardMapRecords.SequenceEqual(Enumerable.Range(0, 5)
            .Select(index => new LukeCardMapRecord((byte)(index * 3), (byte)(index * 3), (byte)(index * 3)))),
            "十五个整数保留为五条三字节记录，不作迷宫索引或权重");
        That(LukeCardMapTable.Parse([new(0, 0), new(0, 127), new(0, 255)]) is [{ RawA: 0, RawB: 127, RawC: 255 }],
            "三列原值保序且不要求相等");
        That(LukeCardMapTable.Parse(Enumerable.Repeat(new PvfToken(0, 0), LukeCardMapTable.MaximumRecords * 3).ToArray()).Length ==
            LukeCardMapTable.MaximumRecords, "三字节记录资源预算上界");
        foreach (PvfToken[] malformed in new PvfToken[][]
        {
            [], [new(0, 1)], [new(0, 1), new(0, 2)], [new(0, -1), new(0, 0), new(0, 0)],
            [new(0, 256), new(0, 0), new(0, 0)], [new(2, 0), new(0, 0), new(0, 0)],
            Enumerable.Repeat(new PvfToken(0, 0), (LukeCardMapTable.MaximumRecords + 1) * 3).ToArray()
        })
        {
            bool rejected = false;
            try { LukeCardMapTable.Parse(malformed); } catch (InvalidDataException) { rejected = true; }
            That(rejected, "残缺/越界/非整数/超预算三字节记录拒绝，不沿客户端低字节截断");
        }
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
        int cardStart = Array.IndexOf(input, "[CARD SET MAP INFO]");
        int cardEnd = Array.IndexOf(input, "[/CARD SET MAP INFO]");
        var duplicateCards = input.Concat(input.Skip(cardStart).Take(cardEnd - cardStart + 1)).ToArray();
        var nestedCards = input.Take(cardStart + 1).Concat(new object[] { "[NESTED UNKNOWN]" })
            .Concat(input.Skip(cardStart + 1)).ToArray();
        var missingClose = input.Where((_, index) => index != cardEnd).ToArray();
        var twelveValues = input.Where((_, index) => index < cardStart + 1 || index > cardStart + 3).ToArray();
        That(Parse(duplicateCards).Issues.Any(issue => issue.Code == "duplicate-header"), "重复卡牌地图头拒绝");
        That(Parse(nestedCards).Issues.Any(issue => issue.Code == "header-block-incomplete"), "卡牌地图内嵌字段不作相邻合法块");
        That(Parse(missingClose).Issues.Any(issue => issue.Code == "header-block-incomplete"), "卡牌地图缺闭块拒绝");
        That(Parse(twelveValues).Issues.Any(issue => issue.Code == "unsupported-header"), "完整四条三元组仍不放宽当前十五整数兼容形状");
        foreach (var (values, code) in new (object[], string)[]
        {
            (Change("[CARD SET MAP INFO]", 1, 256), "invalid-card-map-records"),
            (Change("[CARD SET MAP INFO]", 2, -1), "invalid-card-map-records"),
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
        That(unresolved.Issues.Any(i => i.Code == "raid-timer-dungeon-reference-missing" && i.Detail == "999") &&
            unresolved.Phases.Single().Rules.Single().Behavior.Single().RawIntegers.SequenceEqual(new[] { 1, 999 }),
            "实例未登记DGN的timer保留原值与引用错误，不补造目标或SET");
        var absentReset = Parse([.. input[..(behavior + 1)], "[RESET TIMER]", 1, 5054, .. input[end..]]);
        That(absentReset.Issues.IsEmpty, "卢克登记图的缺键RESET是幂等取消，无需伪造SET");
        int trigger = Array.IndexOf(input, "[TRIGGER]"), triggerEnd = Array.IndexOf(input, "[/TRIGGER]");
        var absentCheck = Parse([.. input[..(trigger + 1)], "[CHECK TIMER END]", 1, 5054, .. input[triggerEnd..]]);
        That(absentCheck.Issues.Any(i => i.Code == "raid-timer-reference-missing" && i.Detail == "1:5054"),
            "等待到期必须有实例级SET声明，缺键RESET例外不扩展为吞掉所有引用问题");
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
        That(resources.Issues.IsEmpty && resources.ResourcesReady,
            "实例级timer(1,5054)与缺键RESET(1,5063)经明确验证合同成立，不删除Issues结果冒充支持：" + string.Join(';', resources.Issues));
        That(resources.StartBlocker == "formal-raid-flow-unavailable", "资源齐全不代表阶段消费者与正式生产策略已就绪");
        CheckRuleCapabilities(resources);
        Console.WriteLine($"PASS {Count - before} 卢克真实资源与实例级引用合同断言");
    }

    // 期望独立来自本版逐条审计；调用开战实际守卫，不复制一份允许列表作为测试实现。
    static void CheckRuleCapabilities(RaidResources resources)
    {
        // P1 13 / P2 27 原顺序；G只说明单条允许，不是完整原资源策略可运行。
        string[] expected = ["STGGTGGTGGGSG", "GGGSGGGGSSSSSGGGSGSSGSSGSSG"];
        var script = resources.Script;
        That(script.Phases.Select(phase => phase.Rules.Length).SequenceEqual(expected.Select(value => value.Length)),
            "规则能力矩阵与本版13/27条资源逐条对齐，资源变更不能静默复用旧结论");
        for (int phaseIndex = 0; phaseIndex < script.Phases.Length; phaseIndex++)
        {
            var phase = script.Phases[phaseIndex];
            var ids = phase.Dungeons.Select(dungeon => dungeon.Id).ToHashSet();
            var maps = resources.Dungeons.Where(dungeon => ids.Contains(dungeon.Id)).SelectMany(dungeon => dungeon.MapIds).ToHashSet();
            for (int index = 0; index < phase.Rules.Length; index++)
            {
                var rule = phase.Rules[index];
                string? reason = LukeRaidStart.RuleRejection(rule, script, script, ids, maps, timersEnabled: true);
                string? wanted = expected[phaseIndex][index] switch
                {
                    'S' => "luke-execution-symbol-consumer-unavailable",
                    'T' => "luke-execution-policy-rule-unsupported",
                    _ => null
                };
                string identity = $"P{phaseIndex + 1}-{index + 1} token={rule.Trigger[0].TokenIndex}";
                That(reason == wanted, "原资源单条能力 " + identity + ": " + (reason ?? "clause-supported") + "，不是完整策略开放");
                That(rule.Trigger.All(clause => clause.TokenIndex >= 0) && rule.Behavior.All(clause => clause.TokenIndex >= 0),
                    "所有触发/动作保留原token定位：" + identity);
            }
        }
    }

}
