using static DfoServer.Testing.TestCheck;
using System.Collections.Immutable;
using DfoServer.Game.Auction;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.Network.Builders;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;

namespace DfoServer;

// 拍卖行读路径（挂牌表 → 分页搜索 → 合同包体）的断言：新建库插入、匹配总数、翻页、排除自己、
// 只统计在售未过期的挂牌、分类过滤（顶层大类/全部/未映射）、种子取值与候选池覆盖面。
// 条目字节快照用物品模块的编码入口构造，因此本组不需要 PVF；装备部位的模板展开需要真实目录，
// 由启动一次带 --auction-seed 的独立实例核对（见 Docs/模块/拍卖行.md）。
internal static class AuctionListingTests
{
    public static void Run()
    {
        int checks = Count;
        Seed();
        Pagination();
        Filters();
        Categories();
        ProductionDatabaseShape();
        CategoryLeaves();
        WeaponArmorLeaves();
        AveragePrice();
        UnreconciledLeaves();
        CategoryMapping();
        LeafIdTable();
        SpecialAndTitleLeavesAndSort();
        SeedPool();
        InvalidDraft();
        GoldConsignmentAndSpecialAuction();
        Console.WriteLine($"PASS {Count - checks} auction listing read assertions");
    }

    static void GoldConsignmentAndSpecialAuction()
    {
        That(AuctionGoldTickets.TryGetByCategory(40101, out var firstTicket) && firstTicket.ItemId == 2681725 && firstTicket.FaceGold == 1_000_000 &&
            AuctionGoldTickets.TryGetByCategory(40117, out var lastTicket) && lastTicket.ItemId == 2683073 && lastTicket.FaceGold == 200_000_000 &&
            AuctionGoldTickets.TryGetByItemId(2681734, out var tenMillion) && tenMillion.FaceGold == 10_000_000 &&
            !AuctionGoldTickets.TryGetByCategory(40100, out _),
            "金币寄售17档面额分类(40101..40117)与金币券模板ID精确双向映射");
        var board = new SpecialAuctionBoard();
        board.Publish(new(1, 101010912, 1, 50_000, 0, 100_000, null, "", 2000));
        That(!board.TryPlaceBid(1, 10, "BidderA", 49_999, 1000, out _) &&
            board.TryPlaceBid(1, 10, "BidderA", 60_000, 1000, out var afterBid) &&
            afterBid.CurrentBid == 60_000 && afterBid.HighestBidderId == 10 && !afterBid.Settled,
            "特殊拍卖竞价校验最低起拍与递增出价");
        That(!board.TryPlaceBid(1, 11, "BidderB", 60_000, 1000, out _) &&
            board.TryBuyNow(1, 11, "BidderB", 100_000, 1000, out var afterBuyout) &&
            afterBuyout.Settled && afterBuyout.HighestBidderId == 11 &&
            !board.TryPlaceBid(1, 12, "BidderC", 100_000, 1000, out _),
            "特殊拍卖一口价成交后锁定拍品并拒绝后续竞价");
    }

    static void Seed()
    {
        using var f = new Fixture();
        var items = new[] { Item(101010912, ItemKind.Equipment, 1), Item(10014751, ItemKind.Material, 5) };
        var seed = f.Service.Seed(4, f.Now, items);
        That(seed.Created == 4 && seed.ItemIds.SequenceEqual([101010912, 10014751, 101010912, 10014751]),
            "种子按候选物品轮换写入指定条数");
        var page = f.Search(AuctionCategoryFilter.All, 0);
        That(page.TotalCount == 4 && page.Listings.Length == 4, "搜索返回匹配总数与本页记录");
        var rows = page.Listings;
        That(rows.Select(row => row.SellerName).SequenceEqual(["拍卖机器人甲", "拍卖机器人乙", "拍卖机器人丙", "拍卖机器人甲"]),
            "机器人卖家按顺序轮换");
        That(rows.All(row => row.SellerCharacterId is null), "机器人挂牌的 seller_character_id 为 NULL");
        That(rows.Select(row => row.StartingPrice).SequenceEqual([12_000, 13_000, 14_000, 15_000]), "起拍价 12000 起每行 +1000");
        That(rows.Select(row => row.BuyoutPrice).SequenceEqual([24_000, 26_000, -1, 30_000]), "每第三行是纯竞拍（一口价 -1）");
        That(rows.All(row => row.ExpiresAtUnix == f.Now + 86_400), "到期时间为当前时间 + 24 小时");
        That(rows.All(row => row.PostedAtUnix == f.Now && row.CurrentBid == 0 && row.DisplayPrice == row.StartingPrice),
            "无出价时「当前竞拍」列取起拍价");
        That(rows[0].ItemCount == 1 && rows[1].ItemCount == 5 && rows[0].ItemKind == (byte)ItemKind.Equipment &&
            rows[1].ItemKind == (byte)ItemKind.Material, "整件与堆叠物的种类和数量来自条目快照");

        // 读路径结果直接按合同编码：包体内道具块的可核字段应与条目快照一致。
        var reply = AuctionPackets.SearchReply(0, page.TotalCount, page.Listings);
        That(reply.Length == 8 + 4 * AuctionPackets.RecordSize && BitConverter.ToInt32(reply, 2) == 4,
            "读路径结果编码为 8 + 4×168 且头部是匹配总数");
        int blockAt = 8 + AuctionPackets.ItemOffset;
        That(BitConverter.ToInt32(reply, blockAt + 1) == 101010912 && reply[blockAt + 5] == 2 &&
            reply[blockAt + 12] == 1 && BitConverter.ToInt16(reply, blockAt + 10) == 45,
            "包体内道具块逐字段来自条目快照（attr/耐久/封印）");

        string before = f.Snapshot();
        That(f.Service.Seed(2, f.Now, items).Created == 0 && f.Snapshot() == before, "表内已有挂牌时不重复种子且零写入");
        That(f.Service.Seed(0, f.Now, items).Created == 0 && f.Snapshot() == before, "条数为 0 时不写入");
        var reopened = new AuctionListingRepository(f.Path);
        var reopenedService = new AuctionService(new CharacterRepository(f.Path), f.Categories, f.Items, f.Pieces, AuctionPackets.ItemEntrySnapshot, AuctionPackets.ItemCoreFromEntry);
        That(reopened.CountListings() == 4 &&
            reopenedService.Search(new(0, null, AuctionCategoryFilter.All), null, f.Now).TotalCount == 4,
            "仓储重开后挂牌仍在");
    }

    static void Pagination()
    {
        using var f = new Fixture();
        f.Service.Seed(12, f.Now, [Item(101010912, ItemKind.Equipment, 1)]);
        var first = f.Search(AuctionCategoryFilter.All, 0);
        var second = f.Search(AuctionCategoryFilter.All, AuctionService.PageSize);
        var beyond = f.Search(AuctionCategoryFilter.All, 20);
        That(AuctionService.PageSize == 10 && first.Listings.Length == 10 && second.Listings.Length == 2 && beyond.Listings.Length == 0,
            "每页 10 条：本批条数按偏移切分，越界返回空批");
        That(first.TotalCount == 12 && second.TotalCount == 12 && beyond.TotalCount == 12,
            "匹配总数不受 offset 与本批条数影响，客户端据此算总页数");
        That(first.Listings[9].ListingId + 1 == second.Listings[0].ListingId, "按 listing_id 稳定排序，翻页不重不漏");
    }

    static void Filters()
    {
        using var f = new Fixture();
        f.Repository.InsertListings(
        [
            Draft(7, "自己", 101010912, f.Now),
            Draft(null, "机器人", 101010912, f.Now),
            Draft(9, "别人", 10014751, f.Now),
            Draft(9, "已过期", 101010912, f.Now - 100) with { ExpiresAtUnix = f.Now - 1 }
        ]);
        That(f.Repository.CountListings() == 4, "表计数包含已过期挂牌（种子判断看整表）");
        var all = f.Search(AuctionCategoryFilter.All, 0);
        That(all.TotalCount == 3 && all.Listings.Length == 3, "搜索只统计在售且未过期的挂牌");
        var self = f.Search(AuctionCategoryFilter.All, 0, excludeSeller: 7);
        That(self.TotalCount == 2 && self.Listings.All(row => row.SellerName != "自己"),
            "排除自己上架的挂牌，机器人挂牌（seller 为 NULL）保留");
        That(f.Search(AuctionCategoryFilter.All, 0, excludeSeller: 8).TotalCount == 3, "排除其他角色时自己的挂牌照常显示");
        var byItem = f.Search(AuctionCategoryFilter.All, 0, itemTemplateId: 10014751);
        That(byItem.TotalCount == 1 && byItem.Listings.Single().SellerName == "别人", "按道具搜索只回该模板的挂牌");
        var bySelfItem = f.Search(AuctionCategoryFilter.All, 0, itemTemplateId: 101010912, excludeSeller: 7);
        That(bySelfItem.TotalCount == 1 && bySelfItem.Listings.Single().SellerName == "机器人", "按道具搜索同样排除自己");
        f.Execute("UPDATE auction_listings SET state=1 WHERE seller_name='机器人'");
        That(f.Search(AuctionCategoryFilter.All, 0).TotalCount == 2, "已成交（state=1）的挂牌不参与搜索");
        f.Execute($"UPDATE auction_listings SET state=0,expires_at_unix={f.Now} WHERE seller_name='机器人'");
        That(f.Search(AuctionCategoryFilter.All, 0).TotalCount == 2, "到期时间等于当前时间的挂牌按已过期处理");
        f.Execute($"UPDATE auction_listings SET expires_at_unix={f.Now + 1} WHERE seller_name='机器人'");
        That(f.Search(AuctionCategoryFilter.All, 0).TotalCount == 3, "未到期的挂牌恢复参与搜索");
    }

