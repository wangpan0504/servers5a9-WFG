using System.Collections.Immutable;
using System.Text;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Game.Auction;

/// <summary>
/// 一口价购买的两个入口（C2S）。185 已接入真实购买；334 仍是**请求抓取桩**
/// （它的布局连前导字节位置都没定，见 <see cref="AuctionBuyProbes"/>）。
/// 本层不认识 <c>CmdPacketType</c>（Game 不引用 Network，见 S5A9001），因此自带一份最小映射。
/// </summary>
internal enum AuctionBuyCommand
{
    /// <summary>CMD 185 `AUCTION_BIDDING`：本客户端点「购买」发的就是这条（两个实机样本已定案）。</summary>
    Bidding = 185,

    /// <summary>CMD 334 `AUCTION_BUY_ITEM_APIECE`：参考实现里的另一入口（该版本未走），精确偏移自标未定。</summary>
    BuyItemApiece = 334
}

/// <summary>
/// CMD 185 `AUCTION_BIDDING`（一口价购买）请求体。**布局已由两个实机样本差分完全定案**，不再是候选。
///
/// 两个样本（点拍卖对话框唯一的「购买」按钮发出；本版客户端没有竞拍 UI），各 **30 字节**：
/// <code>
/// A 点挂牌 5（模板 101010912、一口价 24000）: 00 C05D0000 05 000000 84D6CB6A 00×13 E04D0506
/// B 点挂牌 8（模板 106000064、一口价 30000）: 00 30750000 08 000000 84D6CB6A 00×13 C06E5106
/// </code>
///
/// | 偏移 | 宽度 | 含义 | 证据 |
/// | --- | ---: | --- | --- |
/// | `[0]` | 1 | 前导 0（与 183/184 同款） | 两样本都是 0 |
/// | `[1..4]` | i32 LE | **一口价金额**（是单价还是总额，两样本数量都是 1 ⇒ **本组样本区分不了**） | A=24000、B=30000，与库内 `buyout_price` 一致 |
/// | `[5..8]` | i32 LE | **挂牌 id**（定位用） | A=5、B=8，与 `listing_id` 一致 |
/// | `[9..12]` | i32 LE | **到期时间回传**（库内 `expires_at_unix` 的回显） | 两样本都是 1791743620，与库内两行一致 |
/// | `[13..25]` | 13 | 保留 0 | 两样本都是 0（不是 26B 参考布局里那个「道具标识字符串」） |
/// | `[26..29]` | i32 LE | **物品模板 id** | A=101010912、B=106000064，与库内一致 |
///
/// ★ **参考实现（另一客户端版本）的 26B 布局已被这两个样本证伪**：它的 `[13..25]` 之后没有数据，
/// 而本客户端在那之后还有 4 字节物品模板 id —— 因此**不能**按「26B 一行到底」去读，
/// 也不允许把 `[1..4]` 当成「≥ 某个猜测值就成交」（见[并发、事务与响应延迟](../../../../Docs/规范/并发事务与响应延迟.md)
/// 的「跨账号转移：拍卖行一口价购买」一节）。
///
/// 长度按客户端版本容错：**主体必须 ≥ <see cref="BodySize"/>** 才解析（不足即拒绝 —— 拿不到
/// `[26..29]` 就无法与挂牌的模板交叉校验，不能按残缺字段动跨账号资产）；多出的尾部字节不参与解析但记进日志。
/// </summary>
internal sealed record AuctionBuyRequest(byte Leading, int Amount, int ListingId, uint ExpiresAtUnix, int ItemTemplateId)
{
    /// <summary>已定案的请求体主体长度：前导(1) + 一口价(4) + 挂牌 id(4) + 到期回传(4) + 保留(13) + 模板 id(4)。</summary>
    public const int BodySize = 30;

    /// <summary>一口价金额字段的偏移（`[1..4]`），供日志与自测引用同一份定义。</summary>
    public const int AmountOffset = 1;

