using static DfoServer.Testing.TestCheck;
using System.Collections.Immutable;
using DfoServer.Development.Auction;
using DfoServer.Game.Accounts;
using DfoServer.Game.Auction;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;
using DfoServer.Network.Builders;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;

namespace DfoServer;

/// <summary>
/// 「我的拍卖品」三个页签（CMD 188 我的上架 / 189 我的竞价 / 190 拍卖历史）：
/// 请求解析、应答字节、数据来源与候选轮换。
///
/// 分四组：
///   1. <see cref="Request"/>：请求体恒 1 字节 = 窗口 mode；0/1/其它值原样解析，空体回落 0 并标记；
///   2. <see cref="Wire"/>：`flag + mode(回显) + count(1B/2B) + N×记录` 的字节，
///      **首字节必须是 flag**、空列表也必须发满头部、189 用 WORD 计数、记录数与体长三者一致；
///   3. <see cref="Data"/>：188 只取本角色的在售未过期挂牌（别人的、已过期/非在售的都排除），190 取全部状态；
///   4. <see cref="Candidates"/>：候选自身的安全性（体长下限、道具块放得下）、探针按页签各自轮换、非法候选拒收。
///
/// 记录尺寸与道具块偏移来自另一个客户端版本（D95 未验证），因此这里锁的是**结构自洽与安全底线**，
/// 「哪一种能让面板显示出记录」只能由实机判定。
/// </summary>
internal static class AuctionMyListTests
{
    /// <summary>普通拍卖行窗口：本客户端的「我的拍卖品」页签就在这个窗口里。</summary>
    const byte NormalMode = AuctionMyListReply.NormalWindowMode;

    public static void Run()
    {
        int checks = Count;
        Request();
        Wire();
        Data();
        Candidates();
        Console.WriteLine($"PASS {Count - checks} auction my-list assertions");
    }

    /// <summary>请求体解析：恒 1 字节的窗口 mode，0/1/其它值都原样带回（回显是约定），空体回落 0。</summary>
    static void Request()
    {
        That(AuctionMyListRequest.Parse([NormalMode]).Mode == NormalMode && !AuctionMyListRequest.Parse([NormalMode]).Fallback,
            "mode=0（普通拍卖行窗口）原样解析，不标回落");
        That(AuctionMyListRequest.Parse([AuctionMyListReply.ConsignmentWindowMode]).Mode == AuctionMyListReply.ConsignmentWindowMode,
            "mode=1（金币寄售窗口）原样解析");
        // 其它取值不做校验、不纠正：应答回显请求首字节本身就是约定，客户端认不认由它决定，
        // 服务端只把原值带回去（纠正会把记录送进另一个窗口类的面板）。
        That(AuctionMyListRequest.Parse([7]).Mode == 7, "未登记的 mode 也原样回显（服务端不替客户端改窗口类型）");
        var empty = AuctionMyListRequest.Parse([]);
        That(empty.Mode == NormalMode && empty.Fallback, "空请求体回落到普通拍卖行窗口并标记回落（页签不能被静默忽略）");
        That(AuctionMyListRequest.Parse([NormalMode, 0x00, 0x00]).Mode == NormalMode && !AuctionMyListRequest.Parse([NormalMode]).Fallback,
            "请求体多出的尾部字节不参与解析（只取首字节，与 182 同款的版本容错）");
    }