    /// <summary>
    /// 分类过滤：顶层按物品大类过滤，匹配总数与分页都必须按过滤后的集合算；
    /// 「全部」（40119）与未映射 id 保持不过滤。分类解析本身不需要目录，因此本组不需要 PVF。
    /// </summary>
    static void Categories()
    {
        using var f = new Fixture();
        f.Repository.InsertListings(
        [
            Stackable(9, "材料", 3037, f.Now, ItemKind.Material, 20),
            Stackable(null, "消耗品", 1000, f.Now, ItemKind.Consumable, 5),
            Stackable(null, "武器", 101010912, f.Now, ItemKind.Equipment, 1)
        ]);
        That(f.Search(AuctionCategoryFilter.All, 0).TotalCount == 3, "「全部」节点（40119）不过滤");
        That(f.Search(0, 0).TotalCount == 3, "未采集过的分类 id 不过滤（id 表未逐节点确认）");
        // 消耗品父节点（13000）的维度由 6 个子叶合并：普通/投掷/抽取/其它→Consumable、材料→Material+Profession、
        // 任务→Quest。因此它比首版的「只按 Consumable」更宽——材料也在其下（这与参考实现的父节点语义一致），
        // 但武器仍然不在。
        var consumable = f.Search(13000, 0);
        That(consumable.TotalCount == 2 && consumable.Listings.Select(row => row.ItemTemplateId).SequenceEqual([3037, 1000]),
            "消耗品父节点回消耗品与材料，武器（item_kind=1）不计入；匹配总数是过滤后的值");
        // 带装备部位的分类走的是「取全量 → 内存过滤 → 再分页」，全量查询必须与分页查询同一套条件。
        var matches = f.Matches(consumable.Plan!.Filter);
        That(matches.Select(row => row.ListingId).SequenceEqual(consumable.Listings.Select(row => row.ListingId)),
            "全量匹配查询与分页查询给出同一批挂牌（内存过滤路径的一致性前提）");
        That(f.Search(13000, AuctionService.PageSize).TotalCount == 2 && f.Search(13000, 20).Listings.Length == 0,
            "分类过滤后的匹配总数不受 offset 影响，越界返回空批");
        That(f.Search(13000, 0, itemTemplateId: 3037).TotalCount == 1 &&
            f.Search(13000, 0, itemTemplateId: 101010912).TotalCount == 0,
            "分类与按道具搜索同时给出时取交集（武器不属于消耗品，交集为空）");
        That(f.Search(13000, 0, excludeSeller: 9).TotalCount == 1, "分类过滤与排除自己可以叠加");
        That(f.Search(13000, 0, excludeSeller: 1000).TotalCount == 2, "排除未挂牌的角色不影响分类过滤结果");
        // 已对账的子叶按自己的维度过滤，不再整体并进顶层大类。
        That(f.Search(13001, 0).TotalCount == 1 && f.Search(13001, 0).Listings.Single().ItemTemplateId == 1000,
            "消耗品→消耗品子叶（13001）只回 item_kind=2");
        That(f.Search(13002, 0).TotalCount == 1 && f.Search(13002, 0).Listings.Single().ItemTemplateId == 3037,
            "消耗品→材料子叶（13002）只回 item_kind=3");
        // 武器子叶已逐节点对账：按族掩码 + 部位 12 过滤，称号/防具/材料都被挡住。
        var weapon = f.Search(10101, 0);
        That(weapon.TotalCount == 1 && weapon.Listings.Single().SellerName == "武器",
            "武器子叶（10101）只回族 101 的装备部位 12；称号、防具、材料都被挡住");
        // 完全没见过的 id：不过滤（客户端界面可能已变化，猜错的过滤比不过滤更难发现）。
        That(f.Search(40101, 0).TotalCount == 0 && f.Search(99999, 0).TotalCount == 3,
            "金币寄售 40101 只检索金币券（这组没有金币券，所以是 0）；其它未采集 id 仍不过滤");
    }

    /// <summary>
    /// **生产库形状回归**（2026-10-11 22:1x 的 `game.db` 逐行同形状）：13 条在售 =
    /// 4 武器 + 5 件种子防具 + 2 材料 + 1 消耗品 + 1 条**搜索者自己**上架的防具，另有 1 条 `state=2`（已取消）。
    ///
    /// 为什么单列一组：这组数据是「分类搜索把所有结果过滤成 0 条」那次误判的现场。它把每个顶层大类解析出的
    /// Kinds / Slots **打进输出**并逐个断言期望条数，同时**逐层**断言同一个请求在哪一层剩下多少行
    /// —— 仓储计数 → SQL 条件（`item_kind IN (…)`）→ 读用例的内存维度（部位/族/职业）→ 应答编码
    /// （含客户端静默丢行门闸）。因此「某一层把行滤空」会直接落在下面某一条断言上，而不是靠翻日志猜。
    ///
    /// 「回 0 条」的两种情形必须能分开：**确无数据**（如族 104 的武器一件都没挂）与**被条件滤掉**。
    /// 因此对前者同时断言 SQL 侧取回的行数 > 0（行到达了内存维度那一层，只是真的没有匹配）。
    /// </summary>
    static void ProductionDatabaseShape()
    {
        using var f = new Fixture();
        const long self = 4;   // 搜索者自己的角色 id（库里那条 10400 的 seller_character_id）
        f.Repository.InsertListings(
        [
            // —— 机器人种子（对应库里 listing_id 5–16）——
            Draft(null, "拍卖机器人甲", 101010912, f.Now),              // 武器：族 101
            Draft(null, "拍卖机器人乙", 102040571, f.Now),              // 武器：族 102
            Draft(null, "拍卖机器人丙", 106020596, f.Now),              // 武器：族 106
            Draft(null, "拍卖机器人甲", 106000064, f.Now),              // 武器：族 106
            Draft(null, "拍卖机器人乙", 10001, f.Now),                  // 防具：部位 14
            Draft(null, "拍卖机器人丙", 10400, f.Now),                  // 防具：部位 15
            Draft(null, "拍卖机器人甲", 12400, f.Now),                  // 防具：部位 16
            Draft(null, "拍卖机器人乙", 16400, f.Now),                  // 防具：部位 18
            Draft(null, "拍卖机器人丙", 18400, f.Now),                  // 防具：部位 17
            Stackable(null, "拍卖机器人甲", 3037, f.Now, ItemKind.Material, 5),
            Stackable(null, "拍卖机器人乙", 3262, f.Now, ItemKind.Material, 5),
            Stackable(null, "拍卖机器人丙", 10014751, f.Now, ItemKind.Consumable, 5),
            // —— 玩家自己：1 条在售防具（搜索时要被「排除自己」挡下）+ 1 条已取消（不参与搜索）——
            Draft(self, "自己", 10400, f.Now),
            Draft(self, "自己", 10403, f.Now)
        ]);
        f.Execute("UPDATE auction_listings SET state=2 WHERE listing_id=(SELECT max(listing_id) FROM auction_listings)");

        // 第一层：表与仓储。数据完好 —— 这一层不可能滤掉行。
        That(f.Repository.CountListings() == 14, "库里 14 行（13 条在售 + 1 条已取消）：整表计数不做任何过滤");
        var unfiltered = f.Repository.Search(new(null, null), f.Now, 0, AuctionService.PageSize);
        That(unfiltered.TotalCount == 13 && unfiltered.Listings.Length == AuctionService.PageSize,
            "仓储分页搜索（无分类条件）回 13 条匹配、本页 10 条：在售/未过期条件没有把行滤掉");

        // 第二层：SQL 侧的大类下推。它是分类过滤唯一下推到 SQL 的一维，且不能把大类整体滤空。
        That(f.Matches(new(null, null, ImmutableArray.Create((byte)ItemKind.Equipment))).Length == 10,
            "item_kind IN (1) 命中 10 行（4 武器 + 6 防具）：武器/防具共用 Equipment，下推条件不会提前滤掉任何一类");
        That(f.Matches(new(null, null, ImmutableArray.Create(
                (byte)ItemKind.Consumable, (byte)ItemKind.Material, (byte)ItemKind.Quest, (byte)ItemKind.Profession))).Length == 3,
            "消耗品父节点下推的 item_kind IN (2,3,4,10) 命中 3 行（2 材料 + 1 消耗品）");

        // 第三层：读用例（分类维度 → SQL 条件 + 内存维度）。打印每个顶层大类解析出的维度，再逐个断言期望条数。
        (int Id, int Expected)[] topLevels =
        [
            (10000, 4),    // 武器：4 件种子武器；自己那条 10400 是**防具上衣**，不属于武器
            (11000, 6),    // 防具：5 件种子防具 + 自己那条 10400
            (12000, 0),    // 首饰：库里没有首饰在售（维度对，只是确无数据）
            (13000, 3),    // 消耗品：2 材料 + 1 消耗品
            (14000, 0),    // 宠物：库里没有宠物相关挂牌
            (31000, 13),   // 设计图：无维度依据 → 不过滤，回全部在售
            (32000, 0),    // 特殊装备：库里没有辅助装备/魔法石
            (33000, 0),    // 副职业：库里没有副职业道具
            (34000, 13),   // 其它：兜底叶子无法枚举 → 不过滤
            (AuctionCategoryFilter.All, 13)
        ];
        Console.WriteLine("  ── 顶层大类 → 解析维度 → 期望条数（生产库形状） ──");
        foreach (var (id, expected) in topLevels)
        {
            var page = f.Search(id, 0);
            var plan = page.Plan!;
            string kinds = plan.Kinds == 0 ? "-" : string.Join(',', plan.Filter.Kinds);
            string slots = plan.SlotDimensions is { } dimensions ? string.Join(',', AuctionCategoryDimensions.ToList(dimensions.Slots)) : "-";
            Console.WriteLine($"  cat={id,-6} {plan.Category.Status,-8} {plan.Category.Name ?? "-",-8} kinds=[{kinds,-12}] " +
                $"slots=[{slots,-18}] groups=0x{plan.Groups:X} 命中={page.TotalCount}（期望 {expected}）");
            That(page.TotalCount == expected && page.Listings.Length == Math.Min(expected, AuctionService.PageSize),
                $"大类 {id}（{plan.Category.Name ?? "未映射"}）命中 {expected} 条、本页 {Math.Min(expected, AuctionService.PageSize)} 条");
        }

        // 顶层大类的维度与条数逐一核对：Kinds 为空（`item_kind IN ()`）或部位集合为空的解析都会在这些断言上暴露。
        var weapon = f.Search(10000, 0);
        That(weapon.Plan!.Kinds == 1 && weapon.Plan.Filter.Kinds.SequenceEqual(new byte[] { (byte)ItemKind.Equipment }) &&
            weapon.Plan.SlotDimensions is { Slots: 1UL << 12 },
            "武器（10000）下推的 Kinds 非空（[1] = Equipment）、部位维度是 12，不存在「空集合条件」");
        That(weapon.Listings.Select(row => row.ItemTemplateId).Order()
                .SequenceEqual([101010912, 102040571, 106000064, 106020596]),
            "武器那 4 条就是 4 件种子武器，一件不多一件不少");
        var armor = f.Search(11000, 0);
        That(armor.Plan!.Filter.Kinds.SequenceEqual(new byte[] { (byte)ItemKind.Equipment }) &&
            armor.Listings.Count(row => row.ItemTemplateId == 10400) == 2,
            "防具（11000）命中的 6 条里 10400 占两行（种子 + 自己），两行都过材质/部位两维判定");
        That(f.Search(13001, 0).TotalCount == 1 && f.Search(13002, 0).TotalCount == 2,
            "消耗品（13001）1 条、材料（13002）2 条：子叶按各自的 item_kind 收窄且都不为空");

        // 排除自己与分类过滤是两条独立的门，不能互相顶替：自己那条防具只在「排除自己」下消失。
        That(f.Search(AuctionCategoryFilter.All, 0, excludeSeller: self).TotalCount == 12 &&
            f.Search(11000, 0, excludeSeller: self).TotalCount == 5 &&
            f.Search(10000, 0, excludeSeller: self).TotalCount == 4,
            "排除自己后「全部」12 条、防具 5 条、武器仍是 4 条（自己的行只被排除自己挡下，不是被分类过滤掉）");

        // 「确无数据」与「被条件滤掉」必须分得开：前者 SQL 侧仍取回行，后者 SQL 侧就已经是 0。
        var gunner = f.Search(10301, 0);
        That(gunner.TotalCount == 0 && f.Matches(gunner.Plan!.Filter).Length == 10,
            "神枪手武器（10301）回 0 条是「库里没有族 104 的武器」：SQL 侧仍取回 10 行 Equipment，0 来自内存里的族判定");
        var amulet = f.Search(12001, 0);
        That(amulet.TotalCount == 0 && f.Matches(amulet.Plan!.Filter).Length == 10,
            "项链子叶（12001）回 0 条是「没有首饰在售」：SQL 侧同样取回 10 行，0 来自内存里的部位判定");
        That(f.Matches(f.Search(33000, 0).Plan!.Filter).Length == 0,
            "副职业（33000）连 SQL 侧都是 0 行：这一类的 0 来自确无该大类的挂牌");

        // 未映射 / 未采集过的 id 不过滤（既有行为的非回归断言，见 Categories 组）。
        That(f.Search(21000, 0).TotalCount == 13 && f.Search(40101, 0).TotalCount == 0 && f.Search(99999, 0).TotalCount == 13,
            "未映射 id（21000、99999）不过滤；40101 是金币寄售，这组没有金币券所以是 0，不会把 13 件装备算进去");
        That(f.Search(21000, 0).Plan is { SlotDimensions: null, Category.Status: AuctionCategoryStatus.Unmapped },
            "未映射 id 的搜索计划不带任何维度，因此 SQL 与内存两侧都不会过滤");

        // 第四层：应答编码。行数一直走到线上帧体都在，且每条都过客户端的静默丢行门闸
        // （道具块 +6 = 记录 [49..52] 在 itemId 合法时必须 > 0，见 AuctionPackets 的丢行过滤器注释）。
        var reply = AuctionPackets.SearchReply(0, weapon.TotalCount, weapon.Listings);
        That(reply.Length == 8 + 4 * AuctionPackets.RecordSize && BitConverter.ToInt32(reply, 2) == 4 &&
            BitConverter.ToUInt16(reply, 6) == 4,
            "武器那一页编码为 8 + 4×168，帧头「匹配总数」与本批条数都是 4");
        int dropped = 0;
        for (int index = 0; index < weapon.Listings.Length; index++)
        {
            int block = 8 + index * AuctionPackets.RecordSize + AuctionPackets.ItemOffset;
            int itemId = BitConverter.ToInt32(reply, block + 1);
            int value = BitConverter.ToInt32(reply, block + 6);
            if (itemId <= 0 || value <= 0)
                dropped++;
        }
        That(dropped == 0, "每条记录的道具块都过客户端的丢行门闸（itemId 合法 ⇒ 块 +6 > 0），编码层不吞行");
        var all = f.Search(AuctionCategoryFilter.All, 0);
        var allReply = AuctionPackets.SearchReply(0, all.TotalCount, all.Listings);
        That(BitConverter.ToInt32(allReply, 2) == 13 && BitConverter.ToUInt16(allReply, 6) == AuctionService.PageSize &&
            allReply.Length == 8 + AuctionService.PageSize * AuctionPackets.RecordSize,
            "「全部」的帧头是 13 条匹配 + 本页 10 条，体长与记录数自洽（编码层不从总数里减行）");
    }

