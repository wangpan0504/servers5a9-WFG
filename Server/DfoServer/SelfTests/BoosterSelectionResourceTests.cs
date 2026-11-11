using DfoServer.GameWorld.Items;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

// 英雄之证装备自选礼盒（490010644 与活动版 490010610）：[booster selection]，6 个分类、选择数量 1。
// 用来锁住 CMD160 选择型礼包的真实 PVF 结构，避免分类、奖励行或品级声明被解析规则误丢。
internal static class BoosterSelectionResourceTests
{
    const int Box = 490010644, EventBox = 490010610;

    public static void RunCore()
    {
        int checks = Count;
        const string typo = "[stackable type]\n`[booster selection]` 0\n[booster selection num]\n1\n" +
            "[booster select category]\n0 0\n[stackble]\n490004325 1 490004326 1 490004327 1 490004328 1\n[/stackble]\n" +
            "[/booster select category]\n";
        var definition = BoosterCatalog.Parse(typo);
        That(definition.Available && definition.Selection is { SelectionCount: 1, Categories.Length: 1 } category &&
            category.Categories[0].PendingRule is null &&
            category.Categories[0].Rewards.SequenceEqual(new BoosterChoice[]
                { new(490004325, 1), new(490004326, 1), new(490004327, 1), new(490004328, 1) }),
            "[stackble]声明保留单选上限和完整四个候选，不变更奖励类型和数量");
        const string ordinaryType = "[stackable type]\n`[booster]` 0\n";
        var overridden = BoosterCatalog.Parse(ordinaryType + typo);
        That(overridden.Available && overridden.Selection is { SelectionCount: 1, Categories.Length: 1 } &&
            overridden.Selection.Categories[0].Rewards.SequenceEqual(definition.Selection!.Categories[0].Rewards),
            "前置普通礼盒类型由后置自选类型覆盖，完整保留候选数量");
        That(!BoosterCatalog.Parse(typo + ordinaryType).Available,
            "末条普通类型生效，不能反向寻找旧自选类型兜底开盒");
        foreach (string badType in new[] { "", "`[booster]`", "`[booster]` nope", "`[booster]` 2147483648",
            "`[booster]` 0 1 2", "`[booster]` 0 extra" })
            That(!BoosterCatalog.Parse("[stackable type]\n" + badType + "\n" + typo).Available,
                "不以末条有效类型覆盖残缺、溢出或非法前声明");
        That(!BoosterCatalog.Parse("[outer]\n" + ordinaryType + "[/outer]\n" + typo).Available &&
            !BoosterCatalog.Parse(ordinaryType + "[outer]\n" + typo + "[/outer]\n").Available,
            "嵌套类型不能按根级顺序覆盖");
        foreach (string bad in new[]
        {
            typo.Replace("[/stackble]", "[/stackable]"), typo.Replace("[/stackble]", ""),
            typo.Replace("490004328 1", "490004328"), typo.Replace("490004328 1", "490004328 0"),
            typo.Replace("stackble", "unrecognized")
        })
            That(BoosterCatalog.Parse(bad).Selection?.Categories[0].PendingRule is not null,
                "未知标签、错配闭合、残缺或非法数量仍拒绝，不截断分类发奖");
        Console.WriteLine($"PASS {Count - checks} stackble选择分类解析断言");
    }

    public static void Run(PvfArchive pvf)
    {
        int checks = Count;
        var catalog = new BoosterCatalog(pvf, PvfScript.List(pvf.ReadText("stackable/stackable.lst")));
        var buffJewellery = catalog.Get(490013849);
        That(buffJewellery.Available && buffJewellery.SetsDisjointInstanceType &&
            buffJewellery.Selection is { SelectionCount: 1, Categories.Length: 1 } buffSelection &&
            buffSelection.Categories[0].PendingRule is null &&
            buffSelection.Categories[0].Rewards.Select(row => row.ItemId).SequenceEqual(new[] { 400310137, 400300136, 400320141 }) &&
            buffSelection.Categories[0].Rewards.All(row => row.Count == 1),
            "BUFF强化首饰盒按末条类型保留三个真实候选和分解来源，不误入空普通奖池");
        foreach (int id in new[] { Box, EventBox })
        {
            var definition = catalog.Get(id);
            That(definition.Available, "自选礼盒可解析 " + id + "，待定原因: " + (definition.PendingRule ?? "无"));
            var selection = definition.Selection;
            if (selection is null)
                continue;
            That(selection.SelectionCount == 1, "选择数量为1 " + id);
            That(selection.Categories.Length == 6, "6个分类 " + id);
            var jewellery = selection.Categories.FirstOrDefault(row => row.Category == 5 && row.Subcategory == 0);
            That(jewellery is not null && jewellery.Rewards.Any(row => row.ItemId == 100353086),
                "分类5为首饰/特殊装备且含100353086 " + id);
            That(selection.Categories.All(row => row.Rewards.Length is >= 5 and <= 6), "每个分类的候选数量在5到6件之间 " + id);
        }
        const int evidenceBox = 490004382;
        var evidence = catalog.Get(evidenceBox);
        var evidenceCategory = evidence.Selection?.Categories.SingleOrDefault(row => row.Category == 0 && row.Subcategory == 0);
        That(evidence.Available && evidence.Selection is { SelectionCount: 1 } && evidenceCategory is not null &&
            evidenceCategory.Rewards.Select(row => row.ItemId).SequenceEqual(new[] { 490004325, 490004326, 490004327, 490004328 }) &&
            evidenceCategory.Rewards.All(row => row.Count == 1),
            "当前PVF 490004382的[stackble]声明完整解析四个候选及其数量");
        // 此处只验证声明形态；艾肯产物另在所属用例验证品级写入，其他装备保持原有行为。
        const string head = "[stackable type]\n`[booster selection]` 0\n[booster selection num]\n1\n" +
            "[booster select category]\n0 0\n[equipment]\n401030066 1\n[/equipment]\n[/booster select category]\n";
        That(BoosterCatalog.Parse("[custom ability grade]\n3\n" + head).Available, "合法品级声明放行");
        foreach (string bad in new[] { "-1", "abc", "" })
            That(!BoosterCatalog.Parse("[custom ability grade]\n" + bad + "\n" + head).Available, "非法品级声明仍拒绝: " + bad);
        Console.WriteLine($"PASS {Count - checks} 自选礼盒资源断言");
    }
}
