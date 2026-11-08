using System.Collections.Immutable;

namespace DfoServer.Game.Inventory.Models;

internal sealed record DailyBoosterUsage(int ItemId, int Day, byte Used)
{
    // 历史名称来自保留礼盒；疲劳主动道具复用相同的角色/STK模板日限次，不另建账本。
    public static void Validate(ImmutableArray<DailyBoosterUsage> rows)
    {
        if (rows.IsDefault || rows.Any(row => row is null || row.ItemId <= 0 || row.Day < 0 ||
            row.Day > DateOnly.MaxValue.DayNumber || row.Used == 0) ||
            rows.Select(row => row.ItemId).Distinct().Count() != rows.Length)
            throw new InvalidDataException("每日保留礼盒次数账本无效");
    }

    // 时钟倒退时保留最近成功次数，不能把未来日行当成新日零次数。
    public static byte Effective(ImmutableArray<DailyBoosterUsage> rows, int itemId, int today) =>
        rows.FirstOrDefault(row => row.ItemId == itemId && row.Day >= today)?.Used ?? 0;

    public static bool TryConsume(ImmutableArray<DailyBoosterUsage> rows, int itemId, int count, byte limit,
        DateTimeOffset now, out ImmutableArray<DailyBoosterUsage> after)
    {
        Validate(rows);
        after = rows;
        int today = ShopPurchaseCount.Today(now);
        if (itemId <= 0 || count <= 0 || limit == 0 || rows.Any(row => row.Day > today) ||
            (int)Effective(rows, itemId, today) + (long)count > limit) return false;
        after = rows.Where(row => row.Day == today && row.ItemId != itemId)
            .Append(new(itemId, today, checked((byte)(Effective(rows, itemId, today) + count))))
            .OrderBy(row => row.ItemId).ToImmutableArray();
        return true;
    }

    public static bool Same(ImmutableArray<DailyBoosterUsage> left, ImmutableArray<DailyBoosterUsage> right) =>
        left == right || left.Length == right.Length && (left.IsEmpty || left.ToHashSet().SetEquals(right));

    public static DateTimeOffset NextReset(DateTimeOffset now) =>
        new DateTimeOffset(DateOnly.FromDayNumber(ShopPurchaseCount.Today(now) + 1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).AddHours(-2);
}