    /// <summary>
    /// 已对账子叶的**分页与总数口径**，以及「部位维度只能在内存里做」这条路径的隔离性。
    /// 部位（首饰/宠物装备）没有 SQL 列，必须走「取全量 → 内存过滤 → 再分页」，
    /// 因此这里专门验证：总数与分页都是**过滤后**的口径，且 SQL 侧不会提前剔掉内存侧要保留的行。
    /// 核心自测没有 PVF，部位来自 <see cref="Fixture.Slots"/> 这张显式模板表（构造器里灌进解析器的模板缓存），
    /// 不绕开被测逻辑：解析、SQL 条件、内存过滤、分页仍然走真实实现。
    /// </summary>
    static void CategoryLeaves()
    {
        using var f = new Fixture();
        f.Repository.InsertListings(
        [
            // 首饰三子叶：item_kind 全是 Equipment，只能靠装备部位 19/20/21 分开。
            Stackable(null, "项链", 102030101, f.Now, ItemKind.Equipment, 1),
            Stackable(null, "戒指", 104050202, f.Now, ItemKind.Equipment, 1),
            Stackable(null, "手镯", 106070303, f.Now, ItemKind.Equipment, 1),
            // 相邻部位：耳环(25)只归首饰父节点，用来确认父节点补上了它、而三个子叶都不含它。
            Stackable(null, "耳环", 108090404, f.Now, ItemKind.Equipment, 1),
            // 宠物本体：item_kind=Creature(5) 而部位 26；三件宠物装备是另一套（部位 27–29 + kind 6）。
            Stackable(null, "宠物本体", 120120808, f.Now, ItemKind.Creature, 1),
            // 宠物装备三子叶：item_kind 同为 CreatureEquipment(6)，只能靠装备部位 27/28/29 分开。
            Stackable(null, "红宠装", 110110505, f.Now, ItemKind.CreatureEquipment, 1),
            Stackable(null, "蓝宠装", 112130606, f.Now, ItemKind.CreatureEquipment, 1),
            Stackable(null, "绿宠装", 114150707, f.Now, ItemKind.CreatureEquipment, 1)
        ]);
        That(f.Search(AuctionCategoryFilter.All, 0).TotalCount == 8, "「全部」节点看到全部 8 条");

        var amulet = f.Search(12001, 0);
        That(amulet.TotalCount == 1 && amulet.Listings.Single().SellerName == "项链",
            "首饰→项链子叶（12001）只回装备部位 19，戒指/手镯/耳环都不计入；总数是过滤后的值");
        That(f.Search(12002, 0).Listings.Single().SellerName == "戒指" && f.Search(12003, 0).Listings.Single().SellerName == "手镯",
            "首饰→戒指（12002）与手镯（12003）按各自部位过滤（参考表 §7.6 的 amulet/ring/wrist 顺序）");
        // 首饰父节点（12000）= 三个子叶的部位（19/21/20）+ 只有父节点能显示的耳环（25），
        // 不含辅助装备(22)/魔法石(23)/辅助武器(24)。
        That(f.Search(12000, 0).TotalCount == 4 &&
            f.Search(12000, 0).Listings.Select(row => row.SellerName).SequenceEqual(["项链", "戒指", "手镯", "耳环"]),
            "首饰父节点（12000）收拢三个首饰子叶 + 耳环（参考表把 earring 写在首饰类下）");

        var red = f.Search(14001, 0);
        That(red.TotalCount == 1 && red.Listings.Single().SellerName == "红宠装",
            "宠物→红色宠物装备（14001）只回装备部位 27");
        That(f.Search(14002, 0).Listings.Single().SellerName == "蓝宠装" && f.Search(14003, 0).Listings.Single().SellerName == "绿宠装",
            "宠物→蓝/绿宠物装备（14002/14003）按部位 28/29 过滤（参考表 §7.3，2026-09-04 抓包 + 用户确认定案）");
        // 「宠物」（14000）在客户端是宠物区的父节点：回宠物本体（部位 26 / item_kind=5）与三件宠物装备，
        // 不含材料等无关物品。参考表 §7.3 把它记作「宠物蛋」，但本仓库资料注明该大类用 ItemKind.Creature，
        // 且 [output index]（蛋与本体的区分字段）没有进模板，故躯体与蛋在此一起返回。
        That(f.Search(14000, 0).TotalCount == 4 && f.Search(14000, 0).Listings.All(row => row.ItemKind is 5 or 6),
            "宠物父节点（14000）回宠物本体与三件宠物装备，材料不计入");
        // SQL 侧口径必须与内存侧一致：宠物装备子叶下推的 item_kind 只能是 6，不能把 5 也带上。
        That(f.Matches(f.Search(14001, 0).Plan!.Filter).Select(row => row.ItemKind).Distinct().SequenceEqual(new byte[] { 6 }),
            "宠物装备子叶下推 SQL 的 item_kind 只有 6（与内存部位口径一致）");

        // 内存过滤在分页之前：再加 10 条项链后，项链子叶的匹配总数必须是过滤后的 11（不是整表条数），
        // 否则客户端按 10 条一页算出的页数会虚高、翻到后面全是空页。
        for (int index = 0; index < 10; index++)
            f.Repository.InsertListings([Stackable(null, "项链" + index, 102030101, f.Now, ItemKind.Equipment, 1)]);
        var paged = f.Search(12001, 0);
        That(paged.TotalCount == 11 && paged.Listings.Length == 10, "首饰子叶的匹配总数与每页 10 条都是过滤后的口径");
        var second = f.Search(12001, AuctionService.PageSize);
        That(second.TotalCount == 11 && second.Listings.Length == 1 && second.Listings.All(row => row.SellerName.StartsWith("项链")),
            "翻到第二页仍是过滤后的口径，不重不漏");
        That(f.Search(12001, 20).Listings.Length == 0 && f.Search(12001, 20).TotalCount == 11, "越界返回空批但总数不变");
    }