    /// <summary>挂牌 id 字段的偏移（`[5..8]`）。</summary>
    public const int ListingIdOffset = 5;

    /// <summary>到期回传字段的偏移（`[9..12]`）。</summary>
    public const int ExpiresEchoOffset = 9;

    /// <summary>保留区偏移（`[13..25]`，13 字节全 0）。</summary>
    public const int ReservedOffset = 13;

    /// <summary>保留区宽度（13 字节）。</summary>
    public const int ReservedWidth = 13;

    /// <summary>物品模板 id 字段的偏移（`[26..29]`）。**不足这个宽度就必须拒绝**（定案要求 ①）。</summary>
    public const int ItemTemplateIdOffset = 26;

    /// <summary>
    /// 解析 185 请求体。**不足 <see cref="BodySize"/> 字节即拒绝**（抛 <see cref="InvalidDataException"/>）：
    /// 拿不到 `[26..29]` 的物品模板 id 就无法与挂牌交叉校验，而这是跨账号钱货转移的入口。
    /// 返回值只做字段切分，不含任何业务判断。
    /// </summary>
    public static AuctionBuyRequest Parse(ReadOnlySpan<byte> body)
    {
        if (body.Length < BodySize)
            throw new InvalidDataException($"CMD 185 请求体至少需要 {BodySize} 字节（两样本都是 30）：{body.Length}");
        // 用 Span 重载逐字段读：`BitConverter.ToInt32(byte[], int)` 不接受 Span，这里不复制整包。
        return new(
            body[0],
            BitConverter.ToInt32(body.Slice(AmountOffset, 4)),
            BitConverter.ToInt32(body.Slice(ListingIdOffset, 4)),
            BitConverter.ToUInt32(body.Slice(ExpiresEchoOffset, 4)),
            BitConverter.ToInt32(body.Slice(ItemTemplateIdOffset, 4)));
    }

    /// <summary>保留区（`[13..25]`）的十六进制：定案说它是全 0，但服务端只**记录**它、不据此拒绝。</summary>
    public string ReservedHex(ReadOnlySpan<byte> body) =>
        body.Length >= ReservedOffset + ReservedWidth ? Convert.ToHexString(body.Slice(ReservedOffset, ReservedWidth)) : "";
}

/// <summary>
/// CMD 334 `AUCTION_BUY_ITEM_APIECE`（单件/拆分购买堆叠挂牌）请求体。
/// 由 `ARAD.exe` `0x03701720`（`WriteOpcode(0x014E)` @ `0x037017F4`）反汇编定案，共 **33 字节**（无前导字节）：
///   `[0..3]` = `TotalPrice:i32`（`UnitPrice * Count`）、`[4..7]` = `Count:i32`（购买数量）、
///   `[8..11]` = `ListingId:i32`、`[12..15]` = `ExpiresAtUnix:i32`、
///   `[16..28]` = `13B` 保留区、`[29..32]` = `ItemTemplateId:i32`。
/// </summary>
internal sealed record AuctionBuyApieceRequest(int Amount, int Count, int ListingId, uint ExpiresAtUnix, int ItemTemplateId)
{
    public const int BodySize = 33;
    public const int AmountOffset = 0;
    public const int CountOffset = 4;
    public const int ListingIdOffset = 8;
    public const int ExpiresEchoOffset = 12;
    public const int ReservedOffset = 16;
    public const int ReservedWidth = 13;
    public const int ItemTemplateIdOffset = 29;

    public static AuctionBuyApieceRequest Parse(ReadOnlySpan<byte> body)
    {
        if (body.Length < BodySize)
            throw new InvalidDataException($"CMD 334 请求体至少需要 {BodySize} 字节（ARAD.exe 0x03701720 固定 33B）：{body.Length}");
        return new(
            BitConverter.ToInt32(body.Slice(AmountOffset, 4)),
            BitConverter.ToInt32(body.Slice(CountOffset, 4)),
            BitConverter.ToInt32(body.Slice(ListingIdOffset, 4)),
            BitConverter.ToUInt32(body.Slice(ExpiresEchoOffset, 4)),
            BitConverter.ToInt32(body.Slice(ItemTemplateIdOffset, 4)));
    }

