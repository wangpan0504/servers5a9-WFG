using System.Collections.Immutable;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Game.Auction;

/// <summary>
/// CMD 183 `AUCTION_REGIST_ITEM`（上架）请求体。**布局已在匹配客户端（D95）实测确认**，不再是候选。
///
/// 实测样本（点击「开始拍卖」发出；界面值：itemId 10400 残缺的牛皮背心、数量 1、起拍价空、一口价 17000、24 小时），
/// 共 **37 字节**：
/// <code>
/// 00 00 1B 00 A0 28 00 00 01 00 00 00 FF FF FF FF 68 42 00 00 68 42 00 00 18 00 00 00 00 * 9
/// </code>
///
/// | 偏移 | 宽度 | 含义 |
/// | --- | ---: | --- |
/// | `[0]` | 1 | 前导 0（与 184 的 `Leading` 同款） |
/// | `[1]` | 1 | 源列表类型（**推断**：与库存协议其余部分一致的 byte 列表类型） |
/// | `[2..3]` | u16 LE | 源槽位（**推断**） |
/// | `[4..7]` | i32 LE | 物品模板 id |
/// | `[8..11]` | i32 LE | 数量 |
/// | `[12..15]` | i32 LE | 起拍价，**空 = -1** |
/// | `[16..19]` | i32 LE | 一口价 |
/// | `[20..23]` | i32 LE | 一口价回显（与 `[16..19]` 同值） |
/// | `[24..27]` | i32 LE | 上架时长（小时） |
/// | `[28..]` | 可变 | 尾部补 0：本客户端 9 字节（共 37），参考实现 28 + 9 字节 |
///
/// ★ **`[1]` / `[2..3]` 的语义只是推断**（参考实现由 `byte [esi+0x188]` / `word [esi+0x184]` 写出，标注为推断）。
/// 因此这里把它们当作**线索**而不是权威：服务端拿到后必须用 `[4..7]` 的 itemId 与槽位实际内容**交叉校验**，
/// 对不上就回退到「按 itemId 扫描来源列表」，并在日志里暴露差异 —— 绝不能按推断的槽位直接扣道具。
///
/// ★ **尾部长度按版本容错**：主体必须 ≥ <see cref="BodySize"/>（28）字节才解析，尾部字节（本客户端 9 个 0）
/// 一律忽略并原样记进日志；不足 28 字节则明确失败，绝不按残缺字段扣道具。
/// </summary>
internal sealed record AuctionRegisterRequest(
    byte Leading,
    byte SourceList,
    ushort SourceSlot,
    int ItemTemplateId,
    int ItemCount,
    int StartingPrice,
    int BuyoutPrice,
    int BuyoutEcho,
    int DurationHours)
{
    /// <summary>已确认的请求体主体长度：前导(1) + 列表类型(1) + 槽位(2) + itemId(4) + 数量(4) + 起拍价(4) + 一口价(4) + 回显(4) + 时长(4)。</summary>
    public const int BodySize = 28;

    /// <summary>
    /// 解析 183 请求体。**主体不足 <see cref="BodySize"/> 字节即拒绝**（抛 <see cref="InvalidDataException"/>）：
    /// 拿不到完整字段时无法安全定位源槽与价格，不能退回「按 itemId 猜一件扣掉」。
    /// 返回值只做字段切分，不含任何业务判断。
    /// </summary>
    public static AuctionRegisterRequest Parse(ReadOnlySpan<byte> body)
    {
        if (body.Length < BodySize)
            throw new InvalidDataException($"CMD 183 请求体主体不足 {BodySize} 字节（实测 37 字节）：{body.Length}");
        return new(
            body[0],
            body[1],
            (ushort)(body[2] | (body[3] << 8)),
            BitConverter.ToInt32(body[4..8]),
            BitConverter.ToInt32(body[8..12]),
            BitConverter.ToInt32(body[12..16]),
            BitConverter.ToInt32(body[16..20]),
            BitConverter.ToInt32(body[20..24]),
            BitConverter.ToInt32(body[24..28]));
    }

    /// <summary>
    /// 两个价格字段是否一致。<see cref="BuyoutEcho"/> 是客户端把一口价**回显**了一遍（参考实现 `[ebp-4]` 副本），
    /// 正常情况下与 <see cref="BuyoutPrice"/> 同值；不一致说明客户端版本或界面状态与本端假设不同，
    /// 需要记日志暴露（服务端以 `[16..19]` 为准，与参考实现的写入顺序一致）。
    /// </summary>
    public bool BuyoutEchoMatches => BuyoutEcho == BuyoutPrice;
}