    /// <summary>
    /// **仍未对账的大类**（辅助装备/魔法石/设计图）点具体子叶时的行为：
    /// 前两类落回所属顶层维度，设计图类没有维度依据因此保持不过滤。
    /// 武器与防具的子叶**已经逐节点对账**，所以这里同时锁住「它们不再走兜底」：
    /// 武器子叶按族过滤（不再只按部位 12 粗过滤）、防具子叶按材质 + 部位过滤。
    /// </summary>
    static void UnreconciledLeaves()
    {
        using var f = new Fixture();
        f.Repository.InsertListings(
        [
            Stackable(null, "武器", 101010912, f.Now, ItemKind.Equipment, 1),      // 族 101、部位 12
            Stackable(null, "称号", 109600041, f.Now, ItemKind.Equipment, 1),      // 部位 13
            Stackable(null, "布甲上衣", 10001, f.Now, ItemKind.Equipment, 1),      // 族 110、部位 14
            Stackable(null, "下装", 12400, f.Now, ItemKind.Equipment, 1),          // 族 110、部位 16
            Stackable(null, "鞋", 18400, f.Now, ItemKind.Equipment, 1),            // 族 110、部位 17
            Stackable(null, "辅助装备", 116170808, f.Now, ItemKind.Equipment, 1),  // 部位 22
            Stackable(null, "魔法石", 118190909, f.Now, ItemKind.Equipment, 1),    // 部位 23
            Stackable(null, "材料", 3037, f.Now, ItemKind.Material, 20)
        ]);
        That(f.Search(AuctionCategoryFilter.All, 0).TotalCount == 8, "「全部」节点看到全部 8 条");

        // 武器子叶：按族 101 + 部位 12。称号（13）、防具（14–18）、材料都被挡住。
        var weapon = f.Search(10101, 0);
        That(weapon.TotalCount == 1 && weapon.Listings.Single().SellerName == "武器",
            "武器子叶（10101）只回族 101 的装备部位 12；称号、防具、材料都被挡住");
        That(f.Search(10000, 0).TotalCount == 1, "武器顶层与子叶口径一致");
        // 防具子叶：材质（族 110 布甲）+ 部位（14/16/17 在声明内）都要命中。
        That(f.Search(11002, 0).Listings.Single().ItemTemplateId == 10001 &&
            f.Search(11004, 0).Listings.Single().ItemTemplateId == 12400 &&
            f.Search(11005, 0).Listings.Single().ItemTemplateId == 18400,
            "布甲的上衣/下装/鞋（11002/11004/11005）各回自己那一件，材质与两个部位都命中");
        That(f.Search(11000, 0).TotalCount == 3 && f.Search(11000, 0).Listings.All(row => row.ItemTemplateId != 101010912),
            "防具父节点收拢布甲三件但挡住武器（族掩码与部位两个维度同时生效）");
        // 特殊装备：两个 id 空间的子叶维度未对账，但能按 id 区间归族——320xx 归辅助装备（部位 22）、
        // 321xx 归魔法石（部位 23）。比首版「不过滤」收窄，且**不**在族内按序号猜是哪个职业。
        var special = f.Search(32002, 0);
        That(special.TotalCount == 1 && special.Listings.Single().SellerName == "辅助装备",
            "辅助装备子叶（32002）落回辅助装备 32001 的部位 22，不会把魔法石也带出来");
        var stone = f.Search(32116, 0);
        That(stone.TotalCount == 1 && stone.Listings.Single().SellerName == "魔法石",
            "魔法石子叶（32116）落回魔法石 32100 的部位 23，不会把辅助装备也带出来");
        That(f.Search(32000, 0).TotalCount == 2, "特殊装备顶层（32000）收拢辅助装备与魔法石");
        That(f.Search(32001, 0).TotalCount == 1 && f.Search(32100, 0).TotalCount == 1,
            "辅助装备/魔法石各自的节点只回自己那一类");
        // 设计图：参考表标注该大类已作废，服务端没有可用的维度依据，因此保持不过滤。
        That(f.Search(31002, 0).TotalCount == 8 && f.Search(31000, 0).TotalCount == 8,
            "设计图子叶（31002）与顶层都不过滤（无维度依据，如实暴露而不是猜一个）");
    }

    /// <summary>分类 id → 过滤维度的映射表本身（不涉及数据库，也就能在没有 PVF 的核心自测里锁定取值）。</summary>
    static void CategoryMapping()
    {
        var weapon = AuctionCategoryFilter.Resolve(10000);
        That(weapon.Status == AuctionCategoryStatus.Mapped && weapon.Name == "武器" &&
            weapon.Dimensions.SlotList.SequenceEqual(new byte[] { 12 }) && !AuctionCategoryFilter.HasKinds(weapon.Dimensions),
            "武器只按装备部位 12 声明维度（部位本就隐含武器，不再叠加会被 SQL 侧过度收窄的种类条件）");
        That(AuctionCategoryFilter.Resolve(11000).Dimensions.SlotList.SequenceEqual(new byte[] { 14, 15, 16, 17, 18 }), "防具覆盖五个部位");
        // 首饰父节点 = 三个子叶的部位（19/21/20）+ 只有父节点能显示的耳环（25）；
        // 不含辅助装备(22)/魔法石(23)/辅助武器(24)——那些部位属于别的分类，进来了就是串类。
        That(AuctionCategoryFilter.Resolve(12000).Dimensions.SlotList.SequenceEqual(new byte[] { 19, 20, 21, 25 }),
            "首饰父节点覆盖三个子叶 + 耳环，且不把辅助装备/魔法石的部位一起收进来");
        That(AuctionCategoryFilter.Resolve(12004).Dimensions.SlotList.SequenceEqual(new byte[] { 13 }), "称号按称号部位过滤");
        That(AuctionCategoryFilter.Resolve(32000).Dimensions.SlotList.SequenceEqual(new byte[] { 22, 23 }) &&
            AuctionCategoryFilter.Resolve(32001).Dimensions.SlotList.SequenceEqual(new byte[] { 22 }) &&
            AuctionCategoryFilter.Resolve(32100).Dimensions.SlotList.SequenceEqual(new byte[] { 23 }),
            "特殊装备按辅助装备/魔法石部位过滤，与规范 id（0x7D00/0x7D01/0x7D64）逐项对应");
        That(AuctionCategoryFilter.Resolve(13000).Dimensions.KindList.SequenceEqual(
                new byte[] { (byte)ItemKind.Consumable, (byte)ItemKind.Material, (byte)ItemKind.Quest, (byte)ItemKind.Profession }),
            "消耗品父节点的维度由 6 个子叶合并：普通/投掷/抽取/其它→Consumable、材料→Material+Profession、任务→Quest");
        // 宠物父节点 = 三个宠物装备子叶的部位（27/28/29）+ 兜底补上的宠物本体部位（26），物品大类是 Creature。
        That(AuctionCategoryFilter.Resolve(14000).Dimensions.KindList.SequenceEqual(new byte[] { (byte)ItemKind.Creature }) &&
            AuctionCategoryFilter.Resolve(14000).Dimensions.SlotList.SequenceEqual(new byte[] { 26, 27, 28, 29 }),
            "宠物父节点 = 宠物本体（部位 26）+ 三个宠物装备子叶（部位 27–29），物品大类是 Creature");
        That(AuctionCategoryFilter.Resolve(33000).Dimensions.KindList.SequenceEqual(new byte[] { (byte)ItemKind.Profession }),
            "副职业按物品大类过滤");
        That(AuctionCategoryFilter.Resolve(AuctionCategoryFilter.All).Status == AuctionCategoryStatus.All &&
            AuctionCategoryFilter.Resolve(40101).Status == AuctionCategoryStatus.Gold &&
            AuctionCategoryFilter.Resolve(40117).Status == AuctionCategoryStatus.Gold &&
            AuctionCategoryFilter.Resolve(21000).Status == AuctionCategoryStatus.Unmapped,
            "「全部」是已识别节点；40101–40117 是金币寄售；其它未知 id 仍归未映射");
        That(AuctionCategoryFilter.Resolve(AuctionCategoryFilter.All).Note is not null &&
            AuctionCategoryFilter.Resolve(10101).Note is not null && AuctionCategoryFilter.Resolve(21000).Note is not null,
            "每条解析都带回取舍说明，日志因此能区分「全部」「子叶未对账」「没见过」三种不过滤/粗过滤");
    }