    public string ReservedHex(ReadOnlySpan<byte> body) =>
        body.Length >= ReservedOffset + ReservedWidth ? Convert.ToHexString(body.Slice(ReservedOffset, ReservedWidth)) : "";
}

/// <summary>
/// 请求金额 `[1..4]` 命中的是哪一个口径。**两个样本的数量都是 1，单价与总额同值 ⇒ 现有样本区分不了**；
/// 这一项就是「第三个样本（数量 &gt; 1 的挂牌）一次判定」要读的字段：
/// 命中 <see cref="UnitPrice"/> 说明客户端发的是单价，命中 <see cref="TotalPrice"/> 说明是总额，
/// 命中 <see cref="UnitEqualsTotal"/> 说明这一笔仍然区分不了（数量 = 1）。
/// </summary>
internal enum AuctionBuyAmountBasis
{
    /// <summary>与库里的单价、总额都不等 ⇒ 按「请求金额不符」拒绝（静默码 `0x7e`）。</summary>
    None,

    /// <summary>`[1..4]` == `buyout_price`（单价口径）。</summary>
    UnitPrice,

    /// <summary>`[1..4]` == `buyout_price × item_count`（总额口径）。</summary>
    TotalPrice,

    /// <summary>数量 = 1：单价与总额同值，命中哪一个无法区分（方案要求把这种情况单独标出来）。</summary>
    UnitEqualsTotal
}

/// <summary>
/// 一口价购买被拒绝的原因。**每一条都要能映射到客户端认识的白名单错误码**
/// （复用 183 那张表，见 <see cref="AuctionRegisterErrors.Whitelist"/>）：
/// 客户端把未知 errcode 当成成功，因此不允许出现没有映射的取值。
/// </summary>
internal enum AuctionBuyError
{
    None = 0,

    /// <summary>请求字段非法：不足 30 字节、挂牌 id 非正、金额非正。</summary>
    InvalidRequest,

    /// <summary>挂牌不存在（客户端窗口里那行已经没了）。</summary>
    NotFound,

    /// <summary>挂牌已不在售（已成交 / 已下架 / 已过期状态位）。</summary>
    NotActive,

    /// <summary>挂牌已过期（`expires_at_unix` 已过，状态位还没流转）。</summary>
    Expired,

    /// <summary>一口价为 `-1`（纯竞拍）：**竞价不实现**，不得按一口价成交。</summary>
    AuctionOnly,

    /// <summary>请求金额 `[1..4]` 与库里的单价、总额都不符（成交价一律取库里的值，绝不采信请求值）。</summary>
    AmountMismatch,

    /// <summary>`[26..29]` 的物品模板 id 与库内挂牌不一致（客户端那行是旧的或包里字段错位）。</summary>
    TemplateMismatch,

    /// <summary>买自己的挂牌。</summary>
    SelfBuy,

    /// <summary>买方金币不足。</summary>
    InsufficientGold,

    /// <summary>挂牌数据不可用：模板解析失败 / 条目快照读不回 / 金额溢出 / 托管文本超长。</summary>
    Unavailable,

    /// <summary>提交时挂牌内容或买方快照已变化（陈旧计划，不自动重试、不重新扣费）。</summary>
    StalePlan,

    /// <summary>条件更新影响 0 行：**同一挂牌被买走 / 重复点击 / 并发第二笔**（整体回滚，零副作用）。</summary>
    RaceLost
}

