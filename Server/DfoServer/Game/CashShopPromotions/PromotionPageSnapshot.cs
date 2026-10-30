using System.Collections.Immutable;

namespace DfoServer.Game.CashShopPromotions;

// 只读页面结果，不授予购买资格，也不代替进度/订单/奖励账本。
internal sealed record PromotionPageReward(int ItemId, int Count, string Name);
internal sealed record PromotionPageSnapshot(string Campaign, string Period, string RulesVersion,
    string Realm, string Group, long CatalogRevision, long? ProgressRevision, string Status,
    long? Count, long MaximumPurchases, uint? AwardMask27, long? OverflowAwardCount, int TtlMs,
    ImmutableArray<PromotionPageReward> Rewards, PromotionPageReward? OverflowReward);