    /// <summary>
    /// 分类 id 表本身：把「客户端实际发出的 140 个 id」与「服务端认识的节点」逐个锁死。
    ///
    /// 这是本次子叶映射的**审计断言**：客户端是点遍整棵分类树采集出来的 140 个 id
    /// （9 个顶层 + 131 个子叶，见 <see cref="AuctionCategoryFilter.CollectedIds"/>），
    /// 源码表只允许是它的子集——表里出现采集之外的 id，说明有人凭猜测加了节点。
    /// 反过来「采集到但表里没有」若是已对账大类下的子叶，就是漏映射（由下面的逐大类计数断言拦住）。
    /// </summary>
    static void LeafIdTable()
    {
        // 采集清单是**界面实际下发**的 140 个 id；已对账的子叶表是它的子集 +
        // 「清单里没有、但由族号与界面顺序推得」的武器/防具子叶。两份合起来才是服务端认识的节点全集。
        var collectedSet = AuctionCategoryFilter.CollectedIds.ToHashSet();
        That(collectedSet.Count == 140 && AuctionCategoryFilter.CollectedIds.Length == 140,
            "采集清单是 140 个互不重复的 id（9 个顶层 + 131 个子叶）");
        int mappedLeaves = AuctionCategoryFilter.Leaves.Length;
        That(mappedLeaves == 119,
            $"已对账子叶 {mappedLeaves} 个 = 首饰 3 + 消耗品 6 + 宠物装备 3 + 副职业 4 + 武器 45 + 防具 25 + 辅助装备 16 + 魔法石 16 + 耳环 1");
        // 每个采集到的 id 都必须被解析成「已识别」（要么精确命中、要么归到某一大类），
        // 不允许出现「客户端点过、服务端却完全没见过」的 id：那说明映射表落后于客户端界面。
        var unrecognized = AuctionCategoryFilter.CollectedIds
            .Where(id => AuctionCategoryFilter.Resolve(id).Status != AuctionCategoryStatus.Mapped).ToArray();
        That(unrecognized.Length == 0,
            "采集到的 140 个 id 全部被识别（顶层精确命中、子叶命中或按所属大类归族），没有一个落到「没见过」");
        // 只有「设计图」与「其它」允许是「认了 id 但确实没有维度可过滤」——这两类的语义就是无法枚举。
        var dimensionless = AuctionCategoryFilter.CollectedIds
            .Where(id => AuctionCategoryFilter.Resolve(id).Dimensions.Kinds == 0 &&
                AuctionCategoryFilter.Resolve(id).Dimensions.Slots == 0 &&
                AuctionCategoryFilter.Resolve(id).Dimensions.Groups == 0 &&
                AuctionCategoryFilter.Resolve(id).Dimensions.Jobs == 0)
            .Distinct().ToArray();
        That(dimensionless.All(id => id / 100 is 310 or 311 or 312 or 313 or 340),
            "无维度可过滤的只有设计图（310/311/312/313 族）与「其它」（34000），其余都必须真的有维度");
        // 子叶表里**不在**采集清单里的 id 只能是武器/防具段（10000–11999）：它们由族号+界面顺序推得，
        // 其余大类的子叶 id 必须逐条来自客户端实发清单（不许凭猜测新增）。
        var outside = AuctionCategoryFilter.Leaves.Where(leaf => !collectedSet.Contains(leaf.Id)).Select(leaf => leaf.Id).ToArray();
        That(outside.Length == 6 && outside.All(id => id >= 10101 && id <= 11405),
            $"采集清单之外的子叶只有 {outside.Length} 个（武器/防具族里被界面顺序漏采的那几个），" +
            "且都落在武器/防具段；其余子叶 id 都来自客户端实发清单");
        That(AuctionCategoryFilter.Leaves.All(leaf => leaf.TopLevelId is 10000 or 11000 || collectedSet.Contains(leaf.Id)),
            "武器/防具以外的每个子叶 id 都在客户端实际发过的清单里，没有一个是猜出来的");
        // 采集清单里的武器/防具子叶（共 40 − 9 − …）必须与子叶表逐条对上，不能有「表里漏了、日志里点过」的 id。
        var weaponArmorCollected = AuctionCategoryFilter.CollectedIds
            .Where(id => id is >= 10101 and <= 11405 && id % 100 != 0).ToArray();
        var weaponArmorMapped = AuctionCategoryFilter.Leaves
            .Where(leaf => leaf.TopLevelId is 10000 or 11000 && leaf.Id % 100 != 0).Select(leaf => leaf.Id).ToHashSet();
        That(weaponArmorCollected.All(weaponArmorMapped.Contains),
            "采集清单里的武器/防具子叶全部出现在子叶表里（点过的节点都认下来了）");

        // 逐大类锁定「已对账子叶数」。数字来自采集结果；改动映射表时必须同步改这里，避免静默增删。
        (int TopLevel, int LeafCount)[] expected =
            [(13000, 6), (14000, 3), (12000, 3), (33000, 4), (10000, 45), (11000, 25), (32001, 16), (32100, 16), (32200, 1)];
        foreach (var (topLevelId, leafCount) in expected)
        {
            var leaves = AuctionCategoryFilter.Leaves.Where(leaf => leaf.TopLevelId == topLevelId).ToArray();
            That(leaves.Length == leafCount,
                $"已对账大类 {topLevelId} 的子叶数为 {leafCount}");
        }

        // 子叶序号：客户端十进制 id = 父节点 id 的族 + 序号，父节点自身占序号 0。
        // 这条结构是子叶映射的依据（12001 是 12000 的第 1 个子节点，且 D95 抓包确认 12001 = 项链）。
        (int Anchor, int First, int Last)[] ordinals = [(13000, 1, 6), (14000, 1, 3), (12000, 1, 3), (33000, 1, 4)];
        foreach (var (anchor, first, last) in ordinals)
        {
            var got = AuctionCategoryFilter.Leaves.Where(leaf => leaf.TopLevelId == anchor).Select(leaf => leaf.Id % 100).Order().ToArray();
            That(got.SequenceEqual(Enumerable.Range(first, last - first + 1)),
                $"大类 {anchor} 的子叶序号连续覆盖 {first}..{last}，与采集到的子节点逐个对应");
        }
        // 武器/防具的族内序号同样连续覆盖采集清单（101/102 与 111–114 族从 1 起，其余族界面从 2 起）。
        (int Anchor, int First, int Last)[] familyOrdinals =
            [(10100, 1, 6), (10200, 1, 6), (10300, 1, 6), (10400, 1, 6), (10500, 1, 6), (10600, 1, 5), (10800, 1, 5), (10900, 1, 5),
             (11000, 2, 6), (11100, 1, 5), (11200, 1, 5), (11300, 1, 5), (11400, 1, 5)];
        foreach (var (anchor, first, last) in familyOrdinals)
        {
            var got = AuctionCategoryFilter.Leaves.Where(leaf => leaf.Id / 100 * 100 == anchor).Select(leaf => leaf.Id % 100).Order().ToArray();
            That(got.SequenceEqual(Enumerable.Range(first, last - first + 1)),
                $"武器/防具族 {anchor} 的子叶序号连续覆盖 {first}..{last}，与界面顺序逐个对应");
        }

        // 子叶只声明「能取到」的维度：装备类走部位，物品类走 ItemKind，不允许两者皆空（那等于不过滤）。
        That(AuctionCategoryFilter.Leaves.All(leaf => leaf.Dimensions.Kinds != 0 || leaf.Dimensions.Slots != 0),
            "每个已对账子叶都至少有一个真实维度，不存在「认了 id 却不过滤」的空转");
        That(AuctionCategoryFilter.Leaves.All(leaf => !string.IsNullOrWhiteSpace(leaf.Evidence)),
            "每个已对账子叶都写明了依据，便于实机复核");

        // 未对账大类必须显式登记原因：否则「不过滤」会被误读成漏映射。
        var unreconciled = AuctionCategoryFilter.Unreconciled.Select(entry => entry.Id).ToHashSet();
        That(new[] { 10000, 11000, 32001, 32100, 31000 }.All(unreconciled.Contains),
            "武器/防具/辅助装备/魔法石/设计图五个未对账大类的 id 空间都登记了原因");
        That(AuctionCategoryFilter.Unreconciled.All(entry => entry.Reason.Length != 0),
            "未对账大类的日志说明非空，服务端能解释为什么点了子叶只按大类过滤");

        // 未对账大类的子叶必须落回所属顶层维度，而不是不过滤。
        foreach (int leaf in new[] { 10101, 11101, 32002, 33001 })
        {
            var resolution = AuctionCategoryFilter.Resolve(leaf);
            That(resolution.Status == AuctionCategoryStatus.Mapped &&
                (resolution.Dimensions.Kinds != 0 || resolution.Dimensions.Slots != 0),
                $"已采集的子叶 {leaf} 落回所属顶层维度（不是「不过滤」）");
        }
        // 设计图是唯一「整段没有维度依据」的大类：顶层与子叶都不过滤，如实暴露而不是猜一个维度。
        That(AuctionCategoryFilter.Resolve(31000).Status == AuctionCategoryStatus.Mapped &&
            AuctionCategoryFilter.Resolve(31002).Status == AuctionCategoryStatus.Mapped &&
            AuctionCategoryFilter.Resolve(31000).Dimensions.Slots == 0 &&
            AuctionCategoryFilter.Resolve(31002).Dimensions.Slots == 0 &&
            AuctionCategoryFilter.Resolve(31002).Note!.Contains("设计图", StringComparison.Ordinal),
            "设计图（31000/31002）保持不过滤，且日志写明原因是「该大类无维度依据」");
        // 32xxx 的子叶必须落回具体的那个顶层：321xx（魔法石）走 32100 的 23，320xx（辅助装备）走 32001 的 22。
        That(AuctionCategoryFilter.Resolve(32002).MappedId == 32001 &&
            AuctionCategoryFilter.Resolve(32002).Dimensions.Slots == AuctionCategoryFilter.Resolve(32001).Dimensions.Slots &&
            AuctionCategoryFilter.Resolve(32116).MappedId == 32100 &&
            AuctionCategoryFilter.Resolve(32116).Dimensions.Slots == AuctionCategoryFilter.Resolve(32100).Dimensions.Slots,
            "特殊装备子叶按 id 区间归族：32002→32001（辅助装备 22）、32116→32100（魔法石 23）");
    }