/// <summary>
/// ★ **CMD 185 失败应答使用的错误码**。与 183/184 共用同一张客户端白名单，取值只有三档
/// （不允许出现第四档，见 <see cref="AuctionBuyErrors.ToClientCode"/>）：
///   - 静默 `0x7e`：**操作前提已经没了** —— 不存在 / 非在售 / 已过期 / 请求金额不符 / 一口价 `-1` /
///     条件更新 0 行（被别人买走、重复点击）；
///   - 通用 `0xd2`：**必须让玩家处理或知情** —— 金币不足、数据不可用、金额溢出、陈旧计划、
///     以及请求体不完整这类无法解释给玩家的情形（与 183/184 对不完整请求的做法一致）；
///   - `0x69`：**只用于「买自己的挂牌」**（参考实现里该码就是这条语义，**D95 未实测其文本**）。
/// ⚠ 185 回调自己的 errcode 分派表没有本客户端实测（183 那张表是反汇编 `0x1126cd0` 得到的），
/// 沿用同一张表是保守选择：白名单里的码至少不会走进「未知码 = 成功」的分支 —— 那会让界面假装成交
/// 而钱货没动，比什么都不回更糟。
/// </summary>
internal static class AuctionBuyErrors
{
    /// <summary>通用提示框（客户端给 `0xd2` 配的是「安全模式」文案）。</summary>
    public const byte Generic = AuctionRegisterErrors.Generic;

    /// <summary>静默收尾：只刷新窗口、不弹框。</summary>
    public const byte Silent = AuctionRegisterErrors.Silent;

    /// <summary>`0x69`：参考实现里就是「不能买自己的挂牌」。</summary>
    public const byte SelfListing = 0x69;

    public static byte ToClientCode(AuctionBuyError error) => error switch
    {
        AuctionBuyError.NotFound or AuctionBuyError.NotActive or AuctionBuyError.Expired or
            AuctionBuyError.AuctionOnly or AuctionBuyError.AmountMismatch or AuctionBuyError.RaceLost => Silent,
        AuctionBuyError.SelfBuy => SelfListing,
        AuctionBuyError.None => throw new InvalidDataException("成功没有错误码"),
        _ => Generic
    };
}

/// <summary>
/// 一封**托管邮件**（跨账号收件行，与系统邮件同一行形状）。
///
/// 收件人只带**角色 id** 而不是 <c>MailboxRecipient</c>：解析必须在购买事务内按 id 重查
/// （邮件表有指向 `characters(id,account)` 的外键，「卖方角色行是否存在」不能靠规划期的快照判断，
/// 见 <see cref="AuctionListingBuyResult.Custody"/> 与失败矩阵）。
/// <see cref="Required"/> = true 表示这封必须投出去（买方的道具），投不出去整笔回滚；
/// false 表示收件人可能是机器人卖家或已被删除的角色，投不出去就跳过并记日志（**成交照常**）。
/// </summary>
internal sealed record AuctionCustodyMail(
    string Key,
    long RecipientCharacterId,
    bool Required,
    string SenderName,
    string Text,
    uint Gold,
    ImmutableArray<InventoryItem> Items,
    long Created,
    long? Expires)
{
    /// <summary>
    /// 邮件表对发件人名（29 字节）与正文（511 字节）都有字节上限。**在规划期就校验**：
    /// 超限时按「数据不可用」拒绝并把是哪一段写进日志，而不是让那次托管投递在事务里抛出去
    /// —— 那会把整笔成交连同买方扣款一起回滚，玩家看到的是「点了没反应」。
    /// 编码口径与 <c>CharacterRepository.DeliverMail</c> 相同（客户端代码页 936）。
    /// 返回 null 表示两个字段都可用，否则返回拒绝原因。
    /// </summary>
    public static string? TextTooLong(string senderName, string text)
    {
        try
        {
            if (CharacterName.Encoding.GetByteCount(senderName) > 29)
                return "卖方名超过邮件发件人名的 29 字节上限";
            if (CharacterName.Encoding.GetByteCount(text) > 511)
                return "托管邮件正文超过 511 字节上限";
            return null;
        }
        catch (EncoderFallbackException error)
        {
            // 名字/正文里出现客户端代码页表示不了的字符：邮件投递同样会失败，这里先按不可用拒绝。
            return "托管邮件文本无法按客户端代码页编码：" + error.Message;
        }
    }
}