    /// <summary>应答字节：flag 在体首、mode 回显、计数宽度、记录字段与体长。</summary>
    static void Wire()
    {
        var core = new ItemCore(SampleTemplate, ItemKind.Equipment, 999_999_998) { Attr = 2, Durability = 45 };
        var listing = new AuctionListing(0x11223344, 7, "MyLister", SampleTemplate, (byte)ItemKind.Equipment, 1,
            AuctionPackets.ItemEntrySnapshot(core), 12_000, 17_000, 0, 1_700_000_000, 1_700_086_400);

        // ① 空列表：最小体长 = flag + mode + count，且必须发满（页签「点了没反应」就是从这里开始修的）。
        var empty151 = AuctionPackets.MyListReply(NormalMode, [], AuctionMyListReply.Registed151, 1_700_000_000);
        Bytes(empty151, [0x01, 0x00, 0x00], "★ 188 空列表 = flag(01) + mode(00) + count(00) 共 3 字节");
        var empty159 = AuctionPackets.MyListReply(NormalMode, [], AuctionMyListReply.Bidding159, 1_700_000_000);
        Bytes(empty159, [0x01, 0x00, 0x00, 0x00], "189 空列表 = flag + mode + count(WORD) 共 4 字节");
        That(AuctionMyListReply.Registed151.MinimumBodyBytes == 3 && AuctionMyListReply.Bidding159.MinimumBodyBytes == 4,
            "安全底线由候选自己声明：空列表的最小体长 = flag + mode + count");

        // ② mode 回显：0/1 都要原样回来（回错窗口 ⇒ 记录进另一类面板、界面恒空）。
        That(AuctionPackets.MyListReply(AuctionMyListReply.ConsignmentWindowMode, [], AuctionMyListReply.Registed151, 0)[1] == 1,
            "★ mode 回显请求首字节（1 = 金币寄售窗口）");
        That(AuctionPackets.MyListReply(7, [], AuctionMyListReply.Registed151, 0)[1] == 7, "未登记的 mode 也回显");

        // ③ 188 记录：151B 步长、道具块 @30、字段偏移与搜索合同同源（同一份挂牌数据）。
        var one = AuctionPackets.MyListReply(NormalMode, [listing], AuctionMyListReply.Registed151, 1_700_000_000);
        That(one.Length == 3 + 151 && one[2] == 1, "★ 188 一条记录 = 3 + 1×151");
        That(BitConverter.ToInt32(one, 3 + 0) == 0x11223344, "记录 [0..3] = 挂牌 id");
        That(BitConverter.ToInt32(one, 3 + 4) == 1_700_086_400, "记录 [4..7] = 到期时间");
        That(BitConverter.ToInt32(one, 3 + 8) == 12_000, "记录 [8..11] = 单价（界面价格列）");
        That(BitConverter.ToInt32(one, 3 + 12) == 17_000, "记录 [12..15] = 一口价总额（单价 × 数量）");
        That(one.Skip(3 + 16).Take(13).All(value => value == 0), "记录 [16..28] 的 13B 字符串保持 0");
        var block = one.AsSpan(3 + 30, 83);
        That(block[0] == (byte)ItemKind.Equipment && BitConverter.ToInt32(block[1..]) == SampleTemplate,
            "★ 188 道具块 @30：块首是物品种类、itemId 在块内 [1..4]");
        That(BitConverter.ToInt32(block[6..]) == 999_999_998 && block[5] == 2, "块内 value @6、attr @5（与普通条目反排）");
        That(BitConverter.ToInt32(one, 3 + 143) == 86_400, "记录 [143..146] = 剩余秒数（到期 − 当前）");
        That(one[0] == 0x01, "★ 首字节必须是 flag（本仓库帧层不补这一字节）");

        // ④ 189 记录：159B 步长、道具块 @38、count 是 WORD。
        var bidding = AuctionPackets.MyListReply(AuctionMyListReply.ConsignmentWindowMode, [listing], AuctionMyListReply.Bidding159, 1_700_000_000);
        That(bidding.Length == 4 + 159 && BitConverter.ToUInt16(bidding, 2) == 1, "★ 189 一条记录 = 4 + 1×159，count 是 WORD");
        That(BitConverter.ToInt32(bidding, 4 + 12) == 17_000, "189 记录 [12..15] 同样是一口价总额");
        var biddingBlock = bidding.AsSpan(4 + 38, 83);
        That(biddingBlock[0] == (byte)ItemKind.Equipment && BitConverter.ToInt32(biddingBlock[1..]) == SampleTemplate,
            "★ 189 道具块 @38（与 188 的 30 不同，两者不能互相套用）");
        That(BitConverter.ToInt32(bidding, 4 + 151) == 86_400, "189 记录 [151..154] = 剩余秒数");

        // ⑤ 一口价总额：单价 × 数量，纯竞拍原样 -1（写 0 会被判成「出价高于一口价」）。
        var stack = listing with { ItemCount = 3, BuyoutPrice = 5_000 };
        That(BitConverter.ToInt32(AuctionPackets.EncodeMyListing(stack, AuctionMyListReply.Registed151, 0), 12) == 15_000,
            "一口价总额 = 单价 × 数量（这两个面板读的是总额，不是单价）");
        That(BitConverter.ToInt32(AuctionPackets.EncodeMyListing(listing with { BuyoutPrice = -1 }, AuctionMyListReply.Registed151, 0), 12) == -1,
            "纯竞拍的一口价总额写 -1（不是 0）");
        That(BitConverter.ToInt32(AuctionPackets.EncodeMyListing(listing with { BuyoutPrice = int.MaxValue, ItemCount = 4 },
            AuctionMyListReply.Registed151, 0), 12) == int.MaxValue,
            "总额溢出时夹到 int.MaxValue（不写成负数）");
        // 参考实现的跳过判定：wire[0] | wire[4] 同时为 0 时整行被隐藏 ⇒ 真实挂牌必须至少一个非 0。
        var realRecord = AuctionPackets.EncodeMyListing(listing, AuctionMyListReply.Registed151, 0);
        That(BitConverter.ToInt32(realRecord, 0) != 0 || BitConverter.ToInt32(realRecord, 4) != 0,
            "★ 每条记录的首字段至少一个非 0（参考实现里两者都为 0 时整行被隐藏）");
        That(AuctionMyListReply.RowVisible(1, 0) && AuctionMyListReply.RowVisible(0, 1) && !AuctionMyListReply.RowVisible(0, 0),
            "可见性判定按「挂牌 id 或到期时间非 0」");
        Reject<InvalidDataException>(() => AuctionPackets.EncodeMyListing(listing with { ListingId = 0, ExpiresAtUnix = 0 },
            AuctionMyListReply.Registed151, 0), "两个首字段都为 0 的占位行被编码器拒绝（不会发出一行永远不显示的记录）");
        That(AuctionPackets.MyListReply(NormalMode, [listing with { ListingId = 0, ExpiresAtUnix = 0 }],
            AuctionMyListReply.HeadOnlySentinel, 0).Length == 3, "哨兵候选不发行记录，因此不受可见性判定约束");

        // ⑥ 计数容量：188 的 count 是 u8、189 是 u16 —— 超容量时按计数宽度限幅，体长仍与 count 一致。
        var many = Enumerable.Repeat(listing, 300).ToArray();
        var capped = AuctionPackets.MyListReply(NormalMode, many, AuctionMyListReply.Registed151, 0);
        That(capped[2] == 255 && capped.Length == 3 + 255 * 151, "188 的 u8 计数限幅到 255，且体长与 count 一致");
        var wide = AuctionPackets.MyListReply(NormalMode, many, AuctionMyListReply.Bidding159, 0);
        That(BitConverter.ToUInt16(wide, 2) == 300 && wide.Length == 4 + 300 * 159, "★ 189 的 WORD 计数能带 300 条，体长随之");
        That(AuctionMyListReply.Registed151.CountCapacity == 255 && AuctionMyListReply.Bidding159.CountCapacity == 65_535,
            "计数容量由计数宽度决定（u8 / u16）");

        // ⑦ 对照候选：镜像 186 搜索记录的 168B 布局（道具块 @43），以及哨兵候选只回头部。
        var mirror = AuctionPackets.MyListReply(NormalMode, [listing], AuctionMyListReply.Mirror168, 1_700_000_000);
        That(mirror.Length == 3 + 168 && BitConverter.ToInt32(mirror.AsSpan(3 + 43, 83)[1..]) == SampleTemplate,
            "对照候选 mirror-168：168B 记录、道具块 @43（与搜索合同一致）");
        var sentinel = AuctionPackets.MyListReply(NormalMode, many, AuctionMyListReply.HeadOnlySentinel, 0);
        Bytes(sentinel, [0x01, 0x00, 0x00], "哨兵候选即使有挂牌也只回头部（分辨「面板打不开」与「记录读不出」）");

        // ⑧ 每一条候选、每一种 mode 都必须满足「首字节是 flag」这条硬约束。
        foreach (var candidate in AuctionMyListReply.All)
        {
            foreach (byte mode in new byte[] { 0, 1 })
            {
                var body = AuctionPackets.MyListReply(mode, [listing], candidate, 1_700_000_000);
                That(body[0] == 0x01 && body[1] == mode && body.Length >= candidate.MinimumBodyBytes,
                    $"候选 {candidate.Name} 在 mode={mode} 下首字节为 flag 且不小于最小体长");
            }
        }
        That(AuctionMyListReply.ItemBlockSize == AuctionPackets.ItemBlockSize,
            "候选声明的道具块宽度与编码器一致（跨层不漂移）");
    }

