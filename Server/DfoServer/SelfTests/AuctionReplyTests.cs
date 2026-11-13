using static DfoServer.Testing.TestCheck;
using DfoServer.Development.Auction;
using DfoServer.Game.Auction;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.Network.Builders;

namespace DfoServer;

// 拍卖行搜索应答的记录布局已在匹配客户端实机定案：CMD 应答 186、信封 8B 头、168B 记录、
// 道具块 @43 宽 83B（物品核心布局 + value/attr 反排）。这里锁定服务端按合同编码的字节，
// 并保留探针候选布局的编码回归。**剩余时间字段仍未确认**，不作为断言对象。
// CMD 182 均价查询应答同样在这里锁定：参考实现的 57B 布局与双应答顺序是**候选**（D95 未验证），
// 因此断言只锁「候选编码正确、默认候选就是参考实现、双应答顺序是先 mode=0 再 mode=1」。
internal static class AuctionReplyTests
{
    public static void Run()
    {
        int checks = Count;
        Header();
        Record();
        ItemBlock();
        AveragePrice();
        BidRecord();
        Probe();
        // 读路径断言放在独立文件里，由本组一起运行：公共自测入口 SelfTestRunner 由负责人维护，不在这里改。
        AuctionListingTests.Run();
        // 上架（CMD 183）的解析/校验/事务/应答断言同样挂在拍卖行组下，避免重复改动公共入口。
        AuctionRegisterTests.Run();
        // 上架提交之后的发布路径（会话安装快照 → 提交回执 → 整列刷新不抛异常）也挂在这里。
        AuctionCommitTests.Run();
        // 取消上架（CMD 184）的解析/应答/同事务提交与回滚，以及挂牌条目快照的往返一致性。
        AuctionCancelTests.Run();
        // CMD 182 的 key（参考单价）：有售取均价、无售取商店价、取不到回 1（永不为 0）。
        AuctionKeyPriceTests.Run();
        // 「我的拍卖品」三个页签（188/189/190）的解析、字节、数据来源与候选轮换。
        AuctionMyListTests.Run();
        // 一口价购买（CMD 334）的**请求抓取桩**：只抓包不实现 —— 注册存在性、任意长度容忍、
        // **零状态变更**（整库逐表 dump 前后一致）与「一个应答都不发」。185 已实现，见下一组。
        AuctionBuyProbeTests.Run();
        // 一口价购买（CMD 185）的**实现**：30 字节布局、失败码三档、金额口径、同事务成交与全部回滚路径。
        AuctionBuyTests.Run();
        // 窗口 mode 分流：mode=0 只回普通挂牌，mode=1 只回金币券。没有金币券时 mode=1 仍是空列表。
        AuctionWindowModeTests.Run();
        AuctionGoldConsignTests.Run();
        // 保证金（挂牌扣 / 取消不退 / 到期未售退还）与挂在读路径上的到期结算。
        AuctionDepositTests.Run();
        Console.WriteLine($"PASS {Count - checks} auction search reply assertions");
    }

