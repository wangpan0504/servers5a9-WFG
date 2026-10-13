using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Game.Auction;

/// <summary>
/// CMD 184 `AUCTION_REGIST_CANCEL`（取消上架）的请求。
///
/// 实测样本 **9 字节**：`00-05-00-00-00-0A-D4-99-6A`
///   `[0]` 前导 0（与 183 的 Leading 同款）
///   `[1..4]` i32 LE 挂牌 id（**定位用**，样本 = 5）
///   `[5..8]` i32 LE 到期时间（辅助字段，样本 = 0x6A99D40A）
///
/// **只用 `[1..4]` 定位**：`[5..8]` 的语义（到期时间还是挂牌 id 高位）在参考实现里也只是推断，
/// 因此它不参与定位、也不作为拒绝条件，只记进日志与真实挂牌对照（`ExpiresEchoMatches`），
/// 作为继续收敛该字段语义的证据 —— 与 183 处理推断字段的口径一致。
///
/// 长度按客户端版本容错：**至少 9 字节**才解析（不足即拒绝，不按残缺字段去动资产），
/// 多出的尾部字节不参与解析但记进日志。
/// </summary>
internal sealed record AuctionCancelRequest(byte Leading, int ListingId, long ExpiresAtUnix)
{
    /// <summary>主体长度：`[0]` + 挂牌 id + 到期时间。</summary>
    public const int BodySize = 9;

    public static AuctionCancelRequest Parse(ReadOnlySpan<byte> body)
    {
        if (body.Length < BodySize)
            throw new InvalidDataException($"取消上架请求至少需要 {BodySize} 字节，实际 {body.Length}");
        // 用 Span 重载逐字段读：`BitConverter.ToInt32(byte[], int)` 不接受 Span，这里不复制整包。
        return new(body[0], BitConverter.ToInt32(body.Slice(1, 4)), BitConverter.ToUInt32(body.Slice(5, 4)));
    }
}

/// <summary>
/// 取消上架被拒绝的原因。**每一条都要能映射到客户端认识的白名单错误码**
/// （复用 183 的那张表，见 <see cref="AuctionRegisterErrors.Whitelist"/>）：
/// 客户端把未知 errcode 当成成功，因此不允许出现没有映射的取值。
/// </summary>
internal enum AuctionCancelError
{
    None = 0,

    /// <summary>请求字段非法：不足 <see cref="AuctionCancelRequest.BodySize"/> 字节、挂牌 id 非正。</summary>
    InvalidRequest,

    /// <summary>挂牌不存在（已被删除，或客户端拿的是过期行）。</summary>
    NotFound,

    /// <summary>挂牌不属于当前角色（换角后点旧窗口的取消按钮）。</summary>
    NotOwner,

    /// <summary>挂牌已经不在售（已取消 / 已成交 / 已下架）。</summary>
    AlreadyInactive,

    /// <summary>数据不可用：模板解析失败，或挂牌保存的物品快照读不回来（此时**不能**动挂牌状态）。</summary>
    Unavailable,

    /// <summary>背包放不下（<see cref="DfoServer.Game.Inventory.Services.InventoryRewardGrantService.TryGrantCore"/> 拒绝）。</summary>
    InventoryRejected,

    /// <summary>提交时角色或背包快照已变化（换角、接管、revision 冲突）。</summary>
    StalePlan
}

/// <summary>
/// ★ **CMD 184 失败应答使用的错误码**。与 183 共用同一张客户端白名单
/// （客户端对未知 errcode 会 fall-through 到成功分支，见 <see cref="AuctionRegisterErrors"/>），
/// 取值策略：
///   - `0x7e`（静默收尾）：**只用于「这条挂牌已经不在/不是你的」**这类过期操作
///     （NotFound / NotOwner / AlreadyInactive）—— 与 183 的「道具已经不在背包」同款；
///   - `0xd2`（通用提示框）：其余全部 —— 尤其 **背包放不下**必须让玩家看到提示，
///     否则道具没回来而界面「假装成功」才是最坏的结果。
/// ⚠ 184 回调自己的 errcode 分派表没有本客户端实测（183 那张表是反汇编 0x1126cd0 得到的），
/// 这里沿用同一张表是**保守选择**：白名单里的码至少不会走进「未知码 = 成功」的分支。
/// </summary>
internal static class AuctionCancelErrors
{
    public const byte Generic = AuctionRegisterErrors.Generic;
    public const byte Silent = AuctionRegisterErrors.Silent;

    public static byte ToClientCode(AuctionCancelError error) => error switch
    {
        AuctionCancelError.NotFound or AuctionCancelError.NotOwner or AuctionCancelError.AlreadyInactive => Silent,
        AuctionCancelError.None => throw new InvalidDataException("成功没有错误码"),
        _ => Generic
    };
}

/// <summary>
/// 一次取消上架的**只读规划**：定位并校验挂牌后，把「道具还回背包」交给
/// <see cref="DfoServer.Game.Inventory.Services.InventoryRewardGrantService.TryGrantCore"/>
/// 产出库存计划（复用现有发放入口，不另写一套放置规则），
/// 提交时由 <see cref="AuctionService.Cancel"/> 把「挂牌置为已下架」与这份计划放进**同一个外层事务**。
/// </summary>
internal sealed record AuctionCancelPlan(
    int ListingId,
    int ItemTemplateId,
    byte ItemKind,
    int ListingCount,
    int ReturnCount,
    long ExpiresAtUnix,
    InventoryPlan Inventory);

/// <summary>规划结果：失败也返回同一种类型（`Error` 非 None、`Options` 为 null），并带上进日志的原因。</summary>
internal sealed record AuctionCancelDecision(AuctionCancelError Error, AuctionCancelPlan? Options, string? Detail = null)
{
    public static AuctionCancelDecision Ok(AuctionCancelPlan plan) => new(AuctionCancelError.None, plan);

    public static AuctionCancelDecision Reject(AuctionCancelError error, string? detail = null) => new(error, null, detail);
}

/// <summary>
/// 取消上架的提交结果。`Character` 是提交后的权威快照（revision 已前进、道具已回到背包），
/// 写库失败则整体抛出、不返回成功（与上架同一口径）。
/// </summary>
internal sealed record AuctionListingCancelResult(AuctionCancelError Error, Character? Character)
{
    public static AuctionListingCancelResult Reject(AuctionCancelError error) => new(error, null);
}
