using System.Collections.Immutable;
using System.Numerics;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Dungeons;

internal static class EquipmentRarityDistribution
{
    public static ImmutableArray<int> Adjust(ImmutableArray<int> cumulative, EquipmentRarityBonus bonus,
        bool rareAvailable = true, bool uniqueAvailable = true)
    {
        if (bonus.IsEmpty) return cumulative;
        if (cumulative.Length != 7 || cumulative[^1] < DungeonDropRatePlan.RarityDenominator ||
            cumulative[0] < 0 || !cumulative.SequenceEqual(cumulative.Order()))
            throw new InvalidDataException("装备掉率调整需要完整七档累计概率");
        var widths = new long[8];
        int previous = 0;
        // 1000001等哨兵在百万骰外；只调整可达区间，不给禁用的尾档制造概率。
        for (int i = 0; i < 7; i++)
        {
            int threshold = Math.Min(cumulative[i], DungeonDropRatePlan.RarityDenominator);
            widths[i] = threshold - previous;
            previous = threshold;
        }
        Expand(widths, [0, 1, rareAvailable ? 2 : -1, uniqueAvailable ? 3 : -1, 4, 5, 6, -2], bonus);
        int sum = 0;
        return Enumerable.Range(0, 7).Select(i => sum = checked(sum + (int)widths[i])).ToImmutableArray();
    }

    // 原档一次性计算：先让出白/蓝装备，再使用直接混池的空抽；不压低其它物品或高品质。
    // 最后一项可用rarity=-2表示空抽，-1表示不参与调整的非装备物品。
    internal static void Expand(long[] widths, int[] rarities, EquipmentRarityBonus bonus)
    {
        if (bonus.RarePercent < 0 || bonus.UniquePercent < 0 || widths.Length != rarities.Length || widths.Any(w => w < 0))
            throw new InvalidDataException("装备掉率增量或原分布非法");
        long rare = 0, unique = 0, low = 0, empty = 0;
        for (int i = 0; i < widths.Length; i++)
            switch (rarities[i])
            {
                case 0: case 1: low = checked(low + widths[i]); break;
                case 2: rare = checked(rare + widths[i]); break;
                case 3: unique = checked(unique + widths[i]); break;
                case -2: empty = checked(empty + widths[i]); break;
            }
        BigInteger rareExtra = (BigInteger)rare * bonus.RarePercent / 100;
        BigInteger uniqueExtra = (BigInteger)unique * bonus.UniquePercent / 100;
        BigInteger requested = rareExtra + uniqueExtra;
        if (requested == 0 || checked(low + empty) == 0) return;
        long used = (long)BigInteger.Min(requested, (BigInteger)low + empty);
        long rareAdded = (long)((BigInteger)used * rareExtra / requested), uniqueAdded = used - rareAdded;
        long lowUsed = Math.Min(low, used), emptyUsed = used - lowUsed;
        long seenLow = 0, seenRare = 0, seenUnique = 0, seenEmpty = 0;
        long allocatedLow = 0, allocatedRare = 0, allocatedUnique = 0, allocatedEmpty = 0;
        for (int i = 0; i < widths.Length; i++)
        {
            long original = widths[i];
            switch (rarities[i])
            {
                case 0: case 1:
                    seenLow += original; Transfer(i, Share(lowUsed, seenLow, low), ref allocatedLow, -1); break;
                case 2:
                    seenRare += original; Transfer(i, Share(rareAdded, seenRare, rare), ref allocatedRare, 1); break;
                case 3:
                    seenUnique += original; Transfer(i, Share(uniqueAdded, seenUnique, unique), ref allocatedUnique, 1); break;
                case -2:
                    seenEmpty += original; Transfer(i, Share(emptyUsed, seenEmpty, empty), ref allocatedEmpty, -1); break;
            }
        }
        void Transfer(int i, long cumulative, ref long allocated, int sign)
        {
            widths[i] = checked(widths[i] + sign * (cumulative - allocated));
            allocated = cumulative;
        }
    }

    static long Share(long budget, long cumulative, long total) => total == 0 ? 0 :
        checked((long)((UInt128)(ulong)budget * (ulong)cumulative / (ulong)total));
}