    /// <summary>
    /// 搜索记录的**逐字段探针**（`--auction-probe bid=`）：每个候选只改一个字段，
    /// 对照候选必须逐字节等于合同编码 —— 否则「按钮变了」就分不清是探针改的那个字段还是别处漂移。
    /// </summary>
    static void BidRecord()
    {
        var core = new ItemCore(101010912, ItemKind.Equipment, 999_999_998) { Attr = 3, Durability = 45 };
        var listing = new AuctionListing(0x11223344, null, "拍卖机器人甲", 101010912, (byte)ItemKind.Equipment, 3,
            AuctionPackets.ItemEntrySnapshot(core), 12_000, 5_000, 0, 1_700_000_000, 1_700_086_400);
        long now = 1_700_000_000;

        // 一口价总额 = 单价 × 数量（客户端 `cmp [row+0x365], -1` 判「有无一口价」，并把该值当总额用）。
        var contract = AuctionPackets.EncodeListing(listing, AuctionSearchRecordProbe.Contract, now);
        That(BitConverter.ToInt32(contract, 13) == 15_000,
            "★ 记录 [13..16] = 一口价**总额**（单价 5000 × 数量 3），不是单价");
        That(Convert.ToHexString(contract) == Convert.ToHexString(AuctionPackets.EncodeListing(listing)),
            "对照候选 contract 与不带探针的合同编码逐字节相同");
        That(BitConverter.ToInt32(AuctionPackets.EncodeListing(listing with { BuyoutPrice = -1 }, null, now), 13) == -1,
            "纯竞拍（一口价 -1）原样写 -1：客户端据此跳过「比一口价高」校验并显示竞拍入口");
        That(BitConverter.ToInt32(AuctionPackets.EncodeListing(listing with { BuyoutPrice = int.MaxValue, ItemCount = 4 }, null, now), 13)
            == int.MaxValue, "总额溢出夹到 int.MaxValue");

        // 每个候选只改一个字段：与 contract 的差异字节集合必须正好落在该字段上。
        var allPure = AuctionPackets.EncodeListing(listing, AuctionSearchRecordProbe.AllPureAuction, now);
        That(BitConverter.ToInt32(allPure, 13) == -1 && Differences(contract, allPure).SequenceEqual(Enumerable.Range(13, 4)),
            "★ allpure 只改 [13..16] = -1（其余字节与对照完全一致）");
        var flag8 = AuctionPackets.EncodeListing(listing, AuctionSearchRecordProbe.Reserved8, now);
        That(flag8[8] == 1 && Differences(contract, flag8).SequenceEqual(new[] { 8 }), "flag8 只改 [8] = 1");
        var flag42 = AuctionPackets.EncodeListing(listing, AuctionSearchRecordProbe.Flag42, now);
        That(flag42[42] == 1 && Differences(contract, flag42).SequenceEqual(new[] { 42 }), "flag42 只改 [42] = 1");
        var tail34 = AuctionPackets.EncodeListing(listing, AuctionSearchRecordProbe.Tail34, now);
        That(tail34[34] == 1 && Differences(contract, tail34).SequenceEqual(new[] { 34 }), "tail34 只改 [34] = 1");
        var tail38 = AuctionPackets.EncodeListing(listing, AuctionSearchRecordProbe.Tail38, now);
        That(tail38[38] == 1 && Differences(contract, tail38).SequenceEqual(new[] { 38 }), "tail38 只改 [38] = 1");
        var market = AuctionPackets.EncodeListing(listing, AuctionSearchRecordProbe.MarketPrice, now);
        That(BitConverter.ToInt32(market, 17) == listing.StartingPrice && WithinField(contract, market, 17, 4),
            "market17 只把 [17..20] 平均市价写成该行起拍价");
        var remain = AuctionPackets.EncodeListing(listing, AuctionSearchRecordProbe.RemainSeconds, now);
        That(BitConverter.ToInt32(remain, 156) == 86_400 && WithinField(contract, remain, 156, 4),
            "remain156 只把 [156..159] 写成剩余秒数");

        That(AuctionSearchRecordProbe.All.All(candidate => candidate.Note.Length != 0), "每个搜索记录候选都写明假设与用途");
        That(AuctionSearchRecordProbe.All.Select(candidate => candidate.Field).Distinct().Count() == AuctionSearchRecordProbe.All.Count,
            "候选各改一个不同的字段（没有两个候选改同一处）");
        That(AuctionSearchRecordProbe.Find("nope") is null && AuctionSearchRecordProbe.Find("ALLPURE") == AuctionSearchRecordProbe.AllPureAuction,
            "候选按名字可达且大小写不敏感");
        // ★ 七个单字段候选已定案为「与竞拍入口无关」（本版客户端没有该入口，见 AuctionSearchRecordProbe
        // 的类型注释），因此默认轮换序**只剩对照**：继续默认轮换只会让每次搜索的记录白白偏离合同编码，
        // 而它要回答的问题已经不存在。它们仍可按名显式点出（保留理由见该类的注释），但不进任何默认轮换。
        That(AuctionSearchRecordProbe.DefaultSequence.Count == 1 &&
            AuctionSearchRecordProbe.DefaultSequence[0] == AuctionSearchRecordProbe.Contract &&
            !AuctionSearchRecordProbe.DefaultSequence.Contains(AuctionSearchRecordProbe.AllPureAuction),
            "★ 默认轮换序只剩对照 contract（七个单字段候选已定案与竞拍入口无关，不再默认轮换）");
        That(AuctionSearchRecordProbe.AllPureAuction.Field == AuctionSearchRecordField.BuyoutTotal &&
            AuctionSearchRecordProbe.Find("allpure") == AuctionSearchRecordProbe.AllPureAuction,
            "allpure 仍可按名字显式启用（只是不在默认轮换序里）");
        var unknown = AuctionSearchRecordProbe.Contract with { Field = (AuctionSearchRecordField)99 };
        Reject<InvalidDataException>(() => unknown.Apply(new byte[168], listing, now), "未知探针字段在写入前抛异常");

        // 探针只为「看界面按钮」服务：默认（未配置 bid=）必须仍然走合同编码。
        That(AuctionProbe.Parse("off").NextSearch().SearchRecord == AuctionSearchRecordProbe.Contract,
            "探针关闭时搜索记录用对照候选（不改任何字段）");

        // —— ★ 应答 mode 轮换（`bidmode=`）：同一个搜索请求分别用 mode=0 / mode=1 应答，记录内容完全相同 ——
        // 客户端按**应答的 mode 字节**分流到 GetWindow(6)/(7) 两套填充，「竞拍入口」是窗口级控件，
        // 因此这一次对照就能判别入口落在哪个窗口类型。
        var modeProbe = AuctionProbe.Parse("bidmode=0,1");
        var modeZero = modeProbe.NextSearch();
        var modeOne = modeProbe.NextSearch();
        That(modeZero.ReplyMode(9) == 0 && modeOne.ReplyMode(9) == 1,
            "★ bidmode=0,1 让同一个搜索请求交替用 mode=0 / mode=1 应答（覆盖请求里回显的字节）");
        That(modeZero.SearchRecord == AuctionSearchRecordProbe.Contract && modeOne.SearchRecord == AuctionSearchRecordProbe.Contract,
            "mode 轮换不改记录内容（没有 bid= 时两边都是对照候选 contract）");
        That(!modeZero.Enabled && !modeOne.Enabled && !modeZero.SearchOverride,
            "只配 bidmode= 不替换真实搜索结果（记录内容必须与常态搜索逐字节相同）");
        That(modeProbe.NextSearch().ReplyMode(9) == 0, "mode 候选用完一轮后循环");
        // 未配置时一律回显请求首字节：这是既有行为，不能因为新增探针而改变。
        That(AuctionProbe.Parse("off").NextSearch().ReplyMode(1) == 1 && AuctionProbe.Parse("off").NextSearch().SearchMode == AuctionProbeSettings.EchoRequestMode,
            "未配置 bidmode= 时 mode 一律回显请求首字节（既有行为不变）");
        That(AuctionProbe.Parse("bid=flag8").NextSearch().ReplyMode(1) == 1, "只配 bid= 时 mode 仍回显请求");
        // 与其它探针键一样，非法取值在解析期就拒绝（客户端只认 0/1 两个窗口分支）。
        Reject<InvalidDataException>(() => AuctionProbe.Parse("bidmode=2"), "bidmode 只接受 0/1");
        Reject<InvalidDataException>(() => AuctionProbe.Parse("bidmode="), "bidmode 不能为空");
        // mode 与搜索布局/记录字段探针各有独立下标：开 bidmode= 不会推进 presets 的下标。
        var mixed = AuctionProbe.Parse("bidmode=1;rows=1;presets=entry,core");
        var mixedFirst = mixed.NextSearch();
        That(mixedFirst.Layout.Name == "entry" && mixedFirst.ReplyMode(0) == 1,
            "bidmode= 用独立下标：搜索布局仍从第一个候选开始，应答 mode 同时生效");
    }