    /// <summary>数据来源：188 只取本角色在售未过期的挂牌，190 取该角色全部状态。</summary>
    static void Data()
    {
        using var f = new Fixture();
        long now = f.Now;
        // 本人：一条在售、一条已过期；另一条由别人（机器人，seller 为 NULL）在售。
        long mine = f.Seed(f.Character.Id, f.Character.Name, now - 100, now + 86_400);
        f.Seed(f.Character.Id, f.Character.Name, now - 200_000, now - 100);
        f.Seed(null, "拍卖机器人甲", now - 100, now + 86_400);

        var active = f.Service.SellerListings(f.Character.Id, now, includeInactive: false);
        That(active.Length == 1 && active[0].ListingId == mine,
            "★ 188 的数据 = 本角色 state=0 且未过期的挂牌（已过期与别人的都不在内）");
        var all = f.Service.SellerListings(f.Character.Id, now, includeInactive: true);
        That(all.Length == 2 && all.All(row => row.SellerCharacterId == f.Character.Id),
            "190 的数据 = 本角色全部状态的挂牌（含已过期），且不含别人的");
        That(f.Service.SellerListings(0, now, includeInactive: true).IsEmpty,
            "角色 id 非正时不查询、直接回空（不会把机器人挂牌当成玩家的东西发出去）");

        // 非在售状态（成交/下架）只有 190 看得到：证明两条查询的条件确实分开，而不是同一句 SQL。
        f.Execute("UPDATE auction_listings SET state=2 WHERE listing_id=" + mine);
        That(f.Service.SellerListings(f.Character.Id, now, includeInactive: false).IsEmpty,
            "188 不显示已成交/已下架的挂牌");
        That(f.Service.SellerListings(f.Character.Id, now, includeInactive: true).Length == 2,
            "190 仍显示已成交/已下架的挂牌（「历史」不会被实现成「在售」）");

        // 端到端字节：把真实挂牌按 188 默认候选编码，记录里的字段必须来自库里那条挂牌。
        var reply = AuctionPackets.MyListReply(NormalMode, active, AuctionMyListReply.Registed151, now);
        That(reply.Length == 3 + 151 && reply[2] == 1, "188 用真实在售数据编码：1 条记录");
        That(AuctionMyListReply.Registed151.CountFor(active.Length) == 1, "候选声明的条数与实际取到的挂牌条数一致");
        var record = reply.AsSpan(3, 151);
        var real = active[0];
        That(BitConverter.ToInt32(record[0..]) == real.ListingId && BitConverter.ToInt32(record[4..]) == real.ExpiresAtUnix,
            "记录 [0..3]/[4..7] 取自库里的挂牌 id 与到期时间");
        That(BitConverter.ToInt32(record[8..]) == real.DisplayPrice, "记录 [8..11] = 库里那行的显示价（无出价取起拍价）");
        That(BitConverter.ToInt32(record[12..]) == real.BuyoutPrice * real.ItemCount, "记录 [12..15] = 一口价总额");
        That(BitConverter.ToInt32(record[31..]) == SampleTemplate,
            "★ 道具块 @30 的 itemId（块内 [1..4]）与库里那条挂牌一致（记录体真的来自真实在售数据）");
    }

    /// <summary>候选自身的安全性、探针轮换与非法候选拒收。</summary>
    static void Candidates()
    {
        That(AuctionMyListReply.All.All(candidate => candidate.IsSafe), "候选表里不存在不安全的项（记录放得下道具块、计数宽度合法）");
        That(AuctionMyListReply.All.All(candidate => candidate.Note.Length != 0), "每个候选都写明自己的结构假设与用途");
        That(AuctionMyListReply.Registed151 is { RecordSize: 151, ItemOffset: 30, CountWidth: 1, RemainOffset: 143, HeadOnly: false },
            "188/190 默认候选 = 参考实现 §4.5：151B / 道具块 @30 / count 1B / 剩余秒数 @143");
        That(AuctionMyListReply.Bidding159 is { RecordSize: 159, ItemOffset: 38, CountWidth: 2, RemainOffset: 151 },
            "189 默认候选 = 参考实现 §4.6：159B / 道具块 @38 / count WORD / 剩余秒数 @151");
        That(AuctionMyListReply.DefaultFor(AuctionMyListPanel.Registed) == AuctionMyListReply.Registed151 &&
            AuctionMyListReply.DefaultFor(AuctionMyListPanel.Bidding) == AuctionMyListReply.Bidding159 &&
            AuctionMyListReply.DefaultFor(AuctionMyListPanel.History) == AuctionMyListReply.Registed151,
            "默认候选按页签取：188/190 用 151B（190 参考实现按 188 镜像），189 用 159B");
        That(AuctionMyListReply.Registed151NoRemain.RemainOffset < 0 && AuctionMyListReply.Registed151NoRemain.IsSafe,
            "对照候选 a21-151-noremain 不写剩余秒数（分辨「剩余时间字段写坏整行」）");

        // 安全底线：不自洽的候选既进不了候选表、也编码不出来。
        var tooShort = new AuctionMyListReply("unsafe-short", 40, 30, 1, -1, false, "自测用：记录放不下 83B 道具块");
        That(!tooShort.IsSafe, "记录放不下道具块的候选被自身安全判定标为不安全");
        Reject<InvalidDataException>(() => AuctionMyListReply.Validate(tooShort), "安全校验直接拒绝不安全候选");
        Reject<InvalidDataException>(() => AuctionPackets.MyListReply(NormalMode, [], tooShort, 0), "编码器拒绝不安全候选");
        Reject<InvalidDataException>(() => AuctionMyListReply.Validate(null), "缺少布局候选时拒绝编码");
        var badCount = new AuctionMyListReply("unsafe-count", 151, 30, 3, -1, false, "自测用：计数宽度只能是 1 或 2");
        Reject<InvalidDataException>(() => AuctionMyListReply.Validate(badCount), "计数宽度 3 的候选被拒绝");
        var badRemain = new AuctionMyListReply("unsafe-remain", 151, 30, 1, 148, false, "自测用：剩余秒数字段超出记录");
        Reject<InvalidDataException>(() => AuctionMyListReply.Validate(badRemain), "剩余秒数字段越界的候选被拒绝");
        That(AuctionMyListReply.Find("nope") is null && AuctionMyListReply.Find("head-only") == AuctionMyListReply.HeadOnlySentinel,
            "候选按名字可达；未登记的名字查不到");
        That(AuctionMyListReply.Find("A21-151") == AuctionMyListReply.Registed151, "候选名大小写不敏感");

        // 探针：mylist= 只轮换「我的拍卖品」，且每个页签各有独立下标（点 188 不推进 189/190）。
        var probe = AuctionProbe.Parse("mylist=a21-151,head-only");
        That(probe.NextMyList(AuctionMyListPanel.Registed).MyList.Name == "a21-151" &&
            probe.NextMyList(AuctionMyListPanel.Registed).MyList.Name == "head-only",
            "mylist= 候选按顺序轮换（实机每点一次页签换一种记录布局）");
        That(probe.NextMyList(AuctionMyListPanel.Registed).MyList.Name == "a21-151", "我的拍卖品候选用完一轮后循环");
        That(probe.NextMyList(AuctionMyListPanel.Bidding).MyList.Name == "a21-151",
            "★ 页签各自的下标：点「我的上架」不推进「我的竞价」的候选");
        That(probe.NextMyList(AuctionMyListPanel.History).MyList.Name == "a21-151" &&
            probe.NextMyList(AuctionMyListPanel.History).MyList.Name == "head-only",
            "190 的候选也从自己的第一个开始，第二次请求才前进到第二个（与 188 互不干扰）");
        var searchFirst = AuctionProbe.Parse("presets=entry,core;avg=zero8;mylist=head-only");
        searchFirst.NextMyList(AuctionMyListPanel.Registed);
        That(searchFirst.NextSearch().Layout.Name == "entry" && searchFirst.NextAverage().Average.Name == "zero8" &&
            searchFirst.NextMyList(AuctionMyListPanel.Registed).MyList.Name == "head-only",
            "推进「我的拍卖品」候选不影响搜索/均价候选的下标");
        That(AuctionProbe.Parse("off").NextMyList(AuctionMyListPanel.Registed).MyList.Name == "a21-151" &&
            AuctionProbe.Parse(null).NextMyList(AuctionMyListPanel.Bidding).MyList.Name == "a21-159",
            "探针关闭时按页签取默认候选（188 → 151B、189 → 159B）");
        That(AuctionProbe.Parse("rows=1").NextMyList(AuctionMyListPanel.History).MyList.Name == "a21-151",
            "开启探针但未给 mylist= 时同样取默认候选");
        Reject<InvalidDataException>(() => AuctionProbe.Parse("mylist=nope"), "未知的「我的拍卖品」候选被拒绝");
        Reject<InvalidDataException>(() => AuctionProbe.Parse("mylist=unsafe-short"),
            "不在候选表里的（不安全）布局名点不到");
        var unknown = AuctionProbe.Parse("mylist=head-only");
        Reject<InvalidDataException>(() => unknown.NextMyList((AuctionMyListPanel)200), "未知页签在轮换前被拒绝");
    }