/// <summary>
/// 一口价购买的**只读规划**：定位并存下成交所需的一切（库内权威金额、卖方、两封托管邮件的内容、
/// 买方扣款计划），提交时由 <see cref="AuctionService.Buy"/> 把「买方扣款 + 挂牌置为已成交 + 托管行」
/// 放进**同一个外层事务**。
///
/// <see cref="AmountBasis"/> / <see cref="RequestedAmount"/> / <see cref="UnitPrice"/> / <see cref="TotalPrice"/>
/// 一起进日志：它们就是「第三个样本（数量 &gt; 1 的挂牌）」一次判定单价/总额所需的全部字段。
/// </summary>
internal sealed record AuctionBuyPlan(
    int ListingId,
    int ItemTemplateId,
    byte ItemKind,
    int ItemCount,
    long? SellerCharacterId,
    string SellerName,
    int UnitPrice,
    long TotalPrice,
    long SellerProceeds,
    AuctionBuyAmountBasis AmountBasis,
    int RequestedAmount,
    uint RequestExpiresAtUnix,
    bool ExpiresEchoMatches,
    ImmutableArray<AuctionCustodyMail> Custody,
    InventoryPlan Inventory,
    long SettledAtUnix,
    long ExpiresAtUnix)
{
    public ImmutableArray<byte> SourceItemEntry { get; init; } = [];
    public int PurchaseCount { get; init; }
    public int EffectivePurchaseCount => PurchaseCount > 0 ? PurchaseCount : ItemCount;
    public int RemainingCount => ItemCount - EffectivePurchaseCount;
    public ImmutableArray<byte> RemainingItemEntry { get; init; } = [];
}

/// <summary>规划结果：失败也返回同一种类型（`Error` 非 None、`Options` 为 null），并带上进日志的原因。</summary>
internal sealed record AuctionBuyDecision(AuctionBuyError Error, AuctionBuyPlan? Options, string? Detail = null)
{
    public static AuctionBuyDecision Ok(AuctionBuyPlan plan) => new(AuctionBuyError.None, plan);

    public static AuctionBuyDecision Reject(AuctionBuyError error, string? detail = null) => new(error, null, detail);
}

/// <summary>一封托管邮件的投递结果（进日志：投到哪个角色、多少钱、有没有因为收件人缺失被跳过）。</summary>
internal readonly record struct AuctionCustodyReceipt(string Key, long RecipientCharacterId, uint Gold, bool Delivered, string? Note);

/// <summary>
/// 购买提交的结果。`Character` 是提交后的权威快照（revision 已前进、金币已扣），
/// 写库失败则整体抛出、不返回成功（与上架/下架同一口径）。
/// <see cref="Custody"/> 记录两封托管行各自的结果：**「卖方角色行不存在」是合法结果**（跳过金币托管、
/// 成交照常），必须能从结果里分辨出来，而不是靠猜。
/// </summary>
internal sealed record AuctionListingBuyResult(AuctionBuyError Error, Character? Character, string? Detail = null)
{
    public ImmutableArray<AuctionCustodyReceipt> Custody { get; init; } = [];

    public static AuctionListingBuyResult Reject(AuctionBuyError error, string? detail = null) => new(error, null, detail);
}

/// <summary>
/// 挂牌状态条件更新（`state=0 → 1`）影响 0 行时抛出的异常：**同一挂牌已经被别人买走或本次是重复点击**。
///
/// 为什么用异常而不是返回码：条件更新为 0 行时必须让**整个事务回滚**（钱、挂牌状态、托管行都是
/// 跨账号资产，半成品只能人工补偿），而抛出是本仓库既有的回滚方式（见 184 的
/// <c>AuctionListingRepository.MarkCancelled</c>）。<see cref="AuctionService.Buy"/> 只捕获**这一个类型**
/// 并翻成静默码 `0x7e`（幂等语义），其它异常照旧向上抛给协议层。
/// </summary>
internal sealed class AuctionBuyRaceLostException(string message) : InvalidOperationException(message);