    /// <summary>两份等长记录里取值不同的字节下标。</summary>
    static int[] Differences(byte[] left, byte[] right) =>
        Enumerable.Range(0, left.Length).Where(index => left[index] != right[index]).ToArray();

    /// <summary>差异字节非空、且全部落在指定字段的宽度内（该字段没写满 4 字节时高字节本来就相同）。</summary>
    static bool WithinField(byte[] contract, byte[] candidate, int offset, int width)
    {
        var differences = Differences(contract, candidate);
        return differences.Length != 0 && differences.All(index => index >= offset && index < offset + width);
    }

    static void Header()
    {
        Bytes(AuctionPackets.SearchReply(0, 0, []), [0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00], "搜索应答空列表头部 8B");
    }

    static void Record()
    {
        var core = new ItemCore(101010912, ItemKind.Equipment, 999_999_998) { Attr = 3, Durability = 45, SealFlag = 1 };
        var listing = new AuctionListing(0x11223344, null, "拍卖机器人甲", 101010912, (byte)ItemKind.Equipment, 1,
            AuctionPackets.ItemEntrySnapshot(core), 12_000, -1, 0, 1_700_000_000, 1_700_086_400);
        var reply = AuctionPackets.SearchReply(1, 3, [listing]);
        That(reply.Length == 8 + AuctionPackets.RecordSize && AuctionPackets.RecordSize == 168, "一条记录时长度 = 8 + 168");
        That(reply[0] == 1 && reply[1] == 1, "flag=1 且 mode 回显请求首字节");
        That(BitConverter.ToInt32(reply, 2) == 3, "头部 dword 是匹配总数，不是本批条数");
        That(BitConverter.ToInt16(reply, 6) == 1, "本批条数是 u16");
        That(BitConverter.ToInt32(reply, 8) == 0x11223344, "挂牌 id 在记录 [0..3]");
        That(BitConverter.ToInt32(reply, 12) == 1_700_086_400, "到期时间在记录 [4..7]");
        That(reply[16] == 0, "默认大区身份为0，调用方可显式传入当前真实大区");
        var seller = AuctionPackets.EncodeListing(listing with { SellerName = "中文甲乙丙丁" }, sellerServerIndex: 7);
        That(seller[8] == 7 && CharacterName.Encoding.GetString(seller, 21, 12) == "中文甲乙丙丁" && seller[33] == 0,
            "搜索记录包含原属大区及12B CP936卖家名，13B区末字节保留NUL供原生自买判断");
        var longSeller = AuctionPackets.EncodeListing(listing with { SellerName = "ABCDEFGHIJK中文" });
        That(CharacterName.Encoding.GetString(longSeller, 21, 11) == "ABCDEFGHIJK" && longSeller[32] == 0 && longSeller[33] == 0,
            "历史异常长卖家名仅截完整编码字符，不把半个中文或尾字节写入下一字段");
        var identityReply = AuctionPackets.SearchReply(0, 1, [listing], sellerServerIndex: 7);
        That(identityReply[16] == 7 && CharacterName.Encoding.GetString(identityReply, 29, 12) == listing.SellerName,
            "批量编码传递同一真实大区和挂牌卖家名，不把名字留空");
        That(BitConverter.ToInt32(reply, 17) == 12_000, "无出价时「当前竞拍」列取起拍价");
        That(BitConverter.ToInt32(reply, 21) == -1, "纯竞拍的一口价写 -1 而不是 0");
        That(BitConverter.ToInt32(AuctionPackets.EncodeListing(listing with { CurrentBid = 15_500 }), 9) == 15_500, "有出价时「当前竞拍」列取最高出价");
        var buyout = listing with { BuyoutPrice = 5_000, ItemCount = 3 };
        var buyoutRecord = AuctionPackets.EncodeListing(buyout);
        That(BitConverter.ToInt32(buyoutRecord, 9) == -1 && BitConverter.ToInt32(buyoutRecord, 13) == 15_000 &&
            buyout.StartingPrice == listing.StartingPrice && buyout.DisplayPrice == listing.DisplayPrice,
            "无出价的一口价挂牌隐藏当前竞拍行，售价总额及库内起拍/均价来源不变");
        That(BitConverter.ToInt32(AuctionPackets.EncodeListing(buyout with { CurrentBid = 15_500 }), 9) == 15_500,
            "已有真实出价的兼容挂牌保留当前竞价，不把历史出价隐藏");
        That(reply.Skip(8 + 126).All(value => value == 0), "记录 [126..167] 保持 0（剩余时间字段未确认，不写）");
        var batch = AuctionPackets.SearchReply(0, 25, [listing, listing]);
        That(BitConverter.ToInt32(batch, 2) == 25 && BitConverter.ToInt16(batch, 6) == 2 && batch.Length == 8 + 2 * 168,
            "匹配总数与本批条数分开编码，客户端据此算总页数");
    }

