using System.Collections.Immutable;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Models;

internal sealed record BuyItemRequest(int ItemId, int Count, int ShopId, int NpcId);
internal sealed record SellItemRequest(InventorySlot Position, int Count, int NpcContext, int WindowState, uint Check);
internal sealed record ShopPurchaseCount(int NpcId, int ItemId, bool AccountWide, bool ResetDaily, int Day, int Count)
{
    public ItemPurchasePeriod Period { get; init; }
    public bool SameKey(ShopPurchaseCount other) => NpcId == other.NpcId && ItemId == other.ItemId &&
        AccountWide == other.AccountWide && ResetDaily == other.ResetDaily && Period == other.Period;
    // 每日资格以 UTC+8 的06:00为界；没有 reset 标记的限购不因名称含 daily 而清零。
    public static int Today(DateTimeOffset utcNow) => DateOnly.FromDateTime(utcNow.UtcDateTime.AddHours(2)).DayNumber;
    public static int PeriodDay(int today, ItemPurchasePeriod period)
    {
        var date = DateOnly.FromDayNumber(today);
        return period switch
        {
            ItemPurchasePeriod.Legacy or ItemPurchasePeriod.Daily => today,
            ItemPurchasePeriod.Weekly => today - ((int)date.DayOfWeek - (int)DayOfWeek.Thursday + 7) % 7,
            ItemPurchasePeriod.Monthly => new DateOnly(date.Year, date.Month, 1).DayNumber,
            ItemPurchasePeriod.Accumulate => 0,
            _ => throw new InvalidDataException("未知商品限购周期")
        };
    }
    public int Effective(int today) => Period == ItemPurchasePeriod.Legacy
        ? !ResetDaily || Day == today ? Count : 0
        : Period == ItemPurchasePeriod.Accumulate || Day == PeriodDay(today, Period) ? Count : 0;

    public long? NextReset(int today)
    {
        if (Period == ItemPurchasePeriod.Accumulate || Period == ItemPurchasePeriod.Legacy && !ResetDaily) return null;
        int start = PeriodDay(today, Period);
        var next = Period switch
        {
            ItemPurchasePeriod.Weekly => DateOnly.FromDayNumber(start).AddDays(7),
            ItemPurchasePeriod.Monthly => DateOnly.FromDayNumber(start).AddMonths(1),
            _ => DateOnly.FromDayNumber(start).AddDays(1)
        };
        return new DateTimeOffset(next.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).AddHours(-2).ToUnixTimeSeconds();
    }
    public static bool Same(ImmutableArray<ShopPurchaseCount> left, ImmutableArray<ShopPurchaseCount> right) =>
        left.Length == right.Length && left.ToHashSet().SetEquals(right);
}

internal sealed record ShopMaterialBalance(int ItemId, long Remaining);
// Paid 保存扣费后余额，Core 保存首个获得槽的完整核心；协议层自行投影本次购买增量。
internal sealed record ShopPurchaseReceipt(int ItemId, int Count, InventorySlot Position, ItemCore? Core,
    InventorySnapshot Paid, ImmutableArray<ShopMaterialBalance> Materials);
