using DfoServer.GameWorld.Items;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

// 英雄之证装备自选礼盒（490010644 与活动版 490010610）：[booster selection]，6 个分类、选择数量 1。
// 用来锁住 CMD160 选择型礼包的真实 PVF 结构，避免分类、奖励行或品级声明被解析规则误丢。
internal static class BoosterSelectionResourceTests
{
    const int Box = 490010644, EventBox = 490010610;

    public static void Run(PvfArchive pvf)
    {
        int checks = Count;
        var catalog = new BoosterCatalog(pvf, PvfScript.List(pvf.ReadText("stackable/stackable.lst")));
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
        // [custom ability grade] 只校验形态：合法整数放行且不写入实例，非法值仍整包拒绝。
        const string head = "[stackable type]\n`[booster selection]` 0\n[booster selection num]\n1\n" +
            "[booster select category]\n0 0\n[equipment]\n401030066 1\n[/equipment]\n[/booster select category]\n";
        That(BoosterCatalog.Parse("[custom ability grade]\n3\n" + head).Available, "合法品级声明放行");
        foreach (string bad in new[] { "-1", "abc", "" })
            That(!BoosterCatalog.Parse("[custom ability grade]\n" + bad + "\n" + head).Available, "非法品级声明仍拒绝: " + bad);
        Console.WriteLine($"PASS {Count - checks} 自选礼盒资源断言");
    }
}