    static void ItemBlock()
    {
        var core = new ItemCore(101010912, ItemKind.Equipment, 999_999_998) { Attr = 3, Durability = 45, SealFlag = 1 };
        var entry = AuctionPackets.ItemEntrySnapshot(core);
        That(entry.Length == 123 && BitConverter.ToInt32(entry, 2) == 101010912 && entry[10] == 3 &&
            BitConverter.ToInt32(entry, 6) == 999_999_998 && BitConverter.ToInt16(entry, 11) == 45 && entry[13] == 1,
            "条目快照沿用普通条目布局：itemId@2、value@6、attr@10、耐久@11、封印@13");
        var block = AuctionPackets.ItemBlock(entry, (int)core.Kind);
        That(block.Length == 83, "道具块固定 83B，不是条目的前 83 字节");
        That(block[0] == (byte)ItemKind.Equipment, "块首是物品种类，不是条目开头的 slot");
        That(BitConverter.ToInt32(block, 1) == 101010912, "itemId 在块内 [1..4]");
        That(block[5] == 3, "attr 在块内 [5]");
        That(BitConverter.ToInt32(block, 6) == 999_999_998, "value 在块内 [6..9]（与条目里的位置相反）");
        That(BitConverter.ToInt16(block, 10) == 45, "耐久在块内 [10..11]");
        That(block[12] == 1, "封印在块内 [12]");
        That(block.Skip(13).All(value => value == 0), "块内未确认字段保持 0");
        Bytes(AuctionPackets.ItemBlock(core), block, "从物品核心直接构造的块与从条目快照构造的一致");
        Reject<InvalidDataException>(() => AuctionPackets.ItemBlock(new byte[10], 1), "过短的条目快照拒绝");
        Reject<InvalidDataException>(() => AuctionPackets.ItemBlock(new byte[18], 3), "缺少升级字节的条目快照拒绝");
        foreach (int itemId in new[] { 10306743, 10306744 })
        {
            // 卡片和宝珠共用实例升级字段，但其位置由83B消费者独立确认，不从123B布局类推。
            var upgraded = new ItemCore(itemId, ItemKind.Material, 2) { Attr = 7, EnchantUpgrade = 2 };
            var snapshot = AuctionPackets.ItemEntrySnapshot(upgraded);
            var upgradedBlock = AuctionPackets.ItemBlock(snapshot, (int)upgraded.Kind);
            That(upgradedBlock[61] == 2 && upgradedBlock[5] == 7 &&
                upgradedBlock.Where((_, index) => index >= 13 && index != 61).All(value => value == 0),
                "83B块+61保留实例升级，强化仍在+5，其余未知字段不类推");
            Bytes(upgradedBlock, AuctionPackets.ItemBlock(upgraded), "卡片/宝珠的快照与直接核心投影一致");
            var listing = new AuctionListing(17, 9, "卖家", itemId, (byte)ItemKind.Material, 2,
                snapshot, 100, 100, 0, 1_700_000_000, 1_700_086_400);
            That(AuctionPackets.EncodeListing(listing)[43 + 61] == 2 &&
                AuctionPackets.EncodeMyListing(listing, AuctionMyListReply.Registed151, 1_700_000_000)[30 + 61] == 2,
                "搜索与我的上架两条展示路径均保留升级卡片/宝珠等级");
            That(AuctionPackets.ItemCoreFromEntry(snapshot, ItemKind.Material) == upgraded,
                "挂牌持久快照保留完整核心，展示修复不改变还原内容");
        }
        BlockValueFloor();
    }