    static void SpecialAndTitleLeavesAndSort()
    {
        using var f = new Fixture();
        f.Categories.PrefillSlots(new Dictionary<int, int>
        {
            [114014003] = 14, // 板甲上衣
            [114001201] = 12, // 魔枪士武器
            [116170801] = 22, // 鬼剑士辅助装备
            [116170802] = 22, // 枪剑士辅助装备
            [118190901] = 23, // 鬼剑士魔法石
            [118190902] = 23, // 枪剑士魔法石
            [109600042] = 13  // 鬼剑士称号
        });
        f.Categories.PrefillJobs(new Dictionary<int, ushort>
        {
            [114001201] = 1 << 13, // demonic lancer
            [116170801] = 1 << 0,  // swordman
            [116170802] = 1 << 15, // gun blader
            [118190901] = 1 << 0,  // swordman
            [118190902] = 1 << 15, // gun blader
            [109600042] = 1 << 0   // swordman
        });
        f.Categories.PrefillAttributes(new Dictionary<int, (int Rarity, int MinimumLevel)>
        {
            [116170801] = (2, 60),
            [116170802] = (4, 90)
        });
        f.Repository.InsertListings(
        [
            Draft(null, "板甲上衣", 114014003, f.Now) with { StartingPrice = 50_000, BuyoutPrice = 80_000 },
            Draft(null, "魔枪武器", 114001201, f.Now) with { StartingPrice = 20_000, BuyoutPrice = 30_000 },
            Draft(null, "剑士辅装", 116170801, f.Now) with { StartingPrice = 40_000, BuyoutPrice = 60_000 },
            Draft(null, "枪剑辅装", 116170802, f.Now) with { StartingPrice = 10_000, BuyoutPrice = 20_000 },
            Draft(null, "剑士魔石", 118190901, f.Now) with { StartingPrice = 30_000, BuyoutPrice = 50_000 },
            Draft(null, "枪剑魔石", 118190902, f.Now) with { StartingPrice = 15_000, BuyoutPrice = 25_000 },
            Draft(null, "耳环", 108090404, f.Now) with { StartingPrice = 35_000, BuyoutPrice = 55_000 },
            Draft(null, "剑士称号", 109600042, f.Now) with { StartingPrice = 45_000, BuyoutPrice = 70_000 }
        ]);
        // 114 消歧：板甲子叶 11401 命中板甲上衣（不命中魔枪武器），魔枪子叶 10801 命中魔枪武器（不命中板甲上衣）。
        That(f.Search(11401, 0).Listings.Single().SellerName == "板甲上衣" &&
            f.Search(11400, 0).Listings.Single().SellerName == "板甲上衣" &&
            f.Search(10801, 0).Listings.Single().SellerName == "魔枪武器",
            "114 号段消歧：板甲子叶 11401 与板甲材质节点 11400 命中板甲上衣，魔枪子叶 10801 命中魔枪武器");
        // 辅助装备职业子叶（32002 鬼剑士(男) vs 32017 枪剑士）与魔法石职业子叶（32101 vs 32116）。
        That(f.Search(32002, 0).Listings.Single().SellerName == "剑士辅装" &&
            f.Search(32017, 0).Listings.Single().SellerName == "枪剑辅装" &&
            f.Search(32101, 0).Listings.Single().SellerName == "剑士魔石" &&
            f.Search(32116, 0).Listings.Single().SellerName == "枪剑魔石" &&
            f.Search(32200, 0).Listings.Single().SellerName == "耳环",
            "特殊装备子叶（32002/32017/32101/32116/32200）按职业位与耳环部位精确过滤");
        // 称号职业子叶（12005 鬼剑士(男) vs 12006 鬼剑士(女)）。
        That(f.Search(12005, 0).Listings.Single().SellerName == "剑士称号" &&
            f.Search(12006, 0).TotalCount == 0,
            "称号职业子叶（12005..12018）按部位 13 + 职业位精确过滤");
        // 排序与品级/等级过滤。
        var sortedAsc = f.Service.Search(new(0, null, 32001) { SortOrder = AuctionSortOrder.PriceAsc }, null, f.Now);
        var sortedDesc = f.Service.Search(new(0, null, 32001) { SortOrder = AuctionSortOrder.PriceDesc }, null, f.Now);
        That(sortedAsc.Listings.Select(r => r.SellerName).SequenceEqual(["枪剑辅装", "剑士辅装"]) &&
            sortedDesc.Listings.Select(r => r.SellerName).SequenceEqual(["剑士辅装", "枪剑辅装"]),
            "搜索支持按价格升序/降序排序");
        var byRarity = f.Service.Search(new(0, null, 32001) { Rarity = 4, MinLevel = 85, MaxLevel = 95 }, null, f.Now);
        That(byRarity.TotalCount == 1 && byRarity.Listings.Single().SellerName == "枪剑辅装",
            "搜索支持按品级与穿戴等级区间过滤");
    }

    /// <summary>
    /// 种子候选池：覆盖面（武器/防具/材料）与排除项。**模板能否解析**由启动带 --auction-seed 的
    /// 独立实例核对（日志 UnresolvedItemIds），核心自测没有 PVF，不在这里伪造解析结果。
    /// </summary>
    static void SeedPool()
    {
        var pool = AuctionService.SeedPool;
        var ids = new[] { 101010912, 102040571, 106020596, 106000064, 10001, 10400, 12400, 16400, 18400, 3037, 3262, 10014751 };
        That(pool.Count >= 8 && pool.Distinct().Count() == pool.Count && pool.All(id => id > 0),
            "候选池非空且模板 id 互不重复");
        That(pool.Contains(101010912) && pool.Contains(102040571) && pool.Contains(106020596) && pool.Contains(106000064),
            "候选池覆盖多件武器");
        That(pool.Contains(10001) && pool.Contains(10400) && pool.Contains(12400) && pool.Contains(16400) && pool.Contains(18400),
            "候选池覆盖布甲/皮甲的上衣、下装、腰带与鞋");
        That(pool.Contains(3037) && pool.Contains(3262) && pool.Contains(10014751), "候选池覆盖消耗品/材料与既有候选物品");
        That(ids.All(pool.Contains) && pool.Count == ids.Length, "候选池与实机核对清单一致，不多不少");
        That(pool.Contains(106020596) && pool.Contains(101010912), "原有候选物品在扩充后仍保留");
        // 3037/3262 是普通材料（小晶块），不是钱包物品；账号钱包货币是 Main 容器 354–365 的虚拟槽，禁止上架。
        That(!pool.Any(id => id is 3033 or 3034 or 3035 or 3036), "候选池不含账号钱包货币模板（3033–3036）");
    }

    static void InvalidDraft()
    {
        using var f = new Fixture();
        Reject<InvalidDataException>(() => f.Service.Search(new(-1, null, AuctionCategoryFilter.All), null, f.Now), "负的翻页偏移拒绝");
        Reject<InvalidDataException>(() => f.Service.Seed(1, f.Now, []), "解析不到任何候选物品时明确失败");
        f.Repository.InsertListings([]);
        That(f.Snapshot() == "", "空批次不写任何行");
        Reject<InvalidDataException>(() => f.Repository.InsertListings([Draft(null, "  ", 101010912, f.Now)]), "空卖家名拒绝");
        Reject<InvalidDataException>(() => f.Repository.InsertListings(
            [Draft(null, "合法", 101010912, f.Now), Draft(null, "非法", 101010912, f.Now) with { BuyoutPrice = -2 }]), "越界的一口价拒绝");
        Reject<InvalidDataException>(() => f.Repository.InsertListings(
            [Draft(null, "合法", 101010912, f.Now), Draft(null, "非法", 101010912, f.Now) with { ItemEntry = [] }]), "空条目快照拒绝");
        That(f.Snapshot() == "", "整批校验失败时同一事务回滚，不留半批数据");
    }

    static AuctionSeedItem Item(int templateId, ItemKind kind, int count)
    {
        var core = new ItemCore(templateId, kind, kind == ItemKind.Equipment ? InventoryRewardGrantService.InitialQuality : count)
        {
            Attr = 2,
            Durability = 45,
            SealFlag = 1
        };
        return new(templateId, (byte)kind, count, AuctionPackets.ItemEntrySnapshot(core));
    }

    static AuctionListingDraft Draft(long? seller, string name, int templateId, long now) => new(seller, name, templateId,
        (byte)ItemKind.Equipment, 1,
        AuctionPackets.ItemEntrySnapshot(new ItemCore(templateId, ItemKind.Equipment, InventoryRewardGrantService.InitialQuality)),
        12_000, 24_000, now, now + 86_400);

    /// <summary>可堆叠物品的挂牌（材料/消耗品）：种类与数量都进快照，拍卖行应答的道具块按它照抄。</summary>
    static AuctionListingDraft Stackable(long? seller, string name, int templateId, long now, ItemKind kind, int count) =>
        new(seller, name, templateId, (byte)kind, count, AuctionPackets.ItemEntrySnapshot(new ItemCore(templateId, kind, count)),
            12_000, 24_000, now, now + 86_400);