/// <summary>
/// 上架计划：源槽定位、扣减后的背包快照与待写入的挂牌。由只读规划
/// （<see cref="AuctionService.PlanRegister"/>）产生，提交（<see cref="AuctionService.Register"/>）时
/// 与背包扣减**同事务**写入。
///
/// <see cref="FallbackReason"/> 非空表示 `[1]`/`[2..3]` 的推断槽位与实际内容对不上，改走了
/// 「按 itemId 扫描来源列表」的回退路径；这是**必须记日志**的情形，因为它是「推断字段语义是否正确」的唯一证据。
/// </summary>
internal sealed record AuctionRegisterPlan(
    InventorySlot Source,
    byte SourceList,
    ushort SourceSlot,
    int ItemTemplateId,
    byte ItemKind,
    int ItemCount,
    byte[] ItemEntry,
    int StartingPrice,
    int BuyoutPrice,
    int DurationHours,
    long PostedAtUnix,
    long ExpiresAtUnix,
    InventoryPlan Inventory,
    string? FallbackReason)
{
    /// <summary>挂牌时长（秒），由小时换算，供到期时间核对。</summary>
    public long DurationSeconds => (long)DurationHours * 3600;

    /// <summary>
    /// 本次挂牌已收的保证金（金币）：与 <see cref="Inventory"/> 里的金币扣减是同一个数，
    /// 落库后成为「这一行到期该退多少」的唯一依据（见 <see cref="AuctionListing.DepositGold"/>）。
    /// </summary>
    public int DepositGold { get; init; }
}

/// <summary>
/// 上架的可选参数（商城面板等其它上架入口的预留开关）。
/// 当前只有金币寄售面板需要区分，本仓库尚未接入该面板，因此枚举只有普通入口。
/// </summary>
internal enum AuctionRegisterChannel
{
    /// <summary>普通拍卖行（`type 7` 窗口）。</summary>
    Normal = 0
}

/// <summary>
/// 一次上架规划的结果。与邮箱的 `PlanSend` 同形：**规划失败也返回同一种结果类型**（`Error` 非 None、
/// `Options` 为 null），Handler 因此不需要 `out` 参数，也不需要把领域错误塞进库存结果。
/// </summary>
internal sealed record AuctionRegisterDecision(AuctionRegisterError Error, AuctionRegisterPlan? Options)
{
    /// <summary>成功：`Options` 一定非 null。</summary>
    public static AuctionRegisterDecision Ok(AuctionRegisterPlan plan) => new(AuctionRegisterError.None, plan);

    /// <summary>拒绝：不产生任何待提交变更。</summary>
    public static AuctionRegisterDecision Reject(AuctionRegisterError error) => new(error, null);
}

/// <summary>
/// 上架请求的**字段级**校验结果，供日志与自测断言使用：白名单外的时长回落到策略默认值时必须能区分
/// 「客户端原本发了什么」与「服务端实际用了什么」。
/// </summary>
internal readonly record struct AuctionDurationResolution(int Requested, int Applied, bool Fallback)
{
    /// <summary>时长白名单（小时）：客户端下拉只给这四档。</summary>
    public static readonly ImmutableArray<int> Allowed = [6, 12, 24, 48];

    /// <summary>白名单外的回落值：24 小时（客户端下拉的默认档）。</summary>
    public const int FallbackHours = 24;

    /// <summary>白名单外一律回落到默认时长，并让调用方记日志（不静默改值）。</summary>
    public static AuctionDurationResolution Resolve(int requested) => Allowed.Contains(requested)
        ? new(requested, requested, false)
        : new(requested, FallbackHours, true);
}