    /// <summary>
    /// ★ 道具块 <c>+6</c>（= 记录 <c>[49..52]</c>）在 itemId 合法时**恒 &gt; 0**。
    ///
    /// **这是防丢行、不是业务规则**：客户端 186 回调在 itemId 能查到道具资源、而该字段 ≤ 0 时，
    /// 会把整行**静默丢弃**（不拷进行缓冲、本批条数不减），界面上的现象是「总数正常、列表少行」，
    /// 且客户端不报任何错误码。因此三条编码路径都要断言这一条不变式：搜索记录
    /// （<see cref="AuctionPackets.EncodeListing"/>）、「我的拍卖品」记录
    /// （<see cref="AuctionPackets.EncodeMyListing(AuctionListing, AuctionMyListReply, long)"/>）、
    /// 探针行（<see cref="AuctionReplyRow.Encode"/>，合同布局把 value 反排到 +6）。
    /// ★ **该过滤器只有 186（搜索）有**（D95 转储反汇编定案）：186 回调里既有道具资源查询
    /// （`call 0x20BE8C0`）又有 `rep movsd 0x2A`，188（`0x02352810`）/ 189（`0x02352A00`）**两者皆无**
    /// ⇒ 「我的拍卖品」路径上的这条断言是**防御性保留**（同一份记录契约里同一个字段不该有两种含义，
    /// 也防将来某版客户端补上过滤器时静默丢行），**不是**「188/189 会丢行」；
    /// 搜索路径（含走 186 应答的探针行）上它是**必需**的。
    /// </summary>
    static void BlockValueFloor()
    {
        // ① 结构性前提：堆叠类物品的 value **就是数量**，而数量 ≤ 0 的核心在编码入口就被 Validate 拒绝，
        //    所以「堆叠路径上出现需要兜底的 0」这件事本身不成立 —— 这条兜底不可能改到任何堆叠数量。
        Reject<InvalidDataException>(() => AuctionPackets.ItemEntrySnapshot(new ItemCore(10014751, ItemKind.Material, 0)),
            "堆叠数量 0 的物品核心在编码入口即被拒绝（堆叠路径上不存在需要兜底的 0）");
        Reject<InvalidDataException>(() => AuctionPackets.ItemEntrySnapshot(new ItemCore(10014751, ItemKind.Material, -3)),
            "负数量的物品核心同样被拒绝");

        // ② 四类 itemId 合法的输入。装备的价值字段是品质 seed、材料是堆叠数量，
        //    时装/宠物则**本来就发 0**（InventoryRewardGrantService.Create 对 Avatar/Creature 写 0），
        //    最后这一类是本仓库现有数据里确实存在的 value = 0（兜底前那几行会被客户端丢掉）。
        var cases = new (string Name, int ItemId, ItemKind Kind, int Value)[]
        {
            ("装备（品质 seed 为 0）", 101010912, ItemKind.Equipment, 0),
            ("装备（正常品质 seed）", 101010912, ItemKind.Equipment, InventoryRewardGrantService.InitialQuality),
            ("材料（堆叠 5）", 10014751, ItemKind.Material, 5),
            ("时装（Create 发的就是 0）", 101010912, ItemKind.Avatar, 0)
        };
        foreach (var item in cases)
        {
            var core = new ItemCore(item.ItemId, item.Kind, item.Value);
            var entry = AuctionPackets.ItemEntrySnapshot(core);
            // 块 +6 的期望值：value ≤ 0 时兜到 1，否则原样。
            int expected = item.Value > 0 ? item.Value : 1;
            var listing = new AuctionListing(1, null, "拍卖机器人甲", item.ItemId, (byte)item.Kind, 1, entry,
                12_000, -1, 0, 1_700_000_000, 1_700_086_400);

            // ⓵ 搜索记录路径：记录 [44..47] 是丢行过滤器读的 itemId，[49..52] 是块 +6。
            var record = AuctionPackets.EncodeListing(listing, null, 1_700_000_000);
            That(BitConverter.ToInt32(record, 44) == item.ItemId && BitConverter.ToInt32(record, 49) == expected &&
                BitConverter.ToInt32(record, 49) > 0,
                $"{item.Name}：搜索记录 [49..52] = {expected} > 0（写 0 会被客户端整行丢弃）");

            // ⓶ 「我的拍卖品」路径：道具块按候选偏移落在记录里，块 +6 同样必须 > 0。
            var layout = AuctionMyListReply.DefaultFor(AuctionMyListPanel.Registed);
            var myList = AuctionPackets.EncodeMyListing(listing, layout, 1_700_000_000);
            That(AuctionPackets.ReadInt32(myList.AsSpan(layout.ItemOffset), 1) == item.ItemId &&
                AuctionPackets.ReadInt32(myList.AsSpan(layout.ItemOffset), 6) == expected,
                $"{item.Name}：「我的拍卖品」记录的道具块 +6 = {expected} > 0（188/189 无该过滤器，这里是防御性保留）");

            // ⓷ 探针行路径（合同布局 = 物品核心布局 + 反排 ⇒ value 落在块 +6）。
            var block = AuctionPackets.ItemBlock(entry, (int)item.Kind, AuctionProbeSettings.Contract);
            var row = new AuctionReplyRow(1, unchecked((int)1_700_086_400), 12_000, -1, block, 86_400);
            var probeRecord = row.Encode(AuctionProbeSettings.Contract);
            That(BitConverter.ToInt32(probeRecord, 49) == expected,
                $"{item.Name}：探针行（合同布局）记录 [49..52] = {expected} > 0（探针行也不会被丢掉）");

            // ⓸ 合法数据逐字节不变：兜底只在 value ≤ 0 时改写，堆叠数量与品质 seed 都不受影响。
            if (expected == item.Value)
                That(AuctionPackets.ReadInt32(AuctionPackets.ItemBlock(entry, (int)item.Kind), 6) == item.Value,
                    $"{item.Name}：合法 value = {item.Value} 原样写入（兜底不改既有语义）");
        }

        // ③ 手工构造的核心（不过 Validate）同样兜住：负数与 0 都只会来自绕过物品模块的调用方。
        foreach (int broken in new[] { 0, -7 })
        {
            That(AuctionPackets.ReadInt32(AuctionPackets.ItemBlock(new ItemCore(10014751, ItemKind.Material, broken)), 6) == 1,
                $"手工核心 value = {broken}：编码器把块 +6 兜到 1");
        }
        // ④ itemId 无效（0）时**不**兜底：客户端查不到道具资源，那一行不会被过滤器丢掉，此处不替业务数据补救。
        That(AuctionPackets.ReadInt32(AuctionPackets.ItemBlock(new ItemCore(0, ItemKind.Material, 0)), 6) == 0,
            "itemId 无效（0）时不做兜底：丢行过滤器只对「查得到道具资源」的行生效");
    }

