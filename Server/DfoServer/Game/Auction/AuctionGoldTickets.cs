namespace DfoServer.Game.Auction;

/// <summary>
/// 金币寄售用的金币券。出处是本机 PVF <c>stackable/cash/chn_Trade_Gold/gold_*.stk</c>：
/// 17 张 <c>[attach type] [free]</c>、<c>[stackable type] [material] 4</c> 的堆叠材料，
/// 名称与文件名都是面额（1百万…2亿）。同目录的 <c>trade_point.stk</c>（2681762）和
/// <c>trade_point_event.stk</c>（2681917）是代币券，仍走账号钱包，不能上架。
///
/// 分类 40101–40117（<c>0x9CA5</c>–<c>0x9CB5</c>）是参考实现里绕过主分类树的金币寄售区间。
/// 17 个分类与 17 张券数量相同，但「哪个分类对应哪张面额」没有抓包，因此这 17 个 id
/// 都只表示「金币券市场」，不再按面额拆开。拆开需要一次「点某个金币分类 → 请求里的 category id」抓包。
/// </summary>
internal static class AuctionGoldTickets
{
    public const int FirstCategoryId = 40101;
    public const int LastCategoryId = 40117;

    /// <summary>SQL 里用来表示「模板条件不可能命中」的哨兵。挂牌表的模板 id 约束是非负，-1 不会命中任何行。</summary>
    public const int EmptyTemplateId = -1;

    public static readonly AuctionGoldTicket[] All =
    [
        new(2681725, 1_000_000, "1百万", "stackable/cash/chn_Trade_Gold/gold_1m.stk"),
        new(2681726, 2_000_000, "2百万", "stackable/cash/chn_Trade_Gold/gold_2m.stk"),
        new(2681727, 3_000_000, "3百万", "stackable/cash/chn_Trade_Gold/gold_3m.stk"),
        new(2681728, 4_000_000, "4百万", "stackable/cash/chn_Trade_Gold/gold_4m.stk"),
        new(2681729, 5_000_000, "5百万", "stackable/cash/chn_Trade_Gold/gold_5m.stk"),
        new(2681730, 6_000_000, "6百万", "stackable/cash/chn_Trade_Gold/gold_6m.stk"),
        new(2681731, 7_000_000, "7百万", "stackable/cash/chn_Trade_Gold/gold_7m.stk"),
        new(2681732, 8_000_000, "8百万", "stackable/cash/chn_Trade_Gold/gold_8m.stk"),
        new(2681733, 9_000_000, "9百万", "stackable/cash/chn_Trade_Gold/gold_9m.stk"),
        new(2681734, 10_000_000, "1千万", "stackable/cash/chn_Trade_Gold/gold_10m.stk"),
        new(2681735, 20_000_000, "2千万", "stackable/cash/chn_Trade_Gold/gold_20m.stk"),
        new(2681736, 30_000_000, "3千万", "stackable/cash/chn_Trade_Gold/gold_30m.stk"),
        new(2683069, 40_000_000, "4千万", "stackable/cash/chn_Trade_Gold/gold_40m.stk"),
        new(2683070, 50_000_000, "5千万", "stackable/cash/chn_Trade_Gold/gold_50m.stk"),
        new(2683071, 100_000_000, "1亿", "stackable/cash/chn_Trade_Gold/gold_100m.stk"),
        new(2683072, 150_000_000, "1.5亿", "stackable/cash/chn_Trade_Gold/gold_150m.stk"),
        new(2683073, 200_000_000, "2亿", "stackable/cash/chn_Trade_Gold/gold_200m.stk")
    ];

    public static bool IsCategory(int categoryId) => categoryId is >= FirstCategoryId and <= LastCategoryId;

    public static bool IsTicket(int itemTemplateId)
    {
        foreach (var ticket in All)
        {
            if (ticket.ItemId == itemTemplateId)
                return true;
        }
        return false;
    }
}

/// <param name="FaceGold">stk 名称与文件名给出的面额。购买结算仍按挂牌一口价，不按面额入账。</param>
internal readonly record struct AuctionGoldTicket(int ItemId, int FaceGold, string Name, string ScriptPath);
