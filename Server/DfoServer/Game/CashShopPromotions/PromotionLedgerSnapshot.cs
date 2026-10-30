namespace DfoServer.Game.CashShopPromotions;

// 已读进度与账本完整性分开：账本不可核时仍保留已知count/revision，不作补发。
internal sealed record PromotionLedgerSnapshot(PromotionProgress? Progress, uint? AwardMask27,
    long? OverflowAwardCount, bool Verified);