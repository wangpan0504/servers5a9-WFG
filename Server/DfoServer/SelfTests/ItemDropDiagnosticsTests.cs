using DfoServer.GameWorld.Items;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class ItemDropDiagnosticsTests
{
    internal static void Run()
    {
        ItemTemplate first = new(100, ItemKind.Equipment, 0, 0, 0, 1, 0, 1, 4, "", 0, 0, "first") { CreationRate = 7 };
        ItemTemplate second = first with { Id = 101, Kind = ItemKind.Consumable, Grade = 8, Rarity = 2, CreationRate = 3 };
        var expected = ItemDropDiagnostics.Summarize([first, second]);
        That(expected.Count == 2 && expected.Digest == "6BE7161BF6AA19F74799FA947668B04D3E943FA54FC97D85CE66796DA6EEB7E9", "五字段按序小端摘要固定合同");
        That(ItemDropDiagnostics.Summarize([second, first]).Digest != expected.Digest &&
            ItemDropDiagnostics.Summarize([first, second, first]).Digest != expected.Digest, "摘要区分顺序和重复次数，不排序去重");
        foreach (var changed in new[] { first with { Id = 102 }, first with { Kind = ItemKind.Material }, first with { Grade = 5 },
            first with { Rarity = 2 }, first with { CreationRate = 8 } })
            That(ItemDropDiagnostics.Summarize([changed, second]).Digest != expected.Digest, "摘要包含每个候选语义字段");
        That(ItemDropDiagnostics.Summarize([first with { Name = "other" }, second]).Digest == expected.Digest,
            "摘要不冒称全部模板字段等价");
        That(ItemDropDiagnostics.Summarize([]).Count == 0 && ItemDropDiagnostics.Summarize([first with { Id = 0, Grade = 0, Rarity = 0, CreationRate = 0 }]).Count == 1,
            "空序列和零字段有明确编码，不代替业务合法性校验");
        ItemDropDiagnostics.Report? report = null;
        int reports = 0;
        var diagnostic = new ItemDropDiagnostics(value => { report = value; reports++; });
        diagnostic.Start(3);
        diagnostic.Current.Processed = 3;
        diagnostic.Current.Observe(false, new(2, 3, 4, 5, true, true));
        diagnostic.Current.Observe(true, new(2, 0, 4, 0, false, false));
        diagnostic.Capture([first, second], false);
        diagnostic.Complete(); diagnostic.Dispose(); diagnostic.Dispose();
        That(reports == 1 && report is { Completed: true, Total: 3, Processed: 3 } && report.Root.Calls == 1 && report.Import.ReadFailures == 1 &&
            report.OrderedPositiveCandidates == expected, "完成后仅报告一次，读取与导入桶独立并保留摘要");
        using (var failed = new ItemDropDiagnostics(value => report = value)) failed.Capture([first], false);
        That(report is { Completed: false, OrderedPositiveCandidates: null }, "失败构建不冒充成功候选摘要");
        int brokenCalls = 0;
        var broken = new ItemDropDiagnostics(_ => { brokenCalls++; throw new Exception("observer"); });
        broken.Complete(); broken.Dispose(); broken.Dispose();
        That(brokenCalls == 1, "报告回调失败不传播，释放仍幂等且不留后台任务");
    }
}
