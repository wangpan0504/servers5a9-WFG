using static PvfLib.Tests.TestCheck;

namespace PvfLib.Tests;

internal static class AmplificationFileTests
{
    public static void Run()
    {
        PvfToken I(int value) => new(0, value);
        var options = new PvfTokenSection("option data", [new(6, 1), I(2500), I(5), new(6, 2), I(0), I(7)], []);
        That(AmplificationFiles.Options(options, id => "option" + id).Select(row => (row.Label, row.Weight, row.BaseValue))
            .SequenceEqual([("option1", 2500, 5), ("option2", 0, 7)]), "异次元属性原始行保留顺序、零权重及基础值");
        var material = new PvfTokenSection("invest option", [new(6, 0), I(1286), I(1)], []);
        That(AmplificationFiles.Materials(material, _ => "[all]", true) is [{ Option: "[all]", ItemId: 1286, Count: 1 }],
            "可选择材料保留属性标签、模板及数量");
        var purify = material with { Values = [I(1183), I(2)] };
        That(AmplificationFiles.Materials(purify, _ => "", false) is [{ Option: null, ItemId: 1183, Count: 2 }],
            "净化材料双整数字段不套用三列属性行");
        var rarity = new PvfTokenSection("rarity weight", [new(6, 0), new(2, BitConverter.SingleToInt32Bits(1.4f))], []);
        That(AmplificationFiles.NamedNumbers(rarity, _ => "legendary") is [{ Label: "legendary", Value: 1.4f }],
            "异次元倍率保留原float32而不从十进制说明重建");
        var levels = new PvfTokenSection("amplification random value", [I(3), I(50), I(6), I(5)], []);
        That(AmplificationFiles.Levels(levels).Select(row => (row.Level, row.Weight)).SequenceEqual([(3, 50), (6, 5)]),
            "黄金增幅等级和权重成对保留");
        foreach (var bad in new[] { options with { Values = options.Values[..^1] }, options with { Values = [I(1), I(2500), I(5)] },
            options with { Children = [options] } })
            Reject<InvalidDataException>(() => AmplificationFiles.Options(bad, _ => "[all]"));
        Reject<InvalidDataException>(() => AmplificationFiles.Materials(material with { Values = [new(6, 0), I(1)] }, _ => "[all]", true));
        Reject<InvalidDataException>(() => AmplificationFiles.Levels(levels with { Values = [I(3)] }));
        Reject<InvalidDataException>(() => AmplificationFiles.Scalar(levels));
        var rates = new PvfTokenSection("amplification rate by rarity", [I(0), I(0), I(12), I(2061), I(2000), I(2000), I(2000)], []);
        That(AmplificationFiles.Integers(rates).SequenceEqual(new[] { 0, 0, 12, 2061, 2000, 2000, 2000 }), "异界气息七稀有度概率保持原始整数与顺序");
        Reject<InvalidDataException>(() => AmplificationFiles.Integers(rates with { Values = [new(2, BitConverter.SingleToInt32Bits(12f))] }));
        var upgrade = new PvfTokenSection("table type", [new(6, 0), .. Enumerable.Repeat(I(0), 24)], []);
        var row = upgrade.Values.ToArray();
        row[9] = I(60000); row[11] = I(3); row[15] = I(3242); row[16] = I(11);
        upgrade = upgrade with { Values = [.. row] };
        That(AmplificationFiles.UpgradeRows(upgrade) is [{ FailureWeight: 60000, PenaltyType: 3, MaterialId: 3242, MaterialCount: 11 }],
            "新版24列增幅行按实际列读取失败权重及材料，后八列不是下一等级");
        Reject<InvalidDataException>(() => AmplificationFiles.UpgradeRows(upgrade with { Values = upgrade.Values[..^1] }));
        row[9] = new(2, BitConverter.SingleToInt32Bits(60000f));
        Reject<InvalidDataException>(() => AmplificationFiles.UpgradeRows(upgrade with { Values = [.. row] }));
        var lucky = new PvfTokenSection("enchant type", [new(6, 0), I(14), I(82), I(0), I(18)], []);
        That(AmplificationFiles.LuckyRows(lucky) is [{ Level: 14, DownWeight: 82, SameWeight: 0, UpWeight: 18 }], "幸运表保留独立降级、不变及升级权重");
        Reject<InvalidDataException>(() => AmplificationFiles.LuckyRows(lucky with { Values = lucky.Values[..^1] }));
    }
}
