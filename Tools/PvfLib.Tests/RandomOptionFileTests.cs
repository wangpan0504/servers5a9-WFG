using static PvfLib.Tests.TestCheck;

namespace PvfLib.Tests;

internal static class RandomOptionFileTests
{
    public static void Run()
    {
        PvfToken I(int value) => new(0, value);
        var types = new PvfTokenSection("modified option type", [I(2), I(-1), I(10), I(1500), I(7000), I(1500)], []);
        That(RandomOptionFiles.Types(types) is [{ Rarity: 2, ItemType: -1, Level: 10 } row] &&
            row.Weights.SequenceEqual([1500, 7000, 1500]), "变换类型保留六字段与原权重顺序");
        var groups = new PvfTokenSection("modified option selection", [I(2), I(-1), new(6, 0), I(2), I(2),
            I(1002), I(1007), I(0), I(1000)], []);
        var parsed = RandomOptionFiles.Groups(groups, _ => "lt coat").Single();
        That(parsed.Part == "lt coat" && parsed.OptionType == 2 && parsed.Groups.SequenceEqual([1002, 1007]) &&
            parsed.Weights.SequenceEqual([0, 1000]), "本版变长组选行没有等级列，零权重与候选组均保留");
        var cost = new PvfTokenSection("option modification", [I(1), I(50), I(500), I(15), I(200), I(2000)], []);
        That(RandomOptionFiles.ModificationCosts(cost).Select(row => (row.Level, row.CommonGold, row.UniqueGold))
            .SequenceEqual([(1, 50, 500), (15, 200, 2000)]), "变换费用保留原序三列，不在解析层选档");
        var range = new PvfTokenSection("different weight", [I(2), I(1), I(30), I(55)], []);
        That(RandomOptionFiles.Ranges(range) is [{ Minimum: 30, Maximum: 55 }], "随机scalar范围保留四字段");
        var ratio = new PvfTokenSection("option value ratio", [new(2, BitConverter.SingleToInt32Bits(0.8f)), I(1), I(1000)], []);
        That(RandomOptionFiles.ValueRatios(ratio) is [{ Lower: 0.8f, Upper: 1, Weight: 1000 }],
            "比例保留float32与整数混合token，不使用十进制文本重解析");
        Reject<InvalidDataException>(() => RandomOptionFiles.Types(types with { Values = types.Values[..^1] }));
        Reject<InvalidDataException>(() => RandomOptionFiles.Types(types with { Values = [new(6, 0), ..types.Values[1..]] }));
        Reject<InvalidDataException>(() => RandomOptionFiles.Groups(groups with { Values = groups.Values[..^1] }, _ => "lt coat"));
        Reject<InvalidDataException>(() => RandomOptionFiles.ModificationCosts(cost with { Values = cost.Values[..^1] }));
        Reject<InvalidDataException>(() => RandomOptionFiles.Ranges(range with { Children = [range] }));
        Reject<InvalidDataException>(() => RandomOptionFiles.ValueRatios(ratio with { Values = [I(1), I(1), new(2, 0)] }));
    }
}
