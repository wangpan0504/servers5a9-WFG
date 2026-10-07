using System.Collections.Immutable;

namespace DfoServer.Game.Inventory.Models;

internal sealed record BuyItemRequest(int ItemId, int Count, int ShopId, int NpcId);
internal sealed record SellItemRequest(InventorySlot Position, int Count, int NpcContext, int WindowState, uint Check);
internal sealed record ShopPurchaseCount(int NpcId, int ItemId, bool AccountWide, bool ResetDaily, int Day, int Count)
{
    public bool SameKey(ShopPurchaseCount other) => NpcId == other.NpcId && ItemId == other.ItemId &&
        AccountWide == other.AccountWide && ResetDaily == other.ResetDaily;
    // 每日资格以 UTC+8 的06:00为界；没有 reset 标记的限购不因名称含 daily 而清零。
    public static int Today(DateTimeOffset utcNow) => DateOnly.FromDateTime(utcNow.UtcDateTime.AddHours(2)).DayNumber;
    public int Effective(int today) => !ResetDaily || Day == today ? Count : 0;
    public static bool Same(ImmutableArray<ShopPurchaseCount> left, ImmutableArray<ShopPurchaseCount> right) =>
        left.Length == right.Length && left.ToHashSet().SetEquals(right);
}

internal sealed record ShopMaterialBalance(int ItemId, long Remaining);
// Paid 保存扣费后余额，Core 保存首个获得槽的完整核心；协议层自行投影本次购买增量。
internal sealed record ShopPurchaseReceipt(int ItemId, int Count, InventorySlot Position, ItemCore? Core,
    InventorySnapshot Paid, ImmutableArray<ShopMaterialBalance> Materials);