    /// <summary>
    /// 武器/防具子叶的**精确映射**（族 × 族内序号）。依据两类证据，强度不同：
    ///   1. 族 ↔ 职业/材质：用户按界面顺序给出的节点名，与 id 升序的族一一对应（未逐节点抓包）；
    ///   2. 族内序号 ↔ 武器类型/部位：本机 PVF 物品组（装备模板 id 的数字段就是物品组，见
    ///      <see cref="AuctionCategoryFilter.ItemGroupMask"/>），族内组名升序与序号一一对应。
    /// 因此这里逐族锁定「子叶维度声明的族掩码」与「模板 id 判定出的族掩码」必须一致：
    /// 换序时族表与 id 对应关系必须同时改，这个断言会立刻发现只改了一边。
    /// </summary>
    static void WeaponArmorLeaves()
    {
        // 武器：族掩码 + 职业位 + 部位 12。族内 5–6 个子节点的维度**相同**（族内武器类型取不到，见下）。
        That(AuctionCategoryFilter.Resolve(10101) is { Status: AuctionCategoryStatus.Mapped, Name: "短剑" } swordman &&
            swordman.MappedId == 10000 && swordman.Dimensions.SlotList.SequenceEqual(new byte[] { 12 }) &&
            swordman.Dimensions.Groups == AuctionCategoryFilter.WeaponGroupMask(AuctionCategoryFilter.WeaponGroup.BitSwordman) &&
            swordman.Dimensions.Jobs == AuctionCategoryFilter.WeaponJobs(AuctionCategoryFilter.WeaponGroup.BitSwordman),
            "武器子叶 10101 = 族 101 掩码 + 鬼剑士职业位 + 部位 12");
        That(AuctionCategoryFilter.Resolve(10201).Dimensions.Groups == AuctionCategoryFilter.WeaponGroupMask(AuctionCategoryFilter.WeaponGroup.BitFighter) &&
            AuctionCategoryFilter.Resolve(10301).Dimensions.Groups == AuctionCategoryFilter.WeaponGroupMask(AuctionCategoryFilter.WeaponGroup.BitGunner) &&
            AuctionCategoryFilter.Resolve(10401).Dimensions.Groups == AuctionCategoryFilter.WeaponGroupMask(AuctionCategoryFilter.WeaponGroup.BitMage) &&
            AuctionCategoryFilter.Resolve(10501).Dimensions.Groups == AuctionCategoryFilter.WeaponGroupMask(AuctionCategoryFilter.WeaponGroup.BitPriest) &&
            AuctionCategoryFilter.Resolve(10601).Dimensions.Groups == AuctionCategoryFilter.WeaponGroupMask(AuctionCategoryFilter.WeaponGroup.BitThief) &&
            AuctionCategoryFilter.Resolve(10801).Dimensions.Groups == AuctionCategoryFilter.WeaponGroupMask(AuctionCategoryFilter.WeaponGroup.BitLancer) &&
            AuctionCategoryFilter.Resolve(10901).Dimensions.Groups == AuctionCategoryFilter.WeaponGroupMask(AuctionCategoryFilter.WeaponGroup.BitGunBlader),
            "8 个武器族各自映射到独立掩码（101→剑士、102→格斗家、103→神枪手、104→魔法师、105→圣职者、106→暗夜使者、108→魔枪士、109→枪剑士）");
        // 族内细分（短剑/太刀…）在现有数据里取不到：模板没有 [item group name]，模板 id 的族内分段
        // 与组名不是同一个序。**取不到的维度不编**，因此同族子节点维度相同（只按族+职业+部位过滤）。
        That(AuctionCategoryFilter.Resolve(10101).Dimensions == AuctionCategoryFilter.Resolve(10102).Dimensions &&
            AuctionCategoryFilter.Resolve(10102).Dimensions == AuctionCategoryFilter.Resolve(10106).Dimensions,
            "同族子节点维度相同：族内武器类型未定案，子叶只按族+职业+部位过滤（不猜短剑/太刀）");
        var weaponRoot = AuctionCategoryFilter.Resolve(10000).Dimensions;
        ushort unionJobs = AuctionCategoryFilter.WeaponJobs(AuctionCategoryFilter.WeaponGroup.BitSwordman);
        for (int index = 0; index < 8; index++)
            unionJobs |= AuctionCategoryFilter.WeaponJobs(index);
        That(weaponRoot.Groups == AuctionCategoryFilter.AllWeaponGroups &&
            weaponRoot.SlotList.SequenceEqual(new byte[] { 12 }) && unionJobs == weaponRoot.Jobs,
            "武器父节点 = 8 个族的并集 + 部位 12 + 各族职业位（子叶合并，父节点不丢东西）");

        // 防具：材质族掩码 + 族内部位，两个维度都要。
        That(AuctionCategoryFilter.Resolve(11002) is { Status: AuctionCategoryStatus.Mapped, Name: "布甲·上衣" } cloth &&
            cloth.Dimensions.SlotList.SequenceEqual(new byte[] { 14 }) &&
            cloth.Dimensions.Groups == AuctionCategoryFilter.ArmorGroupMask(0),
            "防具子叶 11002 同时声明材质（族 110 布甲）与部位（14 上衣），缺一维就会串材质或串部位");
        That(AuctionCategoryFilter.Resolve(11204).Dimensions.SlotList.SequenceEqual(new byte[] { 17 }) &&
            AuctionCategoryFilter.Resolve(11204).Dimensions.Groups == AuctionCategoryFilter.ArmorGroupMask(2),
            "轻甲·鞋（11204）= 材质 112 + 部位 17");
        That(AuctionCategoryFilter.Resolve(11101).Dimensions.Groups == AuctionCategoryFilter.ArmorGroupMask(1) &&
            AuctionCategoryFilter.Resolve(11301).Dimensions.Groups == AuctionCategoryFilter.ArmorGroupMask(3) &&
            AuctionCategoryFilter.Resolve(11401).Dimensions.Groups == AuctionCategoryFilter.ArmorGroupMask(4),
            "皮甲/重甲/板甲分别映射到材质 111/113/114");
        That(AuctionCategoryFilter.Resolve(11000).Dimensions.Groups == AuctionCategoryFilter.AllArmorGroups &&
            AuctionCategoryFilter.Resolve(11000).Dimensions.SlotList.SequenceEqual(new byte[] { 14, 15, 16, 17, 18 }),
            "防具父节点 = 5 个材质族的并集 + 五个防具部位");
        That(AuctionCategoryFilter.Resolve(11000).Dimensions.Groups != AuctionCategoryFilter.Resolve(11002).Dimensions.Groups,
            "防具父节点与子叶的族掩码口径不同（父节点收 5 族、子叶只收 1 族）");
        // 同族内 5 个部位子叶的维度两两不同（材质相同、部位不同），点「布甲上衣」不会列出下装。
        That(AuctionCategoryFilter.Resolve(11002).Dimensions != AuctionCategoryFilter.Resolve(11004).Dimensions,
            "同族不同部位子叶的维度不同（部位维度把它们分开）");

        // 族掩码来自模板 id 的族号：与目录无关（所以坏模板不会把整行判错）。
        int swordTemplate = AuctionCategoryFilter.EquipmentTemplateId(101, 0, 12);
        That(AuctionCategoryFilter.ItemGroupMask(swordTemplate) ==
                AuctionCategoryFilter.WeaponGroupMask(AuctionCategoryFilter.WeaponGroup.BitSwordman),
            $"武器模板 {swordTemplate} 落剑士族掩码（101xxx / 1_000_000 = 101）");
        int lightTemplate = AuctionCategoryFilter.ArmorTemplateId(112, 17);
        That(lightTemplate == 112_017_003 && AuctionCategoryFilter.ItemGroupMask(lightTemplate) == AuctionCategoryFilter.ArmorGroupMask(2),
            $"防具模板 {lightTemplate} 落轻甲材质掩码（112_xxx / 1_000_000 = 112 = 轻甲）");
        // 族表逐项自洽：每个族号的模板都落进本族掩码、不会落进别的族。
        int[] weaponFamilies = [101, 102, 104, 106, 108, 109, 114, 116];
        for (int index = 0; index < weaponFamilies.Length; index++)
        {
            ulong mask = AuctionCategoryFilter.ItemGroupMask(AuctionCategoryFilter.EquipmentTemplateId(weaponFamilies[index], 0, 12));
            ulong weaponBit = AuctionCategoryFilter.WeaponGroupMask(index);
            That((mask & weaponBit) == weaponBit && (weaponBit & AuctionCategoryFilter.AllWeaponGroups) == weaponBit,
                $"武器族 {weaponFamilies[index]} 的模板含本族掩码（界面第 {index + 1} 个武器节点），且是并集的子集");
        }
        for (int index = 0; index < 5; index++)
        {
            int material = new[] { 110, 111, 112, 113, 114 }[index];
            ulong mask = AuctionCategoryFilter.ItemGroupMask(AuctionCategoryFilter.ArmorTemplateId(material, 16));
            ulong armorBit = AuctionCategoryFilter.ArmorGroupMask(index);
            if (material == 114)
            {
                That(mask == (AuctionCategoryFilter.WeaponGroupMask(AuctionCategoryFilter.WeaponGroup.BitLancer) | armorBit),
                    "材质 114 与武器族 114 共用号段：ItemGroupMask 同时置魔枪士族位与板甲材质位，由部位 12 vs 14..18 精确消歧");
                continue;
            }
            That(mask == armorBit,
                $"防具族 {material} 的模板落进本材质掩码（界面第 {index + 1} 个防具节点）");
        }
        // 不认识的 id 一律不过滤（宁可粗，不可错）：族 103/105/107（本机 PVF 里没有任何武器条目）、
        // 首饰 1003xx、护石 1004xx 都不落任何族。
        foreach (int unknown in new[] { 103000001, 105000001, 107000001, 100300001, 100400001, 0, 42 })
            That(AuctionCategoryFilter.ItemGroupMask(unknown) == 0, $"未登记族的模板 {unknown} 不落任何族（不过滤）");
        // 武器时装（如 101500001，`weapon avatar`）与实体武器同族：族掩码相同，靠**装备部位**区分。
        That(AuctionCategoryFilter.ItemGroupMask(101500001) ==
                AuctionCategoryFilter.WeaponGroupMask(AuctionCategoryFilter.WeaponGroup.BitSwordman) &&
            AuctionCategoryFilter.Resolve(10101).Dimensions.SlotList.SequenceEqual(new byte[] { 12 }),
            "武器时装 101500001 与实体武器同族掩码；是否进武器子叶由部位维度决定（时装部位 10 ≠ 12）");
        // 职业位来自 ItemCatalog.JobNames：两处清单必须逐项同序，否则「按职业过滤」会静默错位。
        That(AuctionCategoryFilter.JobNames.SequenceEqual(
                ["swordman", "fighter", "gunner", "mage", "priest", "at gunner", "thief", "at fighter",
                 "at mage", "demonic swordman", "creator mage", "at swordman", "knight", "demonic lancer",
                 "at priest", "gun blader"]),
            "职业标签清单与 ItemCatalog.JobNames 逐项同序（位掩码按这个顺序建位）");
        That(AuctionCategoryFilter.JobNames.Length == 16 && AuctionCategoryFilter.JobNames.Distinct().Count() == 16,
            "职业位掩码是 16 位（ushort）：清单长度与去重后条数都为 16");
    }

