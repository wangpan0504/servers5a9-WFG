using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Services;

// [random choose info] 的抽取顺序固定：先掷本职业骰（万分比），再按品级权重掷档，最后在品级池里等概率取一件。
// 掷本职业骰命中但该档没有任何本职业可用产物时退回全体池，不能因为某职业缺高档产物就让开箱固定失败。
internal static class RuneBoxSelection
{
    public static string? Select(RuneRandomChooseRow row, Func<int, IReadOnlyList<int>> poolOfGrade,
        Func<int, bool> ownClass, Func<long, long> next, out int grade, out int productId)
    {
        grade = -1;
        productId = 0;
        long ownRoll = next(10000);
        long gradeRoll = next(row.TotalWeight);
        if (ownRoll is < 0 or >= 10000 || gradeRoll < 0 || gradeRoll >= row.TotalWeight)
            return "rune_box_roll";
        bool own = ownRoll < row.OwnWeight;
        foreach (var (candidate, weight) in row.Grades)
            if (gradeRoll < weight)
            {
                grade = candidate;
                break;
            }
            else
                gradeRoll -= weight;
        if (grade < 0)
            return "rune_box_roll";
        var pool = poolOfGrade(grade);
        if (own)
        {
            var ownProducts = pool.Where(ownClass).ToArray();
            if (ownProducts.Length != 0)
                pool = ownProducts;
        }
        if (pool.Count == 0)
            return "rune_box_empty";
        long pick = next(pool.Count);
        if (pick < 0 || pick >= pool.Count)
            return "rune_box_roll";
        productId = pool[checked((int)pick)];
        return null;
    }
}