// —— CMD 334 `AUCTION_BUY_ITEM_APIECE`：**仍然是请求抓取桩** ——
//
// 334 的布局**没有**任何本客户端证据：参考实现自己标注「残栈传参使前导字节与挂牌 id 的精确偏移
// 无法 100% 静态确定」。两个实机样本证实「购买」按钮发的是 185（那里 `[26..29]` 才是物品模板 id，
// 334 的字段顺序尚未验证），因此 334 保持原样：只把请求体与试探读数写进日志，不回任何应答。

/// <summary>
/// 一个**试探性**字段读数。<see cref="Complete"/> 为假表示包体不够长、该字段根本读不到：
/// **不猜、不补 0**，日志里直接标成缺失，避免把「偏移不对」误读成「客户端发了 0」。
/// </summary>
internal sealed record AuctionBuyField(string Name, int Offset, int Width, bool Complete, long? Value, string Raw, string Note);

/// <summary>
/// 包体前 16 个 4 字节对齐位置上的 i32 读值（与参考实现的 `off0..15` 探测打印同款）。
/// 用途：**离线**把 D95 的真实请求体与参考布局逐字段对照 —— 只要某个候选偏移在这里能读出
/// 与界面一致的数字（金额、挂牌 id、数量），布局差异就能一次看清，不必再轮次试包。
/// </summary>
internal sealed record AuctionBuyWindow(int Offset, long Value);

/// <summary>
/// 一次 334 请求的**解析尝试**结果（不表达任何业务语义，也不产生任何状态变更）。
/// <see cref="Fields"/> 里的每一个读数都是**试探**：布局来自另一客户端版本的参考实现，在 D95 上未验证。
/// </summary>
internal sealed record AuctionBuyProbe(
    AuctionBuyCommand Command,
    string Layout,
    string Evidence,
    int ReferenceSize,
    int BodySize,
    bool BodyComplete,
    ImmutableArray<AuctionBuyField> Fields,
    ImmutableArray<AuctionBuyWindow> Windows,
    string TailHex,
    string Note);

/// <summary>
/// CMD 334 请求体的**参考布局探测**。本类只做只读的字节解释，**不扣钱、不发货、不改任何状态**，
/// 也不判断请求是否合法 —— 一旦实机点出 334 的真实请求体，就能用这份读数离线锁定它的偏移。
///
/// ★ 185 的布局已由两个样本定案（见 <see cref="AuctionBuyRequest"/>），因此**不再走试探**：
/// 它按定案布局严格解析，字段读数直接来自解析结果（少一份「同一偏移两处定义」的漂移风险）。
///
/// 布局来源（仓库外 `_review/06-拍卖行协议参考-S4A21.md` §3.10，**未在 D95 验证**）：
/// 前导 + 总价 = 单价×数量 + 数量 + 挂牌 id 8 字节（行结构 `row[0]`/`row[4]`）+ 13B 字符串 + 4B 道具标识，
/// 默认从 `body[9]` 读挂牌 id；前导字节的精确位置未定 ⇒ 后面所有偏移都只能算候选。
/// </summary>
internal static class AuctionBuyProbes
{
    /// <summary>334 参考布局的**长度下界**：前导 1 + 总价 4 + 数量 4 + 挂牌 id 8 + 13B 字符串 + 4B 道具标识 = 34。</summary>
    public const int BuyItemReferenceSize = 34;

    /// <summary>窗口读值的条数上限：与参考实现打印 `off0..15` 一致，不随包体长度增长（日志有界）。</summary>
    public const int WindowCount = 16;

    public static int ReferenceSize(AuctionBuyCommand command) =>
        command == AuctionBuyCommand.Bidding ? AuctionBuyRequest.BodySize : BuyItemReferenceSize;

    public static string LayoutName(AuctionBuyCommand command) =>
        command == AuctionBuyCommand.Bidding ? "settled-185-buy-30" : "reference-334-buy-34";