    /// <summary>
    /// CMD 182 均价查询的数据来源：真实在售挂牌的统计量，不编造定价策略。
    /// 计数与均价都只取「该模板 + 在售 + 未过期」，与搜索同源（同一套过滤条件）。
    /// </summary>
    static void AveragePrice()
    {
        using var f = new Fixture();
        That(f.Service.AveragePrice(101010912, f.Now) == AuctionAveragePrice.None, "没有任何在售时均价为 0、件数为 0（不是编一个价）");
        That(f.Service.AveragePrice(0, f.Now) == AuctionAveragePrice.None, "模板 id 非正时不统计（明确返回无数据）");

        // 12 件挂牌：同一件武器（101010912，族 101）与另一件武器（102040571，族 102）轮换。
        f.Service.Seed(12, f.Now, [Item(101010912, ItemKind.Equipment, 1), Item(102040571, ItemKind.Equipment, 1)]);
        var first = f.Matches(new(101010912, null));
        var second = f.Matches(new(102040571, null));
        That(first.Length == 6 && second.Length == 6, "种子按候选物品轮换，两件武器各 6 条在售");
        int expectedFirst = first.Sum(row => row.DisplayPrice) / first.Length;
        var price = f.Service.AveragePrice(101010912, f.Now);
        That(price.Count == 6 && price.Average == expectedFirst && price.Average > 0,
            "均价 = 该模板当前在售挂牌的显示价平均（向下取整），件数与参与统计的挂牌数一致");
        // 出价参与均价：DisplayPrice 与列表「当前竞拍」列同源（有出价取最高出价）。
        f.Execute($"UPDATE auction_listings SET current_bid=99000 WHERE item_template_id=101010912 AND listing_id={first[0].ListingId}");
        var afterBid = f.Service.AveragePrice(101010912, f.Now);
        That(afterBid.Count == 6 && afterBid.Average > price.Average, "有出价后均价上升（与列表显示价同源，不含未成交的出价之外的字段）");
        // 已成交/已过期不计入：均价必须与搜索口径一致，否则「均价」与「在售列表」会对不上。
        f.Execute("UPDATE auction_listings SET state=1 WHERE item_template_id=101010912");
        That(f.Service.AveragePrice(101010912, f.Now) == AuctionAveragePrice.None, "已成交（state=1）的挂牌不计入均价");
        f.Execute($"UPDATE auction_listings SET state=0, expires_at_unix={f.Now} WHERE item_template_id=101010912");
        That(f.Service.AveragePrice(101010912, f.Now) == AuctionAveragePrice.None, "已过期的挂牌不计入均价");
        f.Execute($"UPDATE auction_listings SET expires_at_unix={f.Now + 1} WHERE item_template_id=101010912");
        That(f.Service.AveragePrice(101010912, f.Now).Count == 6, "未到期的挂牌重新计入均价");
        That(f.Service.AveragePrice(109999999, f.Now) == AuctionAveragePrice.None, "没有挂牌的模板返回无均价");
    }

    /// <summary>
    /// 核心自测没有 PVF，装备部位用 <see cref="Fixture.Slots"/> 这张显式模板表灌进解析器的
    /// 模板缓存（<see cref="AuctionCategoryResolver.PrefillSlots"/>），解析、SQL 条件、内存过滤与分页
    /// 仍然走真实实现；真实目录的部位取值由启动带 --auction-seed 的独立实例核对（见 Docs/模块/拍卖行.md）。
    /// </summary>
    sealed class Fixture : IDisposable
    {
        readonly string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "S5A9-auction-" + Guid.NewGuid().ToString("N"));
        public string Path { get; }
        /// <summary>只读 SQL（分页查询 / 计数 / 批量插入）：本组直接断言 SQL 层面的行为。</summary>
        public AuctionListingRepository Repository { get; }
        /// <summary>
        /// 存储合同的实现方（与 Runtime 装配一致）：上架要与背包扣减共用同一个写事务，因此合同由角色仓储实现。
        /// 本组只走读路径，但仍然从它进 <see cref="AuctionService"/>，避免自测用一条生产上不存在的入口。
        /// </summary>
        public CharacterRepository Store { get; }
        public AuctionService Service { get; }
        public AuctionCategoryResolver Categories { get; }
        /// <summary>空表物品目录与空碎片目录：本组只覆盖读路径，不解析任何模板（见构造器注释）。</summary>
        public ItemCatalog Items { get; }
        public EpicPieceCatalog Pieces { get; } = new("[piece list]\n60000 70001\n[/piece list]");
        /// <summary>模板 id → 装备部位。取值与真实目录一致（取自 <c>ItemCatalog.EquipmentSlots</c> 的
        /// weapon/coat/shoulder/pants/shoes/waist/amulet/ring/wrist/support/magic stone 与 artifact 三色）。</summary>
        static readonly Dictionary<int, int> Slots = new()
        {
            // 武器（四件种子武器分属 101/102/106/106 族）、称号
            [101010912] = 12, [102040571] = 12, [106020596] = 12, [106000064] = 12, [109600041] = 13,
            // 防具五部位
            [10001] = 14, [10400] = 15, [12400] = 16, [18400] = 17, [16400] = 18,
            // 首饰：项链/戒指/手镯/耳环
            [102030101] = 19, [104050202] = 21, [106070303] = 20, [108090404] = 25,
            // 宠物本体（部位 26）
            [120120808] = 26,
            // 特殊装备：辅助装备 / 魔法石
            [116170808] = 22, [118190909] = 23,
            // 宠物装备三色
            [110110505] = 27, [112130606] = 28, [114150707] = 29
        };
        /// <summary>模板 id → 适用职业位（<c>[usable job]</c> 的位掩码）。只有武器子叶判职业，
        /// 因此这里给出武器模板的职业位；其余模板不填，解析不出来时按「不限制职业」处理。</summary>
        static readonly Dictionary<int, ushort> Jobs = new()
        {
            // 三件种子武器都是鬼剑士系（族 101），第四件属族 106（暗夜使者）
            [101010912] = 1 << 0, [102040571] = 1 << 0, [106020596] = 1 << 5, [106000064] = 1 << 5,
            [109600041] = 0
        };

        public long Now { get; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        public Fixture()
        {
            Directory.CreateDirectory(root);
            Path = System.IO.Path.Combine(root, "game.db");
            Repository = new(Path);
            // 本组只覆盖**读路径**（搜索 / 分类 / 均价 / 种子），不解析任何模板，因此目录与碎片目录都用空表：
            // 上架规划（要真模板）在 AuctionRegisterTests 里用 PrefillTemplates 预置的模板跑。
            Items = new ItemCatalog(null!, null!, null!, 0);
            Categories = new(Items);
            Categories.PrefillSlots(Slots);
            Categories.PrefillJobs(Jobs);
            Store = new(Path);
            Service = new(Store, Categories, Items, Pieces, AuctionPackets.ItemEntrySnapshot, AuctionPackets.ItemCoreFromEntry);
        }

        public AuctionSearchPage Search(int categoryId, int offset, int? itemTemplateId = null, long? excludeSeller = null) =>
            Service.Search(new(offset, itemTemplateId, categoryId), excludeSeller, Now);

        /// <summary>仓储的全量匹配查询（带装备部位的分类走这条路），用与分页查询同一套过滤条件。</summary>
        public ImmutableArray<AuctionListing> Matches(AuctionSearchFilter filter) => Repository.LoadMatches(filter, Now);

        public void Execute(string sql)
        {
            using var db = Open();
            using var command = db.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        public string KindRows()
        {
            using var db = Open();
            using var command = db.CreateCommand();
            command.CommandText = "SELECT group_concat(item_template_id||'='||item_kind,'|') FROM (SELECT * FROM auction_listings ORDER BY listing_id)";
            return command.ExecuteScalar() as string ?? "";
        }


        public string Snapshot()
        {
            using var db = Open();
            using var command = db.CreateCommand();
            command.CommandText = "SELECT group_concat(listing_id||':'||seller_name||':'||item_template_id||':'||item_count||':'||starting_price||':'||buyout_price||':'||state||':'||expires_at_unix,'|') FROM (SELECT * FROM auction_listings ORDER BY listing_id)";
            return command.ExecuteScalar() as string ?? "";
        }

        static SqliteConnection Open(string path)
        {
            var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, ForeignKeys = true }.ToString());
            db.Open();
            return db;
        }

        SqliteConnection Open() => Open(Path);
        public void Dispose() => TestFiles.DeleteDirectory(root, true);
    }
}
