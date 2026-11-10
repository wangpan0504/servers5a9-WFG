using System.Collections.Immutable;

namespace DfoServer.Game.Dungeons;

// 固定只抽一个结果的奖励表通过重分配区间接入倍率，不重复抽奖或追加装备。
internal static class DungeonEpicRateAdjustment
{
    public static ImmutableArray<int> Adjust(ImmutableArray<int> cumulative, ImmutableArray<bool> epics,
        int denominator, decimal multiplier)
    {
        if (cumulative.IsDefaultOrEmpty || cumulative.Length != epics.Length || denominator <= 0 || multiplier is < 0 or > 100)
            throw new InvalidDataException("史诗奖励概率表或倍率无效");
        // 官方表在百万骰之外用1000001/1000002/1000003标记不可命中的尾档；夹到分母与可达区间等价，
        // 不能再按"超出分母"拒绝，否则只是开启GM掉落控制就会让普通翻牌断开连接。
        var thresholds = new int[cumulative.Length];
        int previous = 0, epic = 0;
        for (int i = 0; i < cumulative.Length; i++)
        {
            thresholds[i] = Math.Min(cumulative[i], denominator);
            if (thresholds[i] < previous) throw new InvalidDataException("史诗奖励累计概率无效");
            if (epics[i]) epic = checked(epic + thresholds[i] - previous);
            previous = thresholds[i];
        }
        if (epic == 0 || multiplier == 1) return cumulative;
        int enhanced = (int)decimal.Min(denominator, decimal.Floor(epic * multiplier));
        var result = ImmutableArray.CreateBuilder<int>(cumulative.Length);
        int epicWeight = 0, nonEpicWeight = 0, total = 0;
        previous = 0;
        for (int i = 0; i < thresholds.Length; i++)
        {
            int width = thresholds[i] - previous;
            previous = thresholds[i];
            if (epics[i]) epicWeight += width; else nonEpicWeight += width;
            total = checked((int)((long)epicWeight * enhanced / epic +
                (epic == denominator ? 0 : (long)nonEpicWeight * (denominator - enhanced) / (denominator - epic))));
            result.Add(total);
        }
        return result.MoveToImmutable();
    }
    // 开箱资源使用64位权重，区间乘法用整数宽算术，不能缩小合法奖池或溢出。
    public static ImmutableArray<long> AdjustWide(ImmutableArray<long> cumulative, ImmutableArray<bool> epics,
        long denominator, decimal multiplier)
    {
        if (cumulative.IsDefaultOrEmpty || cumulative.Length != epics.Length || denominator <= 0 || multiplier is < 0 or > 100)
            throw new InvalidDataException("史诗奖励概率表或倍率无效");
        // 与32位版本相同：哨兵尾档夹到分母，不倒序拒绝合法资源。
        var thresholds = new long[cumulative.Length];
        long previous = 0, epic = 0;
        for (int i = 0; i < cumulative.Length; i++)
        {
            thresholds[i] = Math.Min(cumulative[i], denominator);
            if (thresholds[i] < previous) throw new InvalidDataException("史诗奖励累计概率无效");
            if (epics[i]) epic = checked(epic + thresholds[i] - previous);
            previous = thresholds[i];
        }
        if (epic == 0 || multiplier == 1) return cumulative;
        long enhanced = (long)decimal.Min(denominator, decimal.Floor(epic * multiplier));
        var result = ImmutableArray.CreateBuilder<long>(cumulative.Length);
        long epicWeight = 0, nonEpicWeight = 0;
        previous = 0;
        for (int i = 0; i < thresholds.Length; i++)
        {
            long width = thresholds[i] - previous;
            previous = thresholds[i];
            if (epics[i]) epicWeight += width; else nonEpicWeight += width;
            long total = checked((long)((System.Numerics.BigInteger)epicWeight * enhanced / epic +
                (epic == denominator ? 0 : (System.Numerics.BigInteger)nonEpicWeight * (denominator - enhanced) / (denominator - epic))));
            result.Add(total);
        }
        return result.MoveToImmutable();
    }

}
