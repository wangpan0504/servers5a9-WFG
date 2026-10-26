using DfoServer.Game.Raids;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Raids;
using DfoServer.Testing;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class AntonRaidResourceTests
{
    public static void RunCore()
    {
        int before = Count;
        var strings = new List<string>();
        PvfToken[] Tokens(object[] values) => values.Select(value =>
        {
            if (value is int number) return new PvfToken(0, number);
            string text = (string)value;
            strings.Add(text);
            return new PvfToken(text.StartsWith('[') ? (byte)3 : (byte)6, strings.Count - 1);
        }).ToArray();
        string Resolve(int id) => strings[id];
        object[] symbols = [1, "[HP A]", 2, "[HP B]", 3, "[FLAG]"];
        object[] Phase(int id, string hp) =>
        [
            "[PHASE]", "[PHASE NAME]", "测试阶段", "[TITLE IMG]", "test.img", 0,
            "[MINIMAL DUNGEON CLEAR COUNT]", 1,
            "[CLEAR REWARD ITEM]", "[STATE REWARD]", "squad_item", -1, 665, 100, 1, "[/CLEAR REWARD ITEM]",
            "[DUNGEON INFO]", "[INDEX]", id, "[INIT STATE]", "open", "[HP INFO]", hp, 1, "[PARTY MAX]", 5, "[/DUNGEON INFO]",
            "[TRIGGER]", "[ON CHANGE DUNGEON STATE]", id, "[CHECK DUNGEON STATE]", id, "clear", "[/TRIGGER]",
            "[BEHAVIOR]", "[SET TIMER]", 1, id, 45, "[SET]", "[FLAG]", 1, "[CLEAR PHASE]", "[/BEHAVIOR]",
            "[TRIGGER]", "[CHECK TIMER END]", 1, id, "[/TRIGGER]", "[BEHAVIOR]", "[RESET TIMER]", 1, id, "[/BEHAVIOR]", "[/PHASE]"
        ];
        object[] input = ["[PHASE MAX]", 2, "[RAID MEMBER MAX]", 20, "[RAID START MINIMUM MEMBER]", 10,
            "[WAITING ROOM]", 19, 2, "[ENTRY COST]", 100, 1, "[/ENTRY COST]", "[RETRY ITEM]", 100, 1,
            "[HATCHERY INFO]", 4, 3, 210, 210, 218, 218, "[/HATCHERY INFO]", "[EXCEPT CHEAT DUNGEON]", 218, "[/EXCEPT CHEAT DUNGEON]",
            .. Phase(210, "[HP A]"), .. Phase(218, "[HP B]")];
        AntonRaidScript Parse(object[] values, object[]? names = null) =>
            AntonRaidScriptParser.Parse("Etc/Raid/Anton.etc", Tokens(values), Tokens(names ?? symbols), Resolve);
        var script = Parse(input);
        That(script.Issues.IsEmpty && script.WaitingRoom == new AntonRaidWaitingRoom(19, 2) && script.EntryCost == new AntonRaidCost(100, 1),
            "最小两阶段只读定义及候战室/费用声明完整解析");
        That(script.Phases.Select(p => p.Dungeons.Single().Id).SequenceEqual(new[] { 210, 218 }) &&
            script.Phases[0].Rewards.Single() == new AntonRaidReward("squad_item", -1, 665, 100, 1), "阶段隔离及奖励未知整数原序保留");
        That(new[] { AntonDungeonState.Open, AntonDungeonState.Close, AntonDungeonState.Hide, AntonDungeonState.Clear }
            .Select(s => (byte)s).SequenceEqual(new byte[] { 0, 1, 2, 3 }), "有证地下城状态0/1/2/3，不借用团状态");

        object[] Change(string tag, int offset, object value)
        {
            var changed = (object[])input.Clone();
            changed[Array.IndexOf(changed, tag) + offset] = value;
            return changed;
        }
        foreach (var (value, state) in new[] { ("open", AntonDungeonState.Open), ("close", AntonDungeonState.Close),
            ("hide", AntonDungeonState.Hide), ("clear", AntonDungeonState.Clear) })
            That(Parse(Change("[INIT STATE]", 1, value)).Phases[0].Dungeons[0].InitialState == state, "四种有证状态按字符串解析：" + value);
        int triggerAt = Array.IndexOf(input, "[TRIGGER]"), triggerEnd = Array.IndexOf(input, "[/TRIGGER]");
        int dungeonAt = Array.IndexOf(input, "[DUNGEON INFO]"), dungeonEnd = Array.IndexOf(input, "[/DUNGEON INFO]");
        foreach (var (values, code) in new (object[], string)[]
        {
            (Change("[PHASE MAX]", 1, 3), "phase-count-mismatch"),
            (Change("[WAITING ROOM]", 2, "invalid"), "waiting-room-invalid"),
            (Change("[WAITING ROOM]", 0, "[MISSING WAITING ROOM]"), "waiting-room-invalid"),
            (Change("[RAID START MINIMUM MEMBER]", 1, 21), "member-limits-invalid"),
            (Change("[ENTRY COST]", 2, 0), "cost-declaration-invalid"),
            (Change("[INIT STATE]", 1, "unknown"), "invalid-dungeon-state"),
            (Change("[CHECK DUNGEON STATE]", 1, 218), "phase-dungeon-reference-missing"),
            (Change("[CHECK TIMER END]", 1, 9), "phase-timer-reference-missing"),
            (Change("[CLEAR PHASE]", 0, "[FUTURE RULE]"), "unsupported-clause"),
            ([.. input, "[WAITING ROOM]", 19, 2], "duplicate-header"),
            (input.Where(v => !Equals(v, "[/PHASE]")).ToArray(), "block-incomplete"),
            (Change("[PHASE NAME]", 0, "[UNKNOWN PHASE FIELD]"), "unsupported-phase-field"),
            (Change("[HATCHERY INFO]", 3, 999), "header-dungeon-reference-missing"),
            ([.. input[..(triggerAt + 1)], .. input[triggerEnd..]], "empty-rule-block"),
            ([.. input[..dungeonAt], .. input[dungeonAt..(dungeonEnd + 1)], .. input[dungeonAt..]], "duplicate-phase-dungeon")
        })
        {
            var invalid = Parse(values);
            That(invalid.Issues.Any(i => i.Code == code && i.Path == "etc/raid/anton.etc" && i.TokenIndex >= 0),
                "残缺/未知语法显式拒绝并保留来源：" + code);
        }
        That(Parse(input, symbols[..^2]).Issues.Any(i => i.Code == "symbol-reference-missing"), "缺符号不能静默跳过");
        That(Parse(input, [.. symbols, 4, "[FLAG]"]).Issues.Any(i => i.Code == "duplicate-or-invalid-symbol"), "重复符号名拒绝");
        That(Parse(input, [.. symbols, 1, "[OTHER]"]).Issues.Any(i => i.Code == "duplicate-or-invalid-symbol"), "冲突符号编号拒绝");

        var directory = DungeonCatalog.ParseDirectory(Tokens([210, "Anton/One.DGN", 218, "ANTON/TWO.dgn"]), Resolve, "dungeon");
        That(directory[210] == "dungeon/anton/one.dgn", "备用列表按Dungeon根归一化大小写，不能加tutorial路径前缀");
        Reject<InvalidDataException>(() => DungeonCatalog.ParseDirectory(Tokens([210, "a.dgn", 210, "b.dgn"]), Resolve, "dungeon"), "目录冲突键拒绝");
        Reject<InvalidDataException>(() => DungeonCatalog.ParseDirectory(Tokens([210, "a.dgn", 210, "A.DGN"]), Resolve, "dungeon"), "相同重复键也不覆盖");
        PvfToken[] mapTokens = Tokens(["[map specification]", "map", 0, 0, 32200, "boss", 1, 0, 32202, "[/map specification]"]);
        var resources = AntonRaidResourceLoader.Check(script, directory, _ => null, _ => null, _ => true, _ => mapTokens, Resolve, _ => true);
        That(resources.MissingFormalDungeons.SequenceEqual(new[] { 210, 218 }) && resources.MissingFormalMaps.SequenceEqual(new[] { 32200, 32202 }) &&
            resources.Dungeons.All(d => d.TutorialFileExists && d.FormalPath is null && d.ReferencePath == d.TutorialPath) &&
            resources.StartBlocker == "formal-raid-dungeon-index-missing", "备用脚本及显式MAP引用存在仍不充当主表登记");
        var complete = AntonRaidResourceLoader.Check(script, directory, id => directory[id], _ => "map/test.map", _ => true, _ => mapTokens, Resolve, _ => true);
        That(complete.ResourcesReady && complete.StartBlocker == "formal-raid-flow-unavailable", "资源齐全也不能授权开战成功");
        var missing = AntonRaidResourceLoader.Check(script, directory, _ => null, _ => null, _ => false, _ => throw new Exception("不应读取缺文件"), Resolve, _ => false);
        That(missing.Issues.Any(i => i.Code == "tutorial-dungeon-file-missing") && missing.MissingItems.SequenceEqual(new[] { 100 }), "缺文件与费用/奖励物品缺项有独立诊断");
        List<AntonRaidIssue> mapIssues = [];
        AntonRaidResourceLoader.MapReferences("dungeon/a.dgn", Tokens(["[map specification]", "random", 0, 0, 32200, "[/map specification]"]), Resolve, mapIssues);
        That(mapIssues.Any(i => i.Code == "unsupported-map-reference"), "未知地图语法不从数字或文件名猜测登记");
        var definition = new RaidDefinition(0, script.Path, 20, 10, 2) { Anton = resources };
        That(RaidStartCheck.Evaluate(false, 9, definition) is { Error: 8, Reason: "not-leader" } &&
            RaidStartCheck.Evaluate(true, 9, definition) is { Error: 22, Reason: "too-few-members" } &&
            RaidStartCheck.Evaluate(true, 10, definition) is { Error: 19, Reason: "formal-raid-dungeon-index-missing" }, "8→22→19优先级不被资源检查覆盖");
        Console.WriteLine($"PASS {Count - before} 安徒恩只读资源解析及开战就绪边界断言");
    }

    public static void RunResources(PvfArchive pvf, RaidCatalog catalog, ItemCatalog items)
    {
        int before = Count;
        var dungeons = SelfTestCatalogs.Dungeons(pvf);
        var definition = catalog.PrepareAnton(pvf, dungeons, items).Find(0)!;
        var resources = definition.Anton!;
        var script = resources.Script;
        That(resources.Issues.IsEmpty, "真实安徒恩资源语法完整支持：" + string.Join(';', resources.Issues.Select(i => $"{i.Code}@{i.TokenIndex}:{i.Detail}")));
        That(script.WaitingRoom == new AntonRaidWaitingRoom(19, 2) && script.EntryCost == new AntonRaidCost(10096296, 1) &&
            script.RetryItem == script.EntryCost, "真实候战室与费用/重试声明，不表示已扣票");
        That(script.Phases.Length == 2 && script.Symbols.Count == 41 && script.Phases.Select(p => p.Dungeons.Length).SequenceEqual(new[] { 7, 7 }) &&
            script.Phases.Select(p => p.Rules.Length).SequenceEqual(new[] { 28, 23 }), "真实两阶段14地下城51规则及41符号完整保序");
        That(script.Phases.Select(p => p.Rewards.Length).SequenceEqual(new[] { 6, 8 }) &&
            script.Phases[0].Rewards[0] == new AntonRaidReward("party_card", -1, 100, 10304474, 0) &&
            script.Phases[1].Rewards[2] == new AntonRaidReward("squad_item", -1, 665, 10304480, 0) && resources.MissingItems.IsEmpty,
            "真实14奖励声明的kind和整数原序及物品引用，不解释抽样和发放");
        That(resources.MissingFormalDungeons.SequenceEqual(new[] { 210, 211, 212, 213, 214, 215, 216, 218, 219, 220, 221, 222, 223, 224 }) &&
            resources.MissingFormalMaps.Length == 72 && resources.Dungeons.All(d => d.TutorialFileExists && !d.FormalFileExists) &&
            resources.Dungeons[0].MapIds.SequenceEqual(new[] { 32200, 32202, 32201, 32204 }) && !dungeons.ContainsDungeon(210),
            "真实主表缺14DGN/72MAP、备用脚本存在，检查不污染正式目录");
        var legs = script.Phases[0].Rules.Where(r => r.Trigger.Count(c => c.State == AntonDungeonState.Clear && c.DungeonId is 213 or 215) == 2).ToArray();
        That(legs.Length == 2 && legs.All(r => r.Behavior.Any(c => c.Symbol == "[1phase success]" && c.RawIntegers.SequenceEqual(new[] { 1 }))) &&
            script.Phases[0].Rules.Any(r => r.Trigger.Any(c => c.Symbol == "[1phase success]") && r.Behavior.Any(c => c.Tag == "[clear phase]")),
            "双腿clear经成功符号结束阶段的声明，不声称已运行");
        That(script.Phases[1].Rules.Any(r => r.Trigger.Any(c => c.DungeonId == 220 && c.State == AntonDungeonState.Clear) &&
            r.Behavior.Any(c => c.Tag == "[clear phase]")) &&
            script.Phases[1].Rules.Any(r => r.Behavior.Any(c => c.Timer == new AntonRaidTimer(3, 219) && c.RawIntegers.SequenceEqual(new[] { 3, 219, 180 }))),
            "心脏clear及屏障定时器原始声明，不推断时间单位");
        Console.WriteLine($"PASS {Count - before} 安徒恩真实资源就绪与阶段声明断言");
    }
}