    /// <summary>
    /// CMD 182 均价查询应答的编码、**结构候选**与**安全底线**。
    /// 参考实现的 57B 结构、双应答顺序、以及「CMD 应答体自带 1 字节 flag」都**未经本客户端验证**
    /// （D95 对 33B/37B/57B 都回 217 OVERFLOW_INFO，1B 直接读崩），因此这里锁的是：
    /// 每个候选按自己声明的结构形状编码、长度与声明一致、**任何 &lt; 8B 的数据体都被拒绝**、
    /// 带 flag 的候选确实把 flag 写在体首且其后字段整体后移 1 字节。
    /// 「哪一套能让输入框解锁」只能由实机判定，自测不做结论。
    /// </summary>
    static void AveragePrice()
    {
        // ★ 默认候选 = flag58：CMD 应答体必须自带 flag（186 的实测体以 0x01 开头，而帧头第 0 字节是 kind）。
        var candidate = AuctionAveragePriceReply.Default;
        That(candidate.Name == "flag58" && candidate.Shape == AuctionAveragePriceReply.ShapeFlagReference57 &&
            candidate.ReplyOpcode == 182 && candidate.FirstMode == 0 && candidate.SecondMode == 1,
            "默认候选 = flag58：CMD 182、双应答 mode=0 → mode=1、应答体自带 flag + 57B 参考结构");
        // flag58 的字节序：首字节必须是 0x01（flag），紧接着才是 mode，整体 58B。
        var flagBody = AuctionPackets.AveragePriceReply(0, 17_000, candidate);
        That(flagBody.Length == 58, "flag58 总长 58B（=1 flag + 57B 结构）");
        That(flagBody[0] == 0x01, "flag58 首字节是 flag = 0x01（非 0 才走客户端成功分支）");
        That(flagBody[1] == 0, "flag58 [1] 是 mode（flag 之后），不是 flag 取值");
        That(BitConverter.ToInt32(flagBody, 2) == 17_000, "flag58 key 在 [2..5]（相对 57B 结构整体后移 1 字节）");
        That(BitConverter.ToInt32(flagBody, 6) == 0, "flag58 [6..9] unused 占位保持 0");
        That(Enumerable.Range(0, 6).All(index => BitConverter.ToInt32(flagBody, 10 + index * 4) == 17_000) &&
            Enumerable.Range(0, 6).All(index => BitConverter.ToInt32(flagBody, 34 + index * 4) == 17_000) &&
            flagBody.Length - 34 == 6 * 4,
            "flag58 vector1（[10..33]）与 vector2（[34..57]）各 6 个 i32，与 key 同源，且正好填满 58B");
        var flagSecond = AuctionPackets.AveragePriceReply(1, 17_000, candidate);
        That(flagSecond[0] == 0x01 && flagSecond[1] == 1, "第二应答同样带 flag，mode=1 落在 flag 之后");
        // 整包逐字节锁定：这样「服务端日志里的 Body 十六进制」与仓库里的期望值可以直接对照，不用现场换算。
        That(Convert.ToHexString(flagBody) == "010068420000" + new string('0', 8) + string.Concat(Enumerable.Repeat("68420000", 12)),
            "flag58 完整字节 = 01 | 00 | 68420000 | 00000000 | 68420000 ×12（flag+mode+key+unused+两向量）");
        var flagUnsafe = AuctionPackets.AveragePriceReply(0, 0, candidate);
        That(flagUnsafe[0] == 0x01 && BitConverter.ToInt32(flagUnsafe, 2) == 0,
            "无在售时 key=0 照常下发，但 flag 仍必须非 0（否则整包走错误分支）");

        // 对照候选 flag33 / flag25：同样是「flag + u16 计数 + N×4B 条目」，计数与实际条目数一致。
        // 带 flag 时头部整体后移 1 字节：flag@0、mode@1、key@2..5、unused@6..9、计数@10..11、条目@12..
        var flag33 = AuctionAveragePriceReply.Find("flag33")!;
        var flag33Body = AuctionPackets.AveragePriceReply(0, 17_000, flag33);
        That(flag33Body.Length == 33 && flag33Body[0] == 0x01 && BitConverter.ToUInt16(flag33Body, 10) == flag33.CountEntries &&
            flag33Body.Length - 12 - flag33.TailBytes == flag33.CountEntries * 4,
            "flag33 = flag + 计数头，u16 计数与实际条目数、总长 33B 三者一致");
        var flag25 = AuctionAveragePriceReply.Find("flag25")!;
        var flag25Body = AuctionPackets.AveragePriceReply(0, 17_000, flag25);
        That(flag25Body.Length == 25 && BitConverter.ToUInt16(flag25Body, 10) == flag25.CountEntries &&
            flag25Body.Length - 12 - flag25.TailBytes == flag25.CountEntries * 4,
            "flag25 = 同结构短体（25B），用于 flag 组的长度对照");
        That(AuctionAveragePriceReply.All.Take(3).Select(item => item.Name).SequenceEqual(new[] { "flag58", "flag33", "flag25" }),
            "轮换顺序前三位是带 flag 的候选（flag58 第一优先）");

        // —— 参考结构（无 flag）对照：body 现在从 mode 起算，与 flag58 只差这一个字节 ——
        var a21 = AuctionAveragePriceReply.Find("a21-dual")!;
        var body = AuctionPackets.AveragePriceReply(0, 12_345, a21);
        That(body.Length == AuctionPackets.AverageFullLength && AuctionPackets.AverageFullLength == 57,
            "无 flag 的参考结构仍是 57B");
        That(body[0] == 0, "无 flag 数据体 [0] 是 mode，[1..4] 是 key");
        That(BitConverter.ToInt32(body, 1) == 12_345, "key（预填价）在 [1..4]");
        That(BitConverter.ToInt32(body, 5) == 0, "[5..8] unused 必须占位且保持 0");
        That(Enumerable.Range(0, 6).All(index => BitConverter.ToInt32(body, 9 + index * 4) == 12_345) &&
            Enumerable.Range(0, 6).All(index => BitConverter.ToInt32(body, 33 + index * 4) == 12_345),
            "vector1（[9..32]）与 vector2（[33..56]）各 6 个 i32，与 key 同源");
        That(BitConverter.ToInt32(AuctionPackets.AveragePriceReply(1, 7, a21), 1) == 7,
            "无 flag 版本的第二条应答用 mode=1（开窗），价格字段同样非空洞");
        // 无在售时均价为 0：key 必须是真实的 0，不能编成一个「至少 1」来假装有行情。
        That(BitConverter.ToInt32(AuctionPackets.AveragePriceReply(0, 0, a21), 1) == 0, "无在售时 key=0（如实下发「无均价数据」）");
        // flag 假设与「尾部补字节」假设是两个不同的自变量：两者长度都是 58B，但字节分布必须不同。
        var tail58 = AuctionAveragePriceReply.Find("ref58")!;
        var tailBody = AuctionPackets.AveragePriceReply(0, 12_345, tail58);
        That(tailBody.Length == 58 && tailBody[0] == 0 && BitConverter.ToInt32(tailBody, 1) == 12_345 && tailBody[^1] == 0,
            "ref58 = 多余字节在**尾部**（[0] 仍是 mode），与 flag58 的「字节在头部」是不同假设");

        // —— 安全底线：任何 < 8B 的数据体都不允许编码，也不允许进候选表 ——
        That(AuctionAveragePriceReply.MinimumBodyBytes == 8, "安全底线 = 8 字节（1B ACK 实测把客户端读崩，33B 以上只回 217）");
        var unsafeCandidate = new AuctionAveragePriceReply("unsafe-ack", 182, 0, AuctionAveragePriceReply.NoSecondMode,
            AuctionAveragePriceReply.ShapeAck, false, "自测用：1B ACK，必须被安全底线拒绝");
        That(unsafeCandidate.BodyLength == 1 && !unsafeCandidate.IsSafe, "1B ACK 候选被自身安全判定标为不安全");
        Reject<InvalidDataException>(() => AuctionPackets.AveragePriceReply(0, 5, unsafeCandidate), "编码 1B ACK 被安全底线拒绝");
        Reject<InvalidDataException>(() => AuctionAveragePriceReply.Validate(unsafeCandidate), "安全校验直接拒绝 <8B 候选");
        That(AuctionAveragePriceReply.All.All(item => item.IsSafe && item.BodyLength >= 8),
            "候选表里不存在任何 <8B 的数据体");
        That(AuctionAveragePriceReply.All.All(item => AuctionPackets.AveragePriceReply(0, 5, item).Length == item.BodyLength),
            "每个候选的编码长度都与它声明的结构长度一致（声明与实际脱节正是 217 读超的来源）");
        That(AuctionAveragePriceReply.Find("a21-ack") is null && AuctionAveragePriceReply.Find("a21-legacy38") is null,
            "旧的不安全候选（1B ACK）与错误命名的旧候选不再按名字可达");

        // —— 结构候选：每个都按自己声明的结构编码，长度必须与声明一致 ——
        var legacy = AuctionAveragePriceReply.Find("a21-legacy37")!;
        var legacyBody = AuctionPackets.AveragePriceReply(0, 5, legacy);
        That(legacyBody.Length == 37 && AuctionPackets.AverageLegacyLength == 37,
            "a21-legacy37 编码 37B（参考实现格式 10：3 组 [price,0] + 尾部 i32(1)）");
        That(BitConverter.ToInt32(legacyBody, 9) == 5 && BitConverter.ToInt32(legacyBody, 13) == 0 && BitConverter.ToInt32(legacyBody, 33) == 1,
            "旧档结构：[price,0]×3 后跟 i32(1)");

        var ref65 = AuctionAveragePriceReply.Find("ref65")!;
        That(ref65.VectorLength == 7 && AuctionPackets.AveragePriceReply(0, 5, ref65).Length == 65,
            "ref65 = 参考结构但 vector 各 7 个 i32（65B）");

        var recount = AuctionAveragePriceReply.Find("recount15")!;
        var recountBody = AuctionPackets.AveragePriceReply(0, 17_000, recount);
        That(recountBody.Length == 15 && recount.CountEntries == 1 && recountBody[0] == 0 && BitConverter.ToInt32(recountBody, 1) == 17_000,
            "recount15 = mode+key+unused 头部（11B）+ u16 计数 + 1 条 4B 价格条目（15B）");
        // ★ 自洽性：声明的计数必须等于实际提供的条目数（声明 N 条只给 M<N 条正是 OVERFLOW 的最可能来源）。
        ushort declared = BitConverter.ToUInt16(recountBody, 9);
        That(declared == recount.CountEntries && declared * 4 == recountBody.Length - 11 - recount.TailBytes,
            "recount15 的 u16 计数与实际条目数、以及数据体长度三者一致");
        var recount12 = AuctionAveragePriceReply.Find("recount12")!;
        var recount12Body = AuctionPackets.AveragePriceReply(0, 0, recount12);
        That(BitConverter.ToUInt16(recount12Body, 9) == 0 && recount12Body.Length == 11 + recount12.TailBytes &&
            recount12Body.Skip(11).All(value => value == 0),
            "recount12 = 计数 0 + 尾部补零凑到自己的声明长度");

        foreach (var zero in new[] { "zero8", "zero13", "zero33", "zero58", "zero64", "zero128" })
        {
            var fill = AuctionAveragePriceReply.Find(zero)!;
            var fillBody = AuctionPackets.AveragePriceReply(0, 17_000, fill);
            That(fillBody.Length == fill.BodyLength && fillBody.All(value => value == 0),
                $"候选 {zero} 是 {fill.BodyLength}B 的全 0 数据体（长度是唯一变量，key 不参与）");
        }

        var withList = AuctionAveragePriceReply.Find("a21-dual-list")!;
        That(withList.SendListRefresh && withList.ReplyOpcode == 182, "候选 a21-dual-list 额外补发均价列表刷新");
        That(AuctionAveragePriceReply.All.All(item => item.Note.Length != 0),
            "每个候选都写明自己的结构假设与用途");
        var refresh = AuctionPackets.AveragePriceListRefresh();
        That(refresh.Length == 7 && BitConverter.ToInt32(refresh, 0) == 0 && refresh.Skip(4).All(value => value == 0),
            "均价列表刷新数据体 = 长度前缀(0) + 空串 + 三个 0 字节；字符串长度必须 < 0x20，否则客户端静默得空串");
    }

