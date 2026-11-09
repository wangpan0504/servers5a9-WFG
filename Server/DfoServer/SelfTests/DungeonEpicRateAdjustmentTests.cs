using System.Collections.Immutable;
using DfoServer.Game.Dungeons;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class DungeonEpicRateAdjustmentTests
{
    public static void RunCore()
    {
        int before = Count;
        ImmutableArray<int> basis = [600, 700, 1000];
        ImmutableArray<bool> epics = [false, true, false];
        That(DungeonEpicRateAdjustment.Adjust(basis, epics, 1000, 1).SequenceEqual(basis), "一倍保持原抽样表");
        foreach (var (factor, expected) in new (decimal, int[])[]
        {
            (0, [666, 666, 1000]), (0.5m, [633, 683, 1000]), (2, [533, 733, 1000]), (100, [0, 1000, 1000])
        })
        {
            var adjusted = DungeonEpicRateAdjustment.Adjust(basis, epics, 1000, factor);
            That(adjusted.SequenceEqual(expected), "史诗区间倍率及非史诗归一化边界：" + factor);
            That(adjusted.Zip(adjusted.Skip(1)).All(pair => pair.First <= pair.Second), "区间保持单调，无负权重");
        }
        That(DungeonEpicRateAdjustment.AdjustWide([4000000000L, 5000000000L], [false, true], 5000000000L, 2)
            .SequenceEqual(new long[] {3000000000L, 5000000000L}), "64位开箱权重不得被32位截断");
        That(DungeonEpicRateAdjustment.AdjustWide([long.MaxValue - 1, long.MaxValue], [false, true], long.MaxValue, 100)
            .SequenceEqual(new long[] {long.MaxValue - 100, long.MaxValue}), "极大合法权重乘法不溢出且不丢史诗区间");
        That(DungeonEpicRateAdjustment.Adjust([1000], [true], 1000, 0).SequenceEqual([0]), "零倍率允许原全史诗池为空，不伪造其它奖励");
        That(DungeonEpicRateAdjustment.Adjust([1000], [true], 1000, 0.5m).SequenceEqual([500]), "原全史诗池减半后保留真实空抽区间");
        That(DungeonEpicRateAdjustment.Adjust([100, 200, 400], [true, false, true], 1000, 2).SequenceEqual([200, 257, 657]),
            "多个史诗区间保持原相对权重，未声明空抽参与归一化");
        foreach (var factor in new[] { -1m, 100.01m })
            Reject<InvalidDataException>(() => DungeonEpicRateAdjustment.Adjust(basis, epics, 1000, factor), "非法倍率拒绝");
        Reject<InvalidDataException>(() => DungeonEpicRateAdjustment.Adjust([100, 99], [true, false], 1000, 2), "倒序权重拒绝");
        That(basis.SequenceEqual([600, 700, 1000]), "原概率快照不被修改");
        Console.WriteLine($"PASS {Count - before} universal epic card probability checks");
    }
}