    const int SampleTemplate = 10400;

    /// <summary>真实文件库 + 一个真实角色 + 一条挂牌的最小夹具（不需要 PVF：模板只用于构造条目字节）。</summary>
    sealed class Fixture : IDisposable
    {
        readonly string root = Path.Combine(Path.GetTempPath(), "S5A9-auction-mylist-" + Guid.NewGuid().ToString("N"));
        readonly AccountOwners owners = new();
        readonly AccountOwners.Lease owner;

        public CharacterRepository Repository { get; }
        public AuctionService Service { get; }
        public Character Character { get; }
        public long Now { get; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        public Fixture()
        {
            Directory.CreateDirectory(root);
            string path = Path.Combine(root, "game.db");
            owner = owners.Acquire("my-list", () => { });
            Repository = new(path);
            // 背包里放一件装备实例：本组只验证「谁的行被取出来」，不涉及上架资格判定。
            var inventory = new InventorySnapshot([
                new(0, new(InventoryListType.Main, 27), new ItemCore(SampleTemplate, ItemKind.Equipment, 999_999_998) { Attr = 2, Durability = 45 })
            ]);
            Character = Repository.Create(owner, new("MyLister", new(38, 1, 450, 234, 0), null) { Level = 95, Inventory = inventory });
            var catalog = new ItemCatalog(null!, null!, null!, 0);
            Service = new AuctionService(Repository, new AuctionCategoryResolver(catalog), catalog,
                new EpicPieceCatalog("[piece list]\n60000 70001\n[/piece list]"), AuctionPackets.ItemEntrySnapshot, AuctionPackets.ItemCoreFromEntry);
        }

        /// <summary>插入一条挂牌并返回它的 listing_id（走真实的批量插入入口）。</summary>
        public long Seed(long? sellerCharacterId, string sellerName, long postedAt, long expiresAt)
        {
            var core = new ItemCore(SampleTemplate, ItemKind.Equipment, 999_999_998) { Attr = 2, Durability = 45 };
            Repository.InsertListings([new(sellerCharacterId, sellerName, SampleTemplate, (byte)ItemKind.Equipment, 1,
                AuctionPackets.ItemEntrySnapshot(core), 12_000, 17_000, postedAt, expiresAt)]);
            return Convert.ToInt64(Query("SELECT max(listing_id) FROM auction_listings"));
        }

        public void Execute(string sql)
        {
            using var db = Open();
            using var command = db.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        object? Query(string sql)
        {
            using var db = Open();
            using var command = db.CreateCommand();
            command.CommandText = sql;
            return command.ExecuteScalar();
        }

        SqliteConnection Open()
        {
            var db = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(root, "game.db"),
                Pooling = false,
                ForeignKeys = true
            }.ToString());
            db.Open();
            return db;
        }

        public void Dispose()
        {
            owner.Dispose();
            Directory.Delete(root, true);
        }
    }
}
