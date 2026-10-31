using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Raids;
using DfoServer.Testing;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class FiendwarResourceTests
{
    internal static void RunCore()
    {
        int before = Count;
        List<string> strings = [];
        PvfToken[] Tokens(object[] source) => source.Select(value =>
        {
            if (value is int n) return new PvfToken(0, n);
            string text = (string)value;
            strings.Add(text);
            return new PvfToken(text.StartsWith('[') ? (byte)3 : (byte)6, strings.Count - 1);
        }).ToArray();
        object[] source = ["[PHASE MAX]", 1, "[FIENDWAR MEMBER MAX]", 8, "[FIENDWAR START MINIMUM MEMBER]", 1,
            "[WAITING ROOM]", 57, 2, "[ENTRY COST]", 10306760, 1, "[/ENTRY COST]", "[RETRY ITEM]", 10306760, 1,
            "[PHASE TIME OVER]", 3600, "[DISCONN MEMBER WAIT TIME]", 300, "[DAILY ACCOUNT ENTER LIMIT]", 10,
            "[WEEKLY CLEAR COUNT]", 2, "[RADAR SKILL ATTACK BONUS BUFF RATE]", 20, "[BATTERY DEFENCE DEBUFF RATE]", 20,
            "[GUIDE INFO]", 2, 6, 0, 33, 34, 0, "[/GUIDE INFO]", "[ENEMY CREATE RATE]", 1, 10000, "[/ENEMY CREATE RATE]",
            "[MERCHANT CREATE RATE]", 0, 60, 10000, "[/MERCHANT CREATE RATE]", "[MERCHANT CREATE POS]", 6, 100, 200, "[/MERCHANT CREATE POS]",
            "[PHASE]", "[PHASE NAME]", "漩涡", "[MINIMAL DUNGEON CLEAR COUNT]", 1,
            "[AREA INFO]", "[AREA INDEX]", 2, "[WORLDMAP INDEX]", -1, "[DUNGEON INDEX]", -1,
            "[INIT STATE]", "[2 AREA STATE]", "empty", "[MOVE AVAILABLE MAP]", 6, "[/MOVE AVAILABLE MAP]", "[MEMBER MAX]", 8, "[/AREA INFO]",
            "[AREA INFO]", "[AREA INDEX]", 6, "[WORLDMAP INDEX]", 51, "[DUNGEON INDEX]", 100, "[DUNGEON LIMIT TIME]", 210,
            "[INIT STATE]", "[6 AREA STATE]", "empty", "[MOVE AVAILABLE MAP]", 2, "[/MOVE AVAILABLE MAP]", "[MEMBER MAX]", 4, "[/AREA INFO]",
            "[CLEAR REWARD ITEM]", "[STATE REWARD]", "squad_item", -1, 10000, 10306753, 0, "[/CLEAR REWARD ITEM]",
            "[TRIGGER]", "[IF]", "[FIENDWAR INIT]", 0, "[/TRIGGER]",
            "[BEHAVIOR]", "[SET]", "[FIENDWAR INIT]", 1, "[SET TIMER]", "create_monster", 30, 1, "[/BEHAVIOR]",
            "[TRIGGER]", "[IF]", "[ALIVE MONSTER COUNT]", "[<]", 3, "[/TRIGGER]",
            "[BEHAVIOR]", "[CREATE MONSTERS]", "[/BEHAVIOR]", "[/PHASE]",
            "[SINGLE RAID PHASE]", "[AREA INFO]", "[AREA INDEX]", 33, "[DUNGEON INDEX]", 999, "[/AREA INFO]", "[/SINGLE RAID PHASE]"];
        FiendwarScript Parse(object[] values, object[]? symbols = null) => FiendwarScript.Parse("etc/raid/fiendwar_kr/fiendwar.etc",
            Tokens(values), Tokens(symbols ?? [1, "[FIENDWAR INIT]"]), id => strings[id]);
        var parsed = Parse(source);
        That(parsed.Script.Issues.IsEmpty, "漩涡最小声明合法：" + string.Join(';', parsed.Script.Issues));
        That(parsed.Areas.Keys.Order().SequenceEqual(new[] { 2, 6 }) && parsed.Script.Phases.Single().Dungeons.Single().Id == 100,
            "完整团队区域形成DGN依赖，单人支线不混入");
        That(parsed.Guides is [{ From: 2, To: 6 }] && parsed.Rules.Length == 2 &&
            parsed.Rules[0].Actions[1] is { Op: FiendwarOp.SetTimer, Key: "create_monster", A: 30, B: 1 } &&
            parsed.Rules[1].Conditions is [{ Op: FiendwarOp.AliveMonstersLess, A: 3 }], "字符串timer与存活数量比较按原语法编译");
        object[] Change(string tag, int offset, object value)
        {
            var changed = (object[])source.Clone();
            changed[Array.IndexOf(changed, tag) + offset] = value;
            return changed;
        }
        foreach (var (values, code) in new (object[], string)[]
        {
            (Change("[FIENDWAR MEMBER MAX]", 1, 9), "member-limits-invalid"),
            (Change("[WAITING ROOM]", 2, "bad"), "waiting-room-invalid"),
            (Change("[ENTRY COST]", 2, 0), "cost-declaration-invalid"),
            (Change("[MOVE AVAILABLE MAP]", 1, 999), "area-reference-missing"),
            (Change("[DUNGEON LIMIT TIME]", 1, 0), "invalid-area-limits"),
            (Change("[SET TIMER]", 1, 1), "unsupported-clause"),
            (Change("[CREATE MONSTERS]", 0, "[UNKNOWN ACTION]"), "unsupported-clause"),
            (source.Where(value => !Equals(value, "[/PHASE]")).ToArray(), "block-incomplete")
        }) That(Parse(values).Script.Issues.Any(issue => issue.Code == code), "损坏资源明确拒绝：" + code);
        That(Parse(source, [1, "[FIENDWAR INIT]", 1, "[OTHER]"]).Script.Issues.Any(issue => issue.Code == "duplicate-symbol"),
            "符号ID重复不得覆盖");
        That(Parse(source, [1, "[OTHER]"]).Script.Issues.Any(issue => issue.Code == "unsupported-clause"),
            "规则引用缺失符号不得默认成0");
        Console.WriteLine($"PASS {Count - before} 漩涡资源解析断言");
    }

    internal static void RunResources(PvfArchive pvf, RaidCatalog catalog, ItemCatalog items)
    {
        int before = Count;
        var definition = catalog.Find(2)!;
        var resources = RaidResourceLoader.Load(pvf, definition, SelfTestCatalogs.Dungeons(pvf), items);
        var script = resources.Fiendwar!;
        That(resources.ResourcesReady, "漩涡完整资源：" + string.Join(';', resources.Issues) +
            ";dgn=" + string.Join(',', resources.MissingFormalDungeons) + ";map=" + string.Join(',', resources.MissingFormalMaps) +
            ";item=" + string.Join(',', resources.MissingItems));
        That(script.Areas.Count == 30 && script.Areas.Keys.All(id => id is >= 2 and <= 31) && resources.Dungeons.Length == 26,
            "团队30区域含26地下城，不混入33起的官方引导支线");
        That(resources.EntryCatalog is { Ready: true } entry && resources.Dungeons.All(row => entry.Find(row.Id) is not null),
            "全部团队地图沿真实目录和raid标记装配");
        That(script.Rules.Length == 141 && script.Rules.All(row => !row.Conditions.IsEmpty && !row.Actions.IsEmpty),
            "141条团队规则全部编译，未把五条引导规则套入");
        That(script.TotalSeconds == 3600 && script.DisconnectSeconds == 300 && script.MemberMax == 8 && script.MinimumMembers == 1,
            "团队60分钟、5分钟重连、1至8人来自本地声明");
        That(script.Script.EntryCost == new RaidCost(10306760, 1) && script.Script.Phases[0].Rewards.Where(r => r.Kind == "squad_item").Sum(r => r.RawB) == 10000,
            "原门票与完整奖励权重，不改变概率");
        Console.WriteLine($"PASS {Count - before} 漩涡真实资源断言");
        FiendwarBattlefieldTests.RunResources(script);
        FiendwarGuideTests.RunResources(resources);
    }
}
