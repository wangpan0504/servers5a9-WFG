namespace DfoServer.Game.Auction;

/// <summary>
/// 拍卖行搜索应答的记录布局候选值。
///
/// 已确认（匹配客户端实机）：应答走 **CMD 路径、opcode 186**，包体为
/// <c>flag + mode + 匹配总件数:i32 + 本批条数:u16 + N×记录</c>；客户端按每页 10 条
/// 用总件数算出总页数并显示「搜索出 N 个」。
///
/// ★ 仍未确认：**记录尺寸与道具块内容**。客户端能显示总件数却不渲染任何一行，
///   说明行被丢弃——最可能是道具块里 `itemId` 不在客户端读的位置（本仓库普通条目以
///   `slot:u16` 开头，而拍卖行块以物品种类开头）。因此提供两种块来源：
///   <see cref="LegacyCore"/> = 按参考实现的物品核心布局逐字段重排（kind@0、itemId@1、value@5、attr@9）；
///   否则直接从普通条目字节复制。
///
/// 记录内字段含义（候选，来自参考实现同一份逆向）：
///   [0..3] 挂牌 id 低位、[4..7] 到期时间、[9..12] 当前竞价、[13..16] 一口价总额
///   （纯竞拍必须写 -1）、[43..] 道具块、[156..] 装备类剩余秒数（仅 RemainOffset ≥ 0 时写）。
/// </summary>
internal sealed record AuctionReplyLayout(
    string Name,
    int RecordSize,
    int ItemOffset,
    int ItemSize,
    int EntryOffset,
    int SwapRegionOffset,
    int RemainOffset,
    bool LegacyCore)
{
    /// <summary>哨兵候选：不回记录，只回空列表头部（界面上与「没回复」无法区分，不作判据）。</summary>
    public static readonly AuctionReplyLayout Empty = new("empty", 0, 0, 0, 0, -1, -1, false);

    /// <summary>参考实现直接移植：168B 记录、道具块 @43 宽 83B、物品核心布局 + 反排、剩余秒数 @156。</summary>
    public static readonly AuctionReplyLayout A21 = new("a21", 168, 43, 83, 0, 6, 156, false);

    /// <summary>同上但不反排。</summary>
    public static readonly AuctionReplyLayout A21Plain = new("a21-plain", 168, 43, 83, 0, -1, 156, false);

    /// <summary>★ 按参考实现的物品核心布局逐字段重排（kind@0、itemId@1、value@5、attr@9）+ 拍卖行反排。</summary>
    public static readonly AuctionReplyLayout Legacy = new("legacy", 168, 43, 83, 0, 5, 156, true);

    /// <summary>物品核心布局但不反排。</summary>
    public static readonly AuctionReplyLayout LegacyPlain = new("legacy-plain", 168, 43, 83, 0, -1, 156, true);

    /// <summary>物品核心布局 + 记录更长（客户端记录可能随物品条目一起变长）。</summary>
    public static readonly AuctionReplyLayout Legacy208 = new("legacy-208", 208, 43, 83, 0, 5, 156, true);

    /// <summary>物品核心布局 + 中等长度记录。</summary>
    public static readonly AuctionReplyLayout Legacy176 = new("legacy-176", 176, 43, 83, 0, 5, 156, true);

    /// <summary>道具块直接用本仓库普通条目字节，不截断。</summary>
    public static readonly AuctionReplyLayout Entry = new("entry", 168, 43, 123, 0, -1, -1, false);

    /// <summary>条目字节 + 属性/数量反排（条目内 value@6、attr@10）。</summary>
    public static readonly AuctionReplyLayout EntrySwap = new("entry-swap", 168, 43, 123, 0, 6, -1, false);

    /// <summary>去掉条目开头的 slot:u16，只送物品核心（核心内 value@4、attr@8）。</summary>
    public static readonly AuctionReplyLayout Core = new("core", 168, 43, 121, 2, -1, -1, false);

    /// <summary>核心 + 属性/数量反排 + 剩余秒数。</summary>
    public static readonly AuctionReplyLayout CoreSwap = new("core-swap", 168, 43, 121, 2, 4, 156, false);

    /// <summary>按「记录更长」的假设拉开步长（条目字节）。</summary>
    public static readonly AuctionReplyLayout Stride176 = new("stride176", 176, 43, 123, 0, -1, -1, false);

    /// <summary>同上，步长按 43 + 123 + 42 估计。</summary>
    public static readonly AuctionReplyLayout Stride208 = new("stride208", 208, 43, 123, 0, -1, -1, false);

    public static readonly IReadOnlyList<AuctionReplyLayout> All =
        [Empty, Legacy, LegacyPlain, Legacy208, Legacy176, A21, A21Plain, Entry, EntrySwap, Core, CoreSwap, Stride176, Stride208];

    public static AuctionReplyLayout Default => Legacy;

    public static AuctionReplyLayout? Find(string name)
    {
        foreach (var layout in All)
        {
            if (string.Equals(layout.Name, name, StringComparison.OrdinalIgnoreCase))
                return layout;
        }
        return null;
    }

    public static string Names => string.Join('/', All.Select(layout => layout.Name));
}
