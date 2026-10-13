namespace DfoServer.Game.Auction;

/// <summary>
/// 「我的拍卖品」三个页签。数值就是 <c>CmdPacketType</c> 里的 opcode（188/189/190），
/// 这里自带一份是为了让 Game 层不引用 Network，同时让候选选择与日志按「哪个页签」而不是裸数字表达。
/// </summary>
internal enum AuctionMyListPanel : byte
{
    /// <summary>188 `AUCTION_MY_REGISTED_ITEM_INFO`：「我的拍卖品 → 上架的物品」。</summary>
    Registed = 188,

    /// <summary>189 `AUCTION_MY_BIDDING_INFO`：「我的拍卖品 → 我的竞价」。</summary>
    Bidding = 189,

    /// <summary>190 `AUCTION_MY_AUCTION_HISTORY`：「拍卖历史」。</summary>
    History = 190
}

/// <summary>
/// 「我的拍卖品」应答（CMD 188/189/190）的记录布局候选。
///
/// **为什么是候选**：参考实现（另一个客户端版本，`_review/06-拍卖行协议参考-S4A21.md` §4.5/§4.6/§4.7）
/// 给出 188 = 151B 记录（道具块 @30、count 1B）、189 = 159B 记录（道具块 @38、count 2B WORD）、
/// 190 自标「仍是抓包桩、按 188 镜像」；**这些量在 D95 全部未验证**。
/// 因此尺寸、计数宽度、道具块偏移都做成候选，由 `--auction-probe mylist=` 轮换，日志记下本次用的是哪一组；
/// 与 186 搜索「168B 记录」的已定案合同一样，最终由实机界面收敛。
///
/// **安全底线**（两道，见 <see cref="IsSafe"/> 与 <c>AuctionPackets.MyListReply</c> 的编码自检）：
///   ① 记录必须放得下 83B 道具块与 16B 头部字段（`RecordSize ≥ ItemOffset + 83`、`ItemOffset ≥ 16`），
///      有剩余秒数字段时还要放得下它（`RemainOffset + 4 ≤ RecordSize`）；
///   ② 应答体**不得短于空列表所需的头部**（`flag + mode + count` = 3 或 4 字节）：短体在 182 上已经
///      实测把客户端读崩（1B ACK），拍卖行一律不允许出现。
/// 另外 `flag` 必须是应答体的**第 0 字节**（本仓库帧层不补这一字节，见[拍卖行协议]），
/// 编码器与自测都把这一条钉死。
///
/// 记录内字段（参考实现同一条逆向，188/190 与 189 只差三个量：记录尺寸、道具块偏移、计数宽度）：
///   `[0..3]` 挂牌 id、`[4..7]` 到期时间、`[8..11]` **单价**（= 界面价格列）、`[12..15]` 一口价总额、
///   `[16..28]` 13B null 结尾字符串（保持 0）、`[ItemOffset..]` 83B 道具块、`[RemainOffset..]` 剩余秒数。
/// 189 另有 `[37]` 出价状态字节（本仓库未接入竞价，不写）。
///
/// ⚠ 客户端跳过判定（参考实现 §4.5）：`wire[0] | wire[4]` **同时为 0 时整行被隐藏**，
/// 因此每条记录至少要有一个非 0 —— 真实挂牌的 `listing_id` 是库里的自增主键（>0），天然满足；
/// 自测另有一条断言锁定这一点，避免以后有人用 0 号假行做种子。
/// </summary>
internal sealed record AuctionMyListReply(
    string Name,
    int RecordSize,
    int ItemOffset,
    int CountWidth,
    int RemainOffset,
    bool HeadOnly,
    string Note)
{
    /// <summary>道具块宽度。与 <c>AuctionPackets.ItemBlockSize</c> 同值，自测断言两者一致（跨层不漂移）。</summary>
    public const int ItemBlockSize = 83;

    /// <summary>普通拍卖行窗口（`type 7`）：「我的拍卖品」页签就在这个窗口里。</summary>
    public const byte NormalWindowMode = 0x00;

    /// <summary>金币寄售窗口（`type 8`）：本仓库尚未接入该面板。</summary>
    public const byte ConsignmentWindowMode = 0x01;

    /// <summary>记录内字段偏移（与候选无关的固定部分）。</summary>
    public const int ListingIdOffset = 0;
    public const int ExpiresOffset = 4;
    public const int PriceOffset = 8;
    public const int BuyoutOffset = 12;

    /// <summary>应答头：`flag(1B) + mode(1B) + count(1..2B)`，其中 flag 与 mode 固定占前 2 字节。</summary>
    public const int FixedHeaderSize = 2;

    /// <summary>参考实现 188/190：151B 记录、道具块 @30、count 1B、剩余秒数 @143。</summary>
    public static readonly AuctionMyListReply Registed151 =
        new("a21-151", 151, 30, 1, 143, false, "参考实现 §4.5：151B 记录、道具块 @30、count 1B、剩余秒数 @143（D95 未验证）");

    /// <summary>同上但不写剩余秒数：分辨「剩余时间字段」是否是整行读不出的原因。</summary>
    public static readonly AuctionMyListReply Registed151NoRemain =
        new("a21-151-noremain", 151, 30, 1, -1, false, "151B 记录但剩余秒数保持 0：对照「剩余时间字段写坏整行」这一假设");

    /// <summary>参考实现 189：159B 记录、道具块 @38、count 2B WORD、剩余秒数 @151。</summary>
    public static readonly AuctionMyListReply Bidding159 =
        new("a21-159", 159, 38, 2, 151, false, "参考实现 §4.6：159B 记录、道具块 @38、count 2B WORD、剩余秒数 @151（D95 未验证）");

    /// <summary>镜像 186 搜索合同的 168B 记录（道具块 @43）：分辨 188/189 是否与搜索共用同一套记录。</summary>
    public static readonly AuctionMyListReply Mirror168 =
        new("mirror-168", 168, 43, 1, -1, false, "镜像 186 搜索合同：168B 记录、道具块 @43、count 1B（若 188 与搜索共用一套记录）");

    /// <summary>同上但计数用 WORD：189 在「与搜索共用记录」假设下的对照。</summary>
    public static readonly AuctionMyListReply Mirror168Word =
        new("mirror-168-word", 168, 43, 2, -1, false, "镜像 186 记录 + count 2B WORD：189 在「共用搜索记录」假设下的对照");

    /// <summary>
    /// 哨兵候选：只回 `flag + mode + count = 0`，**故意不发行记录**。
    /// 用途是把「页签打开没反应（应答通道/头部不对）」与「记录读不出来（布局不对）」分开：
    /// 用它能打开空面板、换成真实候选就是空的 ⇒ 问题在记录布局。
    /// </summary>
    public static readonly AuctionMyListReply HeadOnlySentinel =
        new("head-only", 0, -1, 1, -1, true, "哨兵：只回 flag+mode+count=0（故意不发行记录），用于分辨通道与记录布局");

    public static readonly IReadOnlyList<AuctionMyListReply> All =
        [Registed151, Bidding159, Registed151NoRemain, Mirror168, Mirror168Word, HeadOnlySentinel];

    /// <summary>
    /// 页签的默认候选（探针关闭时使用）：188/190 按参考实现的 151B，189 按 159B。
    /// 190 参考实现自标「按 188 镜像」，所以与 188 同候选；不确定项与依据见类型注释。
    /// </summary>
    public static AuctionMyListReply DefaultFor(AuctionMyListPanel panel) => panel switch
    {
        AuctionMyListPanel.Bidding => Bidding159,
        AuctionMyListPanel.Registed or AuctionMyListPanel.History => Registed151,
        _ => throw new InvalidDataException("未知的拍卖行页签：" + panel)
    };

    /// <summary>空列表也必须发满的体长：`flag + mode + count`。</summary>
    public int MinimumBodyBytes => FixedHeaderSize + CountWidth;

    /// <summary>计数容量：count 是 u8 或 u16，由此决定一次最多能带几条记录。</summary>
    public int CountCapacity => CountWidth switch
    {
        1 => byte.MaxValue,
        2 => ushort.MaxValue,
        _ => 0
    };

    /// <summary>
    /// 本次实际写进 count 的记录条数（哨兵候选为 0，其余按计数宽度限幅）。
    /// **编码器与日志共用这一处**：日志里的 count 因此永远是真正下发的那一个，不会出现「日志说 3 条、包体写 0 条」。
    /// </summary>
    public int CountFor(int available) => HeadOnly ? 0 : Math.Min(Math.Max(available, 0), CountCapacity);

    /// <summary>带 N 条记录时的应答体长度（checked：候选写错时宁可抛异常，也不发出长度错位的包）。</summary>
    public int BodyLength(int count) => checked(MinimumBodyBytes + count * RecordSize);

    /// <summary>
    /// 安全底线（第一道）：候选自身必须自洽，否则不允许进候选表、也不允许编码。
    /// 判据见类型注释：记录要放得下头部字段与整个道具块，计数宽度只能是 1 或 2，
    /// 有剩余秒数字段时还要放得下 4 字节。
    /// </summary>
    public bool IsSafe => HeadOnly
        ? CountWidth is 1 or 2
        : CountWidth is 1 or 2 && ItemOffset >= 16 && RecordSize >= ItemOffset + ItemBlockSize &&
            (RemainOffset < 0 || RemainOffset + 4 <= RecordSize);

    /// <summary>
    /// 该候选的记录必须带可见的首字段（哨兵候选不发行记录，因此不适用）。
    /// 用于编码器挡住「两个首字段同时为 0 的占位行」——那种行客户端会静默隐藏，看起来像「布局没对」，
    /// 实际是行本身不该发。
    /// </summary>
    public bool KeepsRowVisible => !HeadOnly;

    /// <summary>一条记录的两个首字段是否至少有一个非 0（客户端据此决定是否隐藏整行）。</summary>
    public static bool RowVisible(int listingId, long expiresAtUnix) => listingId != 0 || expiresAtUnix != 0;

    public static AuctionMyListReply? Find(string name)
    {
        foreach (var candidate in All)
        {
            if (string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase))
                return candidate;
        }
        return null;
    }

    public static string Names => string.Join('/', All.Select(candidate => candidate.Name));

    /// <summary>
    /// 安全校验：候选为 null、或自身不自洽（<see cref="IsSafe"/> 为假）时抛异常。
    /// 与均价应答一样是「宁可少发一个候选，也不发一个会读崩客户端的包」。
    /// </summary>
    public static void Validate(AuctionMyListReply? layout)
    {
        if (layout is null)
            throw new InvalidDataException("拍卖行「我的拍卖品」应答缺少记录布局候选");
        if (!layout.IsSafe)
            throw new InvalidDataException(
                $"「我的拍卖品」候选 {layout.Name} 不安全：记录 {layout.RecordSize}B、道具块 @{layout.ItemOffset}、计数 {layout.CountWidth}B");
    }
}

/// <summary>
/// 「我的拍卖品」（CMD 188/189/190）的请求：**请求体恒 1 字节 = 窗口 `mode`**（0 = 普通拍卖行、1 = 金币寄售）。
///
/// 空请求体解析不出 `mode`（参考实现遇到空体时保守回退 `1`，那是它自己那份客户端的取舍）：
/// 本仓库的「我的拍卖品」页签在**普通拍卖行窗口**里，因此回退 `0` 并标记 <see cref="Fallback"/>，
/// 让日志能分辨「客户端这一版发的是空体」与「客户端发的就是 0」。
/// 其余取值原样带回（应答回显请求首字节本身就是约定，是否认识该值由客户端决定）。
/// </summary>
internal readonly record struct AuctionMyListRequest(byte Mode, bool Fallback)
{
    public static AuctionMyListRequest Parse(ReadOnlySpan<byte> body) => body.Length >= 1
        ? new(body[0], false)
        : new(AuctionMyListReply.NormalWindowMode, true);
}
