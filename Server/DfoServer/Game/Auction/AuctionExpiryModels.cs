using System.Collections.Immutable;

namespace DfoServer.Game.Auction;

/// <summary>
/// 到期结算（**到期未售**）的一行：把「道具退回」和「保证金退还」装进**同一封系统邮件**。
///
/// 为什么用邮件而不是直接把道具塞回背包、把钱加回钱包：
///   ① 卖家可能已经下线 —— 到期是**时间驱动**的（`expires_at_unix`），结算必须离线可完成；
///   ② 背包容量与钱包上限的判定发生在**领取**时（邮件模块既有规则），结算时不至于因为背包满而卡住
///      「到期」这件事本身；
///   ③ 与一口价购买的卖方所得同一种通道（托管邮件 + `delivery_key` 幂等键），
///      因此「退一份还是两份」由唯一约束兜底，而不是靠调用顺序。
///
/// <see cref="Delivered"/> = false 表示这一行**没有被结算**（条件更新影响 0 行：已被下架/成交或并发结算），
/// 此时状态与邮件都不动 —— 绝不重复退钱、也绝不把同一件道具退两次。
/// </summary>
internal sealed record AuctionExpiryRow(int ListingId, int ItemTemplateId, AuctionCustodyMail Mail)
{
    public bool Delivered { get; init; }

    /// <summary>结算结果说明（投递成功的记 `mail_id=`，被跳过的记原因）。</summary>
    public string? Note { get; init; }
}

/// <summary>
/// 到期结算的**只读规划**：要结算哪几行、每行的退回邮件长什么样。
///
/// <see cref="Skipped"/> 记「看见了但没能结算」的行（模板解析失败、邮件文本超限等）：
/// 这些行留在 `state=0`，下一次访问重试 —— 与取消上架同一条规则
/// （解不出模板就不能流转状态，否则道具真的没了）。
/// </summary>
internal sealed record AuctionExpiryPlan(
    long SellerCharacterId,
    ImmutableArray<AuctionExpiryRow> Rows,
    ImmutableArray<string> Skipped,
    long SettledAtUnix)
{
    /// <summary>没有到期行（或没有选中角色）：什么都不做。</summary>
    public static readonly AuctionExpiryPlan None = new(0, [], [], 0);

    public bool IsEmpty => Rows.IsDefaultOrEmpty;
}

/// <summary>到期结算的提交结果：逐行说明投递了什么、哪几行被跳过。</summary>
internal sealed record AuctionExpiryResult(ImmutableArray<AuctionExpiryRow> Rows, ImmutableArray<string> Skipped)
{
    public static readonly AuctionExpiryResult None = new([], []);

    /// <summary>本次真正结算（状态已流转 + 退回邮件已投递）的行数。</summary>
    public int SettledCount => Rows.IsDefaultOrEmpty ? 0 : Rows.Count(row => row.Delivered);
}