    static void Probe()
    {        var layout = AuctionProbeSettings.Contract;
        That(layout.RecordSize == 168 && layout.ItemOffset == 43 && layout.ItemSize == 83 && layout.RemainOffset == -1,
            "探针默认候选就是已定案的合同值，且不写未确认的剩余时间");
        var row = new AuctionReplyRow(0x11223344, unchecked((int)0x55667788), 0x0A, -1, new byte[83], 3600);
        That(AuctionPackets.SearchReply(1, 3, [row], layout).Length == 8 + 168, "探针候选仍按布局长度编码");
        var probe = AuctionProbe.Parse("rows=1;items=501,502;presets=entry,core;reply=186,187");
        That(probe.NextSearch().Layout.Name == "entry" && probe.NextSearch().Layout.Name == "core", "显式 presets 仍按顺序轮换搜索候选");
        That(probe.NextSearch().Layout.Name == "entry", "搜索候选用完一轮后循环");
        var variant = AuctionProbe.Parse("rows=1;items=501,502;presets=entry,core;reply=186,187");
        variant.NextSearch();
        var second = variant.NextSearch();
        That(second.ItemTemplateId == 502 && second.ReplyOpcode == 187, "物品模板与应答 opcode 与搜索布局同下标轮换");
        That(AuctionProbe.Parse("rows=1").NextSearch().Layout.Name == "contract", "未指定 presets 时探针使用合同布局");
        That(AuctionProbe.Parse("rows=0;total=9999,8888").NextSearch().Total == 9999, "total 覆盖头部匹配总数");
        That(AuctionProbe.Parse("rows=1;presets=contract").NextSearch().Total == 1, "未覆盖时匹配总数即本批条数");
        That(AuctionProbe.Parse("off").NextSearch().Enabled == false && !AuctionProbe.Parse(null).NextSearch().Enabled, "off 或缺省即关闭探针");
        // ★ 均价候选有独立的轮换下标：拖物品换均价候选不会推进搜索候选（用户不会因此看到空列表）。
        var averageProbe = AuctionProbe.Parse("rows=0;avg=a21-first,a21-second");
        That(averageProbe.NextAverage().Average.Name == "a21-first" && averageProbe.NextAverage().Average.Name == "a21-second",
            "avg= 候选按顺序轮换（实机一轮即可分辨哪种结构能让价格输入框解锁）");
        That(averageProbe.NextAverage().Average.Name == "a21-first", "均价候选用完一轮后循环");
        var independent = AuctionProbe.Parse("rows=1;presets=entry,core;avg=a21-first,a21-second");
        independent.NextAverage();
        independent.NextAverage();
        var afterAverage = independent.NextSearch();
        That(afterAverage.Layout.Name == "entry" && afterAverage.AverageIndex == 0,
            "推进均价候选不改动搜索候选下标：搜索仍从第一个搜索候选开始");
        var searchFirst = AuctionProbe.Parse("presets=entry,core;avg=a21-first,a21-second");
        searchFirst.NextSearch();
        That(searchFirst.NextSearch().Layout.Name == "core" && searchFirst.NextAverage().Average.Name == "a21-first",
            "推进搜索候选不改动均价候选下标：均价仍从第一个均价候选开始");
        var avgOnlyProbe = AuctionProbe.Parse("avg=zero8;avgonly=1");
        That(!avgOnlyProbe.NextSearch().Enabled && avgOnlyProbe.NextAverage().Average.Name == "zero8",
            "avgonly=1 时搜索路径整条走真实挂牌数据，均价候选仍从自己列表的第一项开始");
        var avgOnlyCandidate = AuctionProbe.Parse("avg=zero8;avgonly=1");
        avgOnlyCandidate.NextAverage();
        That(avgOnlyCandidate.NextAverage().Average.Name == "zero8",
            "avgonly=1 时均价候选仍按自己的下标轮换");
        That(AuctionProbe.Parse("rows=0").NextAverage().Average.Name == AuctionAveragePriceReply.Default.Name,
            "未指定 avg 时均价应答用参考实现的默认候选");
        Reject<InvalidDataException>(() => AuctionProbe.Parse("avg=nope"), "未知均价候选被拒绝");
        Reject<InvalidDataException>(() => AuctionProbe.Parse("avg=a21-ack"),
            "已禁用的不安全均价候选（1B ACK）被拒绝，不会被塞进轮换");
        Reject<InvalidDataException>(() => AuctionProbe.Parse("presets=nope"), "未知布局候选被拒绝");
        Reject<InvalidDataException>(() => AuctionProbe.Parse("reply=185"), "未知 reply 取值被拒绝");
        Reject<InvalidDataException>(() => AuctionProbe.Parse("avgonly=2"), "avgonly 只接受 0/1");
        Reject<InvalidDataException>(() => AuctionProbe.Parse("boom"), "缺少 key=value 的片段被拒绝");
        // 候选路径仍可反排：条目内 value@6、attr@10，候选布局把它们旋转成 attr@6、value@7..10。
        var entry = new byte[123];
        entry[2] = 0x39; entry[3] = 0x0C;
        entry[6] = 0x11; entry[7] = 0x22; entry[8] = 0x33; entry[9] = 0x44; entry[10] = 0x05;
        var swapped = AuctionPackets.ItemBlock(entry, 3, AuctionReplyLayout.EntrySwap);
        That(swapped.Length == 123 && swapped[6] == 0x05 && swapped[7] == 0x11 && swapped[10] == 0x44, "候选布局把 [value4][attr1] 反排成 [attr1][value4]");
        var plain = AuctionPackets.ItemBlock(entry, 3, AuctionReplyLayout.Entry);
        That(plain[6] == 0x11 && plain[10] == 0x05, "不反排的候选保持条目原顺序");
    }
}
