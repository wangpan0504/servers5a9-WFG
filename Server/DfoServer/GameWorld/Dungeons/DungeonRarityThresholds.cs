using System.Collections.Immutable;

namespace DfoServer.GameWorld.Dungeons;

// 官方掉落表在百万骰之外用1000001/1000002/1000003标记不可命中的尾档，
// 抽样只有0..999999；解析时统一归一到分母，后续稀有度骰、史诗倍率和GM掉落控制不再各自判断哨兵。
internal static class DungeonRarityThresholds
{
    public const int Denominator = 1000000;

    public static ImmutableArray<int> Normalize(ReadOnlySpan<int> thresholds)
    {
        var result = new int[thresholds.Length];
        for (int i = 0; i < thresholds.Length; i++)
        {
            if (thresholds[i] < 0) throw new InvalidDataException("稀有度阈值不能为负");
            result[i] = Math.Min(thresholds[i], Denominator);
        }
        return [.. result];
    }
}