    /// <summary>布局出处，写进日志后可以直接拿去对照参考实现的哪一节，不必再回忆来源。</summary>
    public static string Evidence(AuctionBuyCommand command) => command == AuctionBuyCommand.Bidding
        ? "D95 两样本差分定案（30B：前导+一口价+挂牌id+到期回传+13B保留+模板id）"
        : "参考实现 §3.10（前导+总价+数量+挂牌id8+13B串+道具id，前导与挂牌id偏移未定），D95 未验证";

    /// <summary>
    /// 按参考布局读出 334 的试探字段。**只有 334 会走到这里**：185 的布局已定案，
    /// 处理函数按 <see cref="AuctionBuyRequest.Parse"/> 严格解析，不在这里再定义一份偏移。
    /// </summary>
    public static AuctionBuyProbe Describe(AuctionBuyCommand command, ReadOnlySpan<byte> body)
    {
        if (command == AuctionBuyCommand.Bidding)
            throw new InvalidOperationException("185 的布局已定案，不再走试探读数（见 AuctionBuyRequest）");
        int reference = BuyItemReferenceSize;
        // 334：同一张表的字段顺序，但前导字节位置未定 ⇒ 后面所有偏移都只能算候选，逐字段标注「未定」。
        ImmutableArray<AuctionBuyField> fields =
        [
            Byte(body, "Leading", 0, "参考实现 §3.10：前导字节，精确偏移未定（这里按 [0] 试探）"),
            Int32(body, "TotalPrice", 1, "参考实现 §3.10：总价 = 单价×数量（偏移未定，随前导位置浮动）"),
            Int32(body, "Count", 5, "参考实现 §3.10：购买数量（偏移未定，随前导位置浮动）"),
            Int32(body, "ListingIdLow", 9, "参考实现 §3.10 默认读数：挂牌 id 低 4 位（行结构 `row[0]`，偏移未定）"),
            Int32(body, "ListingIdHigh", 13, "参考实现 §3.10：挂牌 id 高 4 位（行结构 `row[4]`，偏移未定）"),
            Blob(body, "ItemKeyString", 17, 13, "参考实现 §3.10：13B 字符串 `@[ecx+0x36d]`（偏移未定）"),
            Int32(body, "ItemKey", 30, "参考实现 §3.10：4B 道具标识 `@[ecx+0x383]`（偏移未定）")
        ];
        var windows = ImmutableArray.CreateBuilder<AuctionBuyWindow>();
        for (int offset = 0; offset < WindowCount && offset + 4 <= body.Length; offset++)
            windows.Add(new(offset, BitConverter.ToInt32(body.Slice(offset, 4))));
        int tailStart = Math.Min(reference, body.Length);
        return new(command, LayoutName(command), Evidence(command), reference, body.Length, body.Length >= reference,
            fields, windows.ToImmutable(), Convert.ToHexString(body[tailStart..]),
            body.Length >= reference
                ? "按参考布局逐字段读到的值都是**试探**，不代表服务端承认该请求"
                : $"包体短于参考布局（{body.Length} < {reference}），读不到的字段保持缺失，不补 0、不猜测");
    }

    static AuctionBuyField Byte(ReadOnlySpan<byte> body, string name, int offset, string note) => offset + 1 <= body.Length
        ? new(name, offset, 1, true, body[offset], Convert.ToHexString(body.Slice(offset, 1)), note)
        : new(name, offset, 1, false, null, "", note);

    static AuctionBuyField Int32(ReadOnlySpan<byte> body, string name, int offset, string note) => offset + 4 <= body.Length
        ? new(name, offset, 4, true, BitConverter.ToInt32(body.Slice(offset, 4)), Convert.ToHexString(body.Slice(offset, 4)), note)
        : new(name, offset, 4, false, null, "", note);

    static AuctionBuyField Blob(ReadOnlySpan<byte> body, string name, int offset, int width, string note) => offset + width <= body.Length
        ? new(name, offset, width, true, null, Convert.ToHexString(body.Slice(offset, width)), note)
        : new(name, offset, width, false, null, "", note);
}
