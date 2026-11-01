using static DfoServer.Testing.TestCheck;
using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Auction;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Mailbox;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Builders;
using DfoServer.Network.Handlers;
using DfoServer.Network.Projection;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;

namespace DfoServer;

/// <summary>
/// CMD 185 `AUCTION_BIDDING`（一口价购买，**跨账号钱货转移**）：请求布局、失败码、只读规划、
/// **同事务**提交与全部回滚路径。数据库是真实文件库 ⇒ revision、审计、外键、事务回滚都是真的。
///
/// 六组：
///   1. <see cref="Parsing"/>：两个实机样本逐字段、不足 30B 逐个长度拒绝、尾部字节容错；
///   2. <see cref="Wire"/>：`flag + mode` 成功、`flag + errcode` 失败、白名单外的码拒发，
///      以及「每个领域错误都能映射到白名单三档之一」；
///   3. <see cref="Planning"/>：金额口径（单价 / 总额 / 数量=1 区分不了）、买自己的挂牌、
///      金币不足、不在售/已过期/纯竞拍、模板不符、机器人卖家；
///   4. <see cref="Commit"/>：成交后**买方金币减、挂牌 state=1 且盖结算时间、两封托管邮件、
///      审计有行**，卖方钱包**不动**（托管 ≠ 入账）；机器人卖家与卖方角色行缺失都照常成交；
///   5. <see cref="Rollback"/>：重复点击/被他人买走（条件更新 0 行）⇒ 整事务回滚零副作用、
///      内容陈旧 ⇒ 拒绝、托管插入失败 ⇒ 连买方扣款一起回滚；
///   6. <see cref="Session"/>：真实命令表派发 185 ⇒ 应答字节、在线快照安装、**不发任何背包/金币刷新**，
///      以及陈旧计划（换角）⇒ 静默拒绝不提交。
/// </summary>
internal static class AuctionBuyTests
{
    /// <summary>实机样本 A：点挂牌 5（模板 101010912、一口价 24000、到期回显 1791743620）。</summary>
    const string SampleA = "00C05D00000500000084D6CB6A00000000000000000000000000E04D0506";

    /// <summary>实机样本 B：点挂牌 8（模板 106000064、一口价 30000、同一行的到期回显）。</summary>
    const string SampleB = "00307500000800000084D6CB6A00000000000000000000000000C06E5106";

    const int SampleTemplate = 10400;      // 整件装备（StackLimit 1）
    const int StackTemplate = 10014751;    // 可堆叠材料（StackLimit 999）
    static readonly Location Room = new(38, 1, 450, 234, 0);

    public static void Run()
    {
        int checks = Count;
        Parsing();
        Wire();
        Planning();
        Commit();
        Rollback();
        Session().GetAwaiter().GetResult();
        Console.WriteLine($"PASS {Count - checks} auction buy assertions");
    }

    /// <summary>请求解析：30 字节 = 前导 0 + 一口价(4) + 挂牌 id(4) + 到期回传(4) + 保留(13) + 模板 id(4)。</summary>
    static void Parsing()
    {
        byte[] sampleA = Convert.FromHexString(SampleA);
        byte[] sampleB = Convert.FromHexString(SampleB);
        That(sampleA.Length == AuctionBuyRequest.BodySize && AuctionBuyRequest.BodySize == 30,
            "实机样本 A 正好 30 字节（= AuctionBuyRequest.BodySize）");
        var a = AuctionBuyRequest.Parse(sampleA);
        That(a.Leading == 0, "样本 A [0] = 前导 0（与 183/184 同款）");
        That(a.Amount == 24_000, "★ 样本 A [1..4] = 一口价 24000（与库内该行的 buyout_price 一致）");
        That(a.ListingId == 5, "★ 样本 A [5..8] = 挂牌 id 5（定位用）");
        That(a.ExpiresAtUnix == 1_791_743_620, "★ 样本 A [9..12] = 到期时间回显 1791743620（库内 expires_at_unix 的回显）");
        That(a.ItemTemplateId == 101_010_912, "★ 样本 A [26..29] = 物品模板 id 101010912（库内该行的 item_template_id）");
        That(a.ReservedHex(sampleA) == new string('0', 26), "样本 A [13..25] 13 字节保留区全 0（不是参考布局 26B 里的道具标识串）");

        var b = AuctionBuyRequest.Parse(sampleB);
        That(b.Amount == 30_000 && b.ListingId == 8 && b.ItemTemplateId == 106_000_064,
            "★ 样本 B 逐字段成立：30000 / 挂牌 8 / 模板 106000064（两个样本与库内数据一一对应）");
        That(b.ExpiresAtUnix == a.ExpiresAtUnix, "两个样本的 [9..12] 相同：它是所在行的到期时间回显，不是金额或数量");

        // 长度容错：不足 30 字节逐个长度拒绝（拿不到 [26..29] 就无法与挂牌交叉校验），尾部多余字节忽略。
        for (int length = 0; length < AuctionBuyRequest.BodySize; length++)
        {
            int captured = length;
            Reject<InvalidDataException>(() => AuctionBuyRequest.Parse(sampleA.AsSpan(0, captured).ToArray()),
                $"不足 30 字节（{captured}B，读不到 [26..29]）时拒绝");
        }
        var padded = AuctionBuyRequest.Parse([.. sampleA, 0xAA, 0xBB]);
        That(padded.ListingId == 5 && padded.ItemTemplateId == 101_010_912,
            "尾部多出的字节不参与解析（长度按客户端版本容错）");
        That(AuctionBuyRequest.Parse(new byte[30]).ReservedHex(new byte[5]).Length == 0,
            "包体不足保留区宽度时不给保留区读数（取值不越界）");
        That(AuctionBuyRequest.Parse(new byte[30]).ListingId == 0, "全 0 请求解析出挂牌 id 0（由规划层按请求非法拒绝）");
    }

    /// <summary>应答字节与错误码白名单（三档，不允许出现第四档）。</summary>
    static void Wire()
    {
        Bytes(AuctionPackets.BuySuccess(), [0x01, 0x00],
            "★ 购买成功应答 = flag(01) + mode(00)：体首必须是 flag，第二字节刷新浏览窗口（与 183/184 同形）");
        Bytes(AuctionPackets.BuyFailure(0xd2), [0x00, 0xd2, 0], "购买失败使用完整u16错误码");
        Bytes(AuctionPackets.BuyFailure(0x69), [0x00, 0x69, 0], "保留原买自己映射；客户端实际文案是登录保护计时，不能从旧端注释推断文本");
        Bytes(AuctionPackets.BuyFailure(0x7e), [0x00, 0x7e, 0], "保留过期操作静默码，但补齐公共u16宽度");
        Bytes(AuctionPackets.BuyFailure(0x72), [0x00, 0x72, 0, 0], "购买114继续读取模式0，不能截断");
        foreach (byte code in new byte[] { 0x00, 0x01, 0x7f, 0xd1, 0xd3, 0xff })
            Reject<InvalidDataException>(() => AuctionPackets.BuyFailure(code), $"旧领域购买入口不扩大白名单0x{code:X2}");

        foreach (var error in Enum.GetValues<AuctionBuyError>().Where(value => value != AuctionBuyError.None))
            That(AuctionRegisterErrors.Whitelist.Contains(AuctionBuyErrors.ToClientCode(error)),
                $"购买的领域错误 {error} 映射到客户端白名单内的错误码");
        Reject<InvalidDataException>(() => AuctionBuyErrors.ToClientCode(AuctionBuyError.None), "成功没有错误码");
        That(AuctionBuyErrors.ToClientCode(AuctionBuyError.NotFound) == AuctionBuyErrors.Silent &&
            AuctionBuyErrors.ToClientCode(AuctionBuyError.NotActive) == AuctionBuyErrors.Silent &&
            AuctionBuyErrors.ToClientCode(AuctionBuyError.Expired) == AuctionBuyErrors.Silent &&
            AuctionBuyErrors.ToClientCode(AuctionBuyError.AuctionOnly) == AuctionBuyErrors.Silent &&
            AuctionBuyErrors.ToClientCode(AuctionBuyError.AmountMismatch) == AuctionBuyErrors.Silent &&
            AuctionBuyErrors.ToClientCode(AuctionBuyError.RaceLost) == AuctionBuyErrors.Silent,
            "★「操作前提已经没了」→ 静默码 0x7e（不存在 / 非在售 / 已过期 / 纯竞拍 / 金额不符 / 条件更新 0 行）");
        That(AuctionBuyErrors.ToClientCode(AuctionBuyError.InsufficientGold) == AuctionBuyErrors.Generic &&
            AuctionBuyErrors.ToClientCode(AuctionBuyError.Unavailable) == AuctionBuyErrors.Generic &&
            AuctionBuyErrors.ToClientCode(AuctionBuyError.StalePlan) == AuctionBuyErrors.Generic &&
            AuctionBuyErrors.ToClientCode(AuctionBuyError.InvalidRequest) == AuctionBuyErrors.Generic &&
            AuctionBuyErrors.ToClientCode(AuctionBuyError.TemplateMismatch) == AuctionBuyErrors.Generic,
            "★「必须让玩家处理或知情」→ 通用码 0xd2（金币不足 / 数据不可用 / 陈旧计划 / 请求不完整 / 模板不符）");
        That(AuctionBuyErrors.ToClientCode(AuctionBuyError.SelfBuy) == AuctionBuyErrors.SelfListing, "★ 0x69 只用于「买自己的挂牌」");
    }

    /// <summary>只读规划：金额口径、买自己的挂牌、金币不足、状态/过期/纯竞拍、模板不符、机器人卖家。</summary>
    static void Planning()
    {
        using var f = new Fixture();
        // ① 单价 = 总额（数量 = 1）：两个实机样本正是这一档，**区分不了**单价与总额，单独标出来。
        int single = f.AddListing(f.Seller.Id, f.Seller.Name, SampleTemplate, 1, 24_000);
        var priceCase = f.Plan(single);
        That(priceCase.Error == AuctionBuyError.None && priceCase.Options is not null, "整件挂牌（数量 1、一口价 24000）规划成功");
        var plan = priceCase.Options!;
        That(plan.UnitPrice == 24_000 && plan.TotalPrice == 24_000 && plan.AmountBasis == AuctionBuyAmountBasis.UnitEqualsTotal,
            "★ 数量 = 1：命中口径记成 UnitEqualsTotal —— 这一档的样本无法判定客户端发的是单价还是总额");

        // ② 数量 > 1：单价与总额不同值，两个口径分别命中（第三个样本要解决的正是这件事）。
        int stack = f.AddListing(f.Seller.Id, f.Seller.Name, StackTemplate, 3, 1_000);
        var unitHit = f.Plan(stack, amount: 1_000).Options!;
        That(unitHit.AmountBasis == AuctionBuyAmountBasis.UnitPrice && unitHit.TotalPrice == 3_000 && unitHit.ItemCount == 3,
            "★ [1..4] = 单价 1000 命中单价口径（成交价仍取库里的总额 3000，不采信请求值）");
        var totalHit = f.Plan(stack, amount: 3_000).Options!;
        That(totalHit.AmountBasis == AuctionBuyAmountBasis.TotalPrice && totalHit.TotalPrice == 3_000,
            "★ [1..4] = 总额 3000 命中总额口径：两个口径都接受，且各自记进日志");
        var neither = f.Plan(stack, amount: 2_000);
        That(neither.Error == AuctionBuyError.AmountMismatch && neither.Options is null && neither.Detail is not null,
            "★ 与单价、总额都不符 ⇒ 拒绝，并把请求值与库里两个值一起带进日志");
        That(AuctionBuyErrors.ToClientCode(neither.Error) == 0x7e, "金额不符映射静默码 0x7e");
        That(unitHit.SellerProceeds == AuctionService.SellerProceeds(3_000) && unitHit.SellerProceeds == 2_850,
            "★ 卖方实收 = floor(成交价 × 95%) = 2850（按库里的总额 3000 算，不按请求里的单价）");

        // ③ 买自己的挂牌。
        int own = f.AddListing(f.Buyer.Id, f.Buyer.Name, SampleTemplate, 1, 12_000);
        var self = f.Plan(own);
        That(self.Error == AuctionBuyError.SelfBuy && AuctionBuyErrors.ToClientCode(self.Error) == 0x69,
            "★ 买自己的挂牌 ⇒ 0x69，且不产生任何计划");

        // ④ 金币不足。
        using var poor = new Fixture(buyerGold: 100);
        int cheap = poor.AddListing(poor.Seller.Id, poor.Seller.Name, SampleTemplate, 1, 24_000);
        var broke = poor.Plan(cheap);
        That(broke.Error == AuctionBuyError.InsufficientGold && AuctionBuyErrors.ToClientCode(broke.Error) == 0xd2,
            "★ 金币不足（100 < 24000）⇒ 0xd2（必须让玩家看到提示），不产生计划");

        // ⑤ 状态与价格前提。
        int inactive = f.AddListing(f.Seller.Id, f.Seller.Name, SampleTemplate, 1, 15_000);
        f.Execute($"UPDATE auction_listings SET state={AuctionListingState.Cancelled} WHERE listing_id={inactive}");
        That(f.Plan(inactive).Error == AuctionBuyError.NotActive, "已下架的挂牌不能购买（前提已经没了）");
        int expired = f.AddListing(f.Seller.Id, f.Seller.Name, SampleTemplate, 1, 15_000, expires: f.Now - 1);
        That(f.Plan(expired).Error == AuctionBuyError.Expired, "已过期的挂牌不能购买（状态位还没流转，靠 expires_at_unix 判）");
        int auctionOnly = f.AddListing(f.Seller.Id, f.Seller.Name, SampleTemplate, 1, -1);
        That(f.Plan(auctionOnly, amount: 15_000).Error == AuctionBuyError.AuctionOnly,
            "★ 一口价 -1（纯竞拍）⇒ 拒绝：竞价不实现，绝不能按一口价把纯竞拍的挂牌卖掉");
        That(f.Plan(9999).Error == AuctionBuyError.NotFound, "挂牌不存在 ⇒ 拒绝");
        That(f.Plan(0).Error == AuctionBuyError.InvalidRequest, "挂牌 id 非正 ⇒ 请求非法（0xd2）");
        That(f.Plan(single, amount: 0).Error == AuctionBuyError.InvalidRequest, "金额非正 ⇒ 请求非法（0xd2）");

        // ⑥ [26..29] 与库内模板不一致。
        var mismatch = f.Plan(single, templateId: 999_999);
        That(mismatch.Error == AuctionBuyError.TemplateMismatch && mismatch.Options is null,
            "★ [26..29] 与库内 item_template_id 不符 ⇒ 拒绝（客户端那行与库里不是同一件东西）");
        That(AuctionBuyErrors.ToClientCode(mismatch.Error) == 0xd2, "模板不符按「数据不可用」走通用码");

        // ⑦ 机器人卖家：规划里只有买方道具一封（卖方 id 为 NULL ⇒ 没有金币托管）。
        using var robot = new Fixture();
        int robotListing = robot.AddListing(null, "拍卖机器人甲", SampleTemplate, 1, 24_000);
        var robotPlan = robot.Plan(robotListing).Options!;
        That(robotPlan.Custody.Length == 1 && robotPlan.Custody[0].Required && robotPlan.Custody[0].Gold == 0 &&
            robotPlan.Custody[0].Key == AuctionService.CustodyItemKey(robotListing) &&
            robotPlan.Custody[0].RecipientCharacterId == robot.Buyer.Id,
            "★ 机器人卖家：托管计划只有买方道具一封（幂等键 auction:item:{id}），没有卖方金币那一封");
        That(robotPlan.SellerCharacterId is null && robotPlan.SellerName == "拍卖机器人甲",
            "计划里保留卖家读数（NULL + 名字），供日志与「金币不入账」核对");

        // ⑧ 真实卖家：两封托管，金币那封的收件人是卖家、金额是卖方实收。
        That(plan.Custody.Length == 2 && plan.Custody[1].RecipientCharacterId == f.Seller.Id &&
            plan.Custody[1].Gold == 22_800 && !plan.Custody[1].Required &&
            plan.Custody[1].Key == AuctionService.CustodyGoldKey(single),
            "★ 真实卖家：第二封托管给卖家本人，金额 = floor(24000 × 95%) = 22800，幂等键 auction:gold:{id}");
        That(plan.Inventory.Reason == "auction_buy" && plan.Inventory.ChangedSlots.IsEmpty &&
            plan.Inventory.ChangedCurrencies.Length == 1 && plan.Inventory.ChangedCurrencies[0] == CurrencyKind.Gold,
            "★ 买方扣款计划只改金币一个货币项、不动任何背包槽（道具走托管邮件，不占背包）");
        That(plan.Inventory.Before.Gold == f.Buyer.Inventory.Gold && plan.Inventory.After.Gold == f.Buyer.Inventory.Gold - 24_000,
            "买方扣款计划的 before/after 就是「当前金币 → 当前金币 − 成交价」");
        That(plan.ExpiresEchoMatches && plan.RequestExpiresAtUnix == plan.ExpiresAtUnix,
            "[9..12] 与库内到期时间一致（当前只记日志：D95 是否把它当乐观锁门闸尚未实测）");
    }

    /// <summary>成交：买方金币减、挂牌 state=1、两封托管邮件、审计有行；卖方钱包不动（托管 ≠ 入账）。</summary>
    static void Commit()
    {
        using var f = new Fixture();
        int listingId = f.AddListing(f.Seller.Id, f.Seller.Name, SampleTemplate, 1, 24_000);
        var listing = f.Service.FindListing(listingId)!;
        long sellerGoldBefore = f.Gold(f.Seller.Id);
        long inventoryAuditBefore = f.QueryLong("SELECT count(*) FROM inventory_audit_log");
        var plan = f.Plan(listingId).Options!;
        var committed = f.Service.Buy(f.BuyerOwner, f.Buyer, plan);
        That(committed.Error == AuctionBuyError.None && committed.Character is not null, "购买提交成功并返回提交后的角色快照");
        var after = committed.Character!;
        That(after.Revision == f.Buyer.Revision + 1 && after.Inventory.Gold == f.Buyer.Inventory.Gold - 24_000,
            "★ 买方金币减 24000、revision 前进一次（与挂牌成交、托管行同一个提交）");
        That(f.Gold(f.Buyer.Id) == f.Buyer.Inventory.Gold - 24_000, "独立读库确认买方金币已扣（不是只改了内存）");

        That(f.ListingState(listingId) == AuctionListingState.Sold && f.ListingSettled(listingId) == f.Now,
            "★ 独立读库确认挂牌 state=1（已成交）且 settled_at_unix = 本次提交时间");
        That(f.Service.Search(new(0, null, AuctionCategoryFilter.All), null, f.Now).TotalCount == 0,
            "★ 成交后该挂牌从在售搜索里消失（卖家自己的「拍卖历史」仍能看到）");
        That(f.Service.SellerListings(f.Seller.Id, f.Now, includeInactive: true).Length == 1,
            "卖家的拍卖历史里仍有这一条（190 面板用 includeInactive）");

        // 两封托管邮件：买方道具 + 卖方所得。
        var mails = f.Mails();
        That(mails.Length == 2, $"托管邮件两封（实际 {mails.Length} 封）");
        var itemMail = mails.Single(mail => mail.Key == AuctionService.CustodyItemKey(listingId));
        var goldMail = mails.Single(mail => mail.Key == AuctionService.CustodyGoldKey(listingId));
        That(itemMail.RecipientId == f.Buyer.Id && itemMail.Gold == 0 && itemMail.SenderName == f.Seller.Name &&
            itemMail.System && itemMail.Attachments == 1,
            "★ 买方道具托管邮件：收件人是买方、无金币、发件人名 = 卖家名、系统邮件、带 1 个附件");
        That(goldMail.RecipientId == f.Seller.Id && goldMail.Gold == 22_800 && goldMail.Attachments == 0 &&
            goldMail.SenderName == AuctionService.SystemSender,
            "★ 卖方所得托管邮件：收件人是卖方、金币 22800（成交价 × 95%）、无附件、发件人是拍卖行");
        That(mails.All(mail => mail.State == 1 && mail.Expires is null),
            "两封托管都是未读、无期限（托管 ≠ 入账：不受邮箱容量与期限限制）");
        That(f.QueryLong("SELECT count(*) FROM mailbox_audit_log") == 2,
            "两封托管各有 mailbox_audit_log 行（投递审计与托管行同一事务）");

        // 买方道具：托管实例归买方所有、不在任何真实槽位、核心与挂牌快照一致。
        var held = f.HeldItem(itemMail.MailId);
        var expectedCore = AuctionPackets.ItemCoreFromEntry(listing.ItemEntry, ItemKind.Equipment);
        That(held.CharacterId == f.Buyer.Id && held.Slots == 0 &&
            JsonSerializer.Deserialize<ItemCore>(held.Core) == expectedCore,
            "★ 托管道具归买方所有、不在任何槽位（`character_inventory_slots` 无行），核心就是挂牌保存的那份快照");
        That(f.Repository.LoadCharacter(f.BuyerOwner, f.Buyer.Slot).Inventory.Items.IsEmpty,
            "★ 道具**没有**直接进买方背包（购买阶段不检查背包容量，容量判定发生在领取时）");

        // 卖方钱包不动：托管 ≠ 入账。
        That(f.Gold(f.Seller.Id) == sellerGoldBefore,
            "★★ 卖方角色行金币完全没动：所得在托管邮件里，卖方领取时才落进钱包（跨账号不取卖方租约）");
        var sellerClaims = f.Repository.LoadMailClaims(f.SellerOwner, f.Seller.Id, [goldMail.MailId], f.Now);
        That(sellerClaims.Length == 1 && sellerClaims[0].Mail.Gold == 22_800, "★ 卖方能读到并领取这封托管（金额就是卖方实收）");
        var buyerClaims = f.Repository.LoadMailClaims(f.BuyerOwner, f.Buyer.Id,
            [MailboxAttachment.ClaimFlag | itemMail.AttachmentId], f.Now);
        That(buyerClaims.Length == 1 && buyerClaims[0].Attachment is not null,
            "★ 买方能领取托管道具（走邮件模块既有领取路径，不是购买路径直接入包）");

        // 审计：金币变更走 currency_audit_log（背包槽没变化 ⇒ inventory_audit_log 不产生新行）。
        var currencyAudit = f.CurrencyAudit("auction_buy");
        That(currencyAudit.Length == 1 && currencyAudit[0].Kind == (int)CurrencyKind.Gold &&
            currencyAudit[0].Before == f.Buyer.Inventory.Gold && currencyAudit[0].After == f.Buyer.Inventory.Gold - 24_000,
            "★ 买方扣款有一条 currency_audit_log（action=auction_buy，前后余额真实）");
        That(f.QueryLong("SELECT count(*) FROM inventory_audit_log") == inventoryAuditBefore,
            "纯金币变更不产生 inventory_audit_log 行（那张表按「变化槽位」记账，购买不动任何槽位）");

        // 真实上架路径写出的挂牌：卖方是自己的角色 ⇒ 0x69（在真实数据上成立）。
        using var register = new Fixture(withItem: true);
        var registerPlan = register.Service.PlanRegister(register.Buyer,
            AuctionRegisterRequest.Parse(RegisterBody()), register.Now).Options!;
        var registered = register.Repository.RegisterListing(register.BuyerOwner, register.Buyer, registerPlan);
        register.Buyer = registered.Character!;
        int registeredId = (int)register.QueryLong("SELECT max(listing_id) FROM auction_listings");
        That(register.Plan(registeredId).Error == AuctionBuyError.SelfBuy,
            "★ 自己刚上架的挂牌（真实上架路径写的行）买不了：0x69 的前提在真实数据上成立");

        // 机器人卖家：成交照常，但只有买方那一封托管。
        using var robot = new Fixture();
        int robotListing = robot.AddListing(null, "拍卖机器人甲", SampleTemplate, 1, 30_000);
        var robotCommit = robot.Service.Buy(robot.BuyerOwner, robot.Buyer, robot.Plan(robotListing).Options!);
        That(robotCommit.Error == AuctionBuyError.None && robotCommit.Character!.Inventory.Gold == robot.Buyer.Inventory.Gold - 30_000,
            "★ 机器人卖家：成交照常、买方照常扣款");
        That(robotCommit.Custody.Length == 1 && robotCommit.Custody[0].Delivered &&
            robot.Mails().Length == 1 && robot.ListingState(robotListing) == AuctionListingState.Sold,
            "★ 只有买方道具那一封托管；卖方所得不入账（系统回收），挂牌仍是已成交");

        // 卖方角色行已不存在（挂牌表没有指向 characters 的外键，机器人以外的脏数据也要能成交）。
        using var missing = new Fixture();
        int orphan = missing.AddListing(9999, "已被删除的卖家", SampleTemplate, 1, 20_000);
        var orphanCommit = missing.Service.Buy(missing.BuyerOwner, missing.Buyer, missing.Plan(orphan).Options!);
        That(orphanCommit.Error == AuctionBuyError.None && orphanCommit.Character is not null,
            "★★ 卖方角色行缺失：**成交不被外键回滚**（不是失败，也不给机器人建角色行）");
        That(orphanCommit.Custody.Length == 2 && orphanCommit.Custody[1] is { Delivered: false, Gold: 19_000 } &&
            orphanCommit.Custody[1].Note is not null,
            "★ 金币托管行被**跳过**并带上原因（金额是卖方实收 19000）：先重查 seller_character_id，查不到就不插" +
            "（先插会等外键报错并回滚整笔）");
        That(missing.Mails().Length == 1 && missing.ListingState(orphan) == AuctionListingState.Sold,
            "独立读库确认：只有买方道具一封托管，挂牌仍已成交");
    }

    /// <summary>回滚与幂等：条件更新 0 行、内容陈旧、托管插入失败、幂等键冲突。</summary>
    static void Rollback()
    {
        // ① 重复点击 / 并发第二笔：同一挂牌只允许成交一次，第二次必须整体回滚、零副作用。
        using var f = new Fixture();
        int listingId = f.AddListing(f.Seller.Id, f.Seller.Name, SampleTemplate, 1, 24_000);
        var firstPlan = f.Plan(listingId).Options!;    // 两笔都在 state=0 时规划（同一窗口内的两次点击/两个买家）
        var secondPlan = f.Plan(listingId).Options!;
        var first = f.Service.Buy(f.BuyerOwner, f.Buyer, firstPlan);
        That(first.Character is not null, "第一笔成交成功");
        string afterFirst = f.Snapshot();
        long goldAfterFirst = f.Gold(f.Buyer.Id);
        long settledAfterFirst = f.ListingSettled(listingId);
        var second = f.Service.Buy(f.BuyerOwner, f.Buyer, secondPlan);
        That(second.Error == AuctionBuyError.RaceLost && second.Character is null &&
            AuctionBuyErrors.ToClientCode(second.Error) == 0x7e && second.Detail is not null,
            "★★ 第二笔（条件更新 state=0→1 影响 0 行）⇒ 整体拒绝并回 0x7e（静默：只刷新窗口）");
        That(f.Snapshot() == afterFirst,
            "★★ 整库逐表 dump 与第一笔成交后完全一致：没有第二次扣款、没有第二封邮件、没有多一行审计");
        That(f.Gold(f.Buyer.Id) == goldAfterFirst && f.ListingSettled(listingId) == settledAfterFirst &&
            f.Mails().Length == 2 && f.ListingState(listingId) == AuctionListingState.Sold,
            "金币 / 结算时间 / 托管封数 / 挂牌状态逐项不变（幂等判据就是挂牌状态）");

        // ② 规划之后挂牌内容变化：绝不在内容变了之后按旧计划结算。
        using var changed = new Fixture();
        int mutable = changed.AddListing(changed.Seller.Id, changed.Seller.Name, SampleTemplate, 1, 24_000);
        var stalePlan = changed.Plan(mutable).Options!;
        changed.Execute($"UPDATE auction_listings SET buyout_price=25000 WHERE listing_id={mutable}");
        string beforeStale = changed.Snapshot();
        var stale = changed.Service.Buy(changed.BuyerOwner, changed.Buyer, stalePlan);
        That(stale.Error == AuctionBuyError.StalePlan && stale.Character is null &&
            AuctionBuyErrors.ToClientCode(stale.Error) == 0xd2 && stale.Detail is not null,
            "★ 挂牌内容（一口价）在规划后变化 ⇒ 陈旧计划被拒（0xd2），不按旧计划结算");
        That(changed.Snapshot() == beforeStale && changed.ListingState(mutable) == AuctionListingState.Active,
            "★ 陈旧计划既没有扣款也没有把挂牌置为已成交（内容复核在事务内、条件更新之前）");

        // ③ 托管插入失败 ⇒ 连买方扣款一起回滚（跨账号资产不允许出现半成品）。
        using var failure = new Fixture();
        int rollback = failure.AddListing(failure.Seller.Id, failure.Seller.Name, SampleTemplate, 1, 24_000);
        var rollbackPlan = failure.Plan(rollback).Options!;
        string beforeFailure = failure.Snapshot();
        failure.Execute("CREATE TRIGGER fail_custody BEFORE INSERT ON mailbox_attachments BEGIN SELECT RAISE(ABORT,'custody-failure'); END");
        Reject<SqliteException>(() => failure.Service.Buy(failure.BuyerOwner, failure.Buyer, rollbackPlan),
            "★ 托管附件插入中途故障 ⇒ 异常向上抛（不伪装成功）");
        failure.Execute("DROP TRIGGER fail_custody");
        That(failure.Snapshot() == beforeFailure,
            "★★ 整笔回滚：买方金币、revision、挂牌状态与结算时间、两封托管行、全部审计都回到请求前");
        That(failure.Gold(failure.Buyer.Id) == failure.Buyer.Inventory.Gold &&
            failure.ListingState(rollback) == AuctionListingState.Active && failure.ListingSettled(rollback) == 0 &&
            failure.Mails().Length == 0,
            "★ 逐项复核：钱没扣、挂牌仍在售且没有结算时间、一封托管都没留下");

        // ④ 第二道幂等防线：托管邮件 key 已被别的资产占用 ⇒ 抛异常并整笔回滚。
        using var keyed = new Fixture();
        int keyedListing = keyed.AddListing(keyed.Seller.Id, keyed.Seller.Name, SampleTemplate, 1, 24_000);
        var keyedPlan = keyed.Plan(keyedListing).Options!;
        keyed.Repository.DeliverMail(new MailboxDelivery(AuctionService.CustodyItemKey(keyedListing),
            keyed.Repository.FindMailRecipient(keyed.Buyer.Name)!, "系统", "别的资产", 5, [], keyed.Now));
        string beforeKeyed = keyed.Snapshot();
        Reject<InvalidOperationException>(() => keyed.Service.Buy(keyed.BuyerOwner, keyed.Buyer, keyedPlan),
            "★ 同一托管 key 已有不同内容 ⇒ 唯一幂等键直接拒绝（不投两份）");
        That(keyed.Snapshot() == beforeKeyed && keyed.ListingState(keyedListing) == AuctionListingState.Active,
            "★ 幂等键冲突同样整笔回滚：挂牌没有被置为已成交（条件更新也一起回退）");

        // ⑤ 买方金币被别的提交改动过 ⇒ 背包提交器按 revision/快照复核拒绝（陈旧计划的安全网）。
        using var guard = new Fixture();
        int guarded = guard.AddListing(guard.Seller.Id, guard.Seller.Name, SampleTemplate, 1, 24_000);
        var guardedPlan = guard.Plan(guarded).Options!;
        guard.Buyer = guard.Repository.CommitInventory(guard.BuyerOwner, guard.Buyer,
            new(guard.Buyer.Inventory, guard.Buyer.Inventory with { Gold = 7 }, "other_mutation"));
        Reject<InvalidOperationException>(() => guard.Service.Buy(guard.BuyerOwner, guard.Buyer, guardedPlan),
            "陈旧计划在写事务内被 revision/快照复核拒绝（零变化，异常向上抛）");
        That(guard.ListingState(guarded) == AuctionListingState.Active && guard.Mails().Length == 0 && guard.Gold(guard.Buyer.Id) == 7,
            "★ 被拒绝的陈旧计划没有成交、没有托管行，买方余额仍是那笔别的提交的结果");
    }

    /// <summary>
    /// 真实链路：命令表派发 185 ⇒ 应答字节、在线快照安装、**不补发任何背包/金币刷新**，
    /// 以及各类失败的应答码与陈旧计划（换角）。
    /// </summary>
    static async Task Session()
    {
        string root = Path.Combine(Path.GetTempPath(), "S5A9-auction-buy-session-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "game.db");
            var owners = new AccountOwners();
            using var buyerOwner = owners.Acquire("auction-buyer", () => { });
            using var sellerOwner = owners.Acquire("auction-seller", () => { });
            var repository = new CharacterRepository(path);
            var catalog = new ItemCatalog(null!, null!, null!, 0);
            catalog.PrefillTemplates(Templates);
            var pieces = new EpicPieceCatalog("[piece list]\n60000 70001\n[/piece list]");
            var service = new AuctionService(repository, new AuctionCategoryResolver(catalog), catalog, pieces,
                AuctionPackets.ItemEntrySnapshot, AuctionPackets.ItemCoreFromEntry);
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var buyer = repository.Create(buyerOwner, new("Buyer", Room, null) { Level = 95, Inventory = new([], Gold: 100_000_000) });
            var seller = repository.Create(sellerOwner, new("Seller", Room, null) { Level = 95, Inventory = new([], Gold: 777) });
            int listingId = Insert(path, repository, seller.Id, seller.Name, 1, 24_000, now);
            int expensive = Insert(path, repository, seller.Id, seller.Name, 1, 900_000_000, now);
            int ownListing = Insert(path, repository, buyer.Id, buyer.Name, 1, 12_000, now);
            int later = Insert(path, repository, seller.Id, seller.Name, 1, 30_000, now);

            var towns = new TownCatalog(new Dictionary<(byte, byte), TownArea>(), Room, Room, new TownDock(0, [], []),
                [], new Dictionary<uint, uint[]>());
            var stats = new CharacterStatComputer(new CharacterCatalog(new Dictionary<byte, CharacterProfile>()));
            var projection = new InventoryProjection(stats, new TownProjection(towns, stats, null, pieces), pieces);
            string logDirectory = Path.Combine(root, "Logs");
            using var log = new ServerLog(logDirectory);
            var handler = new AuctionHandler(catalog, service, projection,
                static () => AuctionProbeSettings.Disabled, static () => AuctionProbeSettings.Disabled,
                static _ => AuctionProbeSettings.Disabled, log);
            var commands = new GameCommandRegistry();
            handler.Register(commands);
            commands.Seal();

            var session = new GameSession(repository, towns, buyerOwner);
            session.Select(buyer.Slot);
            session.MarkReady();
            var planned = session.Selected!;
            using var stop = new CancellationTokenSource();
            using var stream = new MemoryStream();
            var sent = new List<(ushort Opcode, byte[] Body, string Action)>();
            await using var sender = new GameConnectionSender(buyerOwner, stream, stop, 0,
                (_, opcode, _, body, action) => sent.Add((opcode, body.ToArray(), action)));
            var context = new GameConnectionContext(new(1, IPAddress.Loopback, stream, stop), buyerOwner, session, sender);

            // ① 成功：应答 `flag + mode`，而且**只有**这一个包（成交后不补发金币刷新）。
            var ok = await commands.DispatchAsync(context, new Packet(1, 185, Body(listingId, 24_000, SampleTemplate, now + 86_400)));
            That(ok == CommandResult.Handled && sent.Count == 1 && sent[0].Opcode == 185 &&
                sent[0].Body.SequenceEqual<byte>([0x01, 0x00]),
                "★ 185 应答 = flag(01) + mode(00)，且**整条请求只发这一个包**（不补发 0x000D/0x000E：185 回调已本地扣过金币）");
            That(sent[0].Action == "auction_buy", "应答动作名是 auction_buy（日志与发送队列可对照）");
            That(session.Selected!.Revision == planned.Revision + 1 &&
                session.Selected.Inventory.Gold == planned.Inventory.Gold - 24_000,
                "★ 提交后的快照安装回会话：在线金币与 revision 都前进了（下一笔操作不会被判成陈旧）");
            That(repository.LoadCharacter(buyerOwner, buyer.Slot).Inventory.Gold == planned.Inventory.Gold - 24_000,
                "独立读库与会话内的金币一致");

            // ② 再点一次同一条挂牌：规划期就看出「已不在售」⇒ 静默 0x7e，且零副作用。
            sent.Clear();
            string afterFirst = Dump(path);
            await commands.DispatchAsync(context, new Packet(1, 185, Body(listingId, 24_000, SampleTemplate, now + 86_400)));
            That(sent.Count == 1 && sent[0].Body.SequenceEqual<byte>([0x00, 0x7e, 0]),
                "★ 重复点击同一条挂牌 ⇒ 0x7e（静默：只刷新窗口、不弹框）");
            That(Dump(path) == afterFirst, "★ 重复点击零副作用（整库 dump 一致）");

            // ③ 买自己的挂牌 ⇒ 0x69。
            sent.Clear();
            await commands.DispatchAsync(context, new Packet(1, 185, Body(ownListing, 12_000, SampleTemplate, now + 86_400)));
            That(sent.Count == 1 && sent[0].Body.SequenceEqual<byte>([0x00, 0x69, 0]),
                "买自己保留旧领域选码105并补齐u16；该码客户端文案是登录保护，不视为专用文案验收");

            // ④ 金币不足 ⇒ 0xd2；金额不符 ⇒ 0x7e。
            sent.Clear();
            await commands.DispatchAsync(context, new Packet(1, 185, Body(expensive, 900_000_000, SampleTemplate, now + 86_400)));
            That(sent.Count == 1 && sent[0].Body.SequenceEqual<byte>([0x00, 0xd2, 0]), "金币不足保留原D2策略并补齐u16；客户端安全模式文案仍属领域映射缺口");
            sent.Clear();
            await commands.DispatchAsync(context, new Packet(1, 185, Body(later, 123, SampleTemplate, now + 86_400)));
            That(sent.Count == 1 && sent[0].Body.SequenceEqual<byte>([0x00, 0x7e, 0]), "★ 金额与库内不符 ⇒ 0x7e");

            // ⑤ 不足 30 字节 ⇒ 白名单码（绝不能走进「未知码 = 成功」）。
            sent.Clear();
            await commands.DispatchAsync(context, new Packet(1, 185, Convert.FromHexString(SampleA)[..20]));
            That(sent.Count == 1 && sent[0].Body.SequenceEqual<byte>([0x00, 0xd2, 0]), "★ 请求体不足 30 字节 ⇒ 0xd2（读不到 [26..29] 就拒绝）");
            That(Dump(path) == afterFirst,
                "★★ 买自己的挂牌 / 金币不足 / 金额不符 / 短包这几次拒绝全部零副作用（整库 dump 仍与成交后一致）");

            // ⑥ 陈旧计划（换角/接管）：会话在进锁后复核「计划所依据的角色仍是当前在线角色」。
            var stalePlan = service.PlanBuy(session.Selected!, service.FindListing(later),
                AuctionBuyRequest.Parse(Body(later, 30_000, SampleTemplate, now + 86_400)), now).Options!;
            var captured = session.Selected!;
            session.ReturnToRoster();          // 换角：Selected 清空、epoch 前进（接管同理）
            session.Select(buyer.Slot);
            session.MarkReady();
            string afterReselect = Dump(path);
            var refused = session.BuyListing(service, captured, stalePlan);
            That(refused.Buy.Error == AuctionBuyError.StalePlan && refused.Buy.Character is null && refused.Receipt is null &&
                AuctionBuyErrors.ToClientCode(refused.Buy.Error) == 0xd2,
                "★ 陈旧计划（换角后提交前捕获的角色）⇒ StalePlan / 0xd2，不提交、也不产出回执");
            That(repository.LoadCharacter(buyerOwner, buyer.Slot).Inventory.Gold == planned.Inventory.Gold - 24_000 &&
                Dump(path) == afterReselect,
                "★ 陈旧计划零变化：只有第一笔成交扣过款（换角之后整库 dump 仍然一致）");

            // ⑦ 334 仍是抓包桩：不回包。
            sent.Clear();
            var stub = await commands.DispatchAsync(context, new Packet(1, 334, new byte[40]));
            That(stub == CommandResult.Unhandled && sent.Count == 0, "★ 334 仍未实现：一个包都不发（与 AuctionBuyProbeTests 同口径）");

            log.Flush();
            var rejected = Logged(Path.Combine(logDirectory, "server.jsonl"), "auction_buy_rejected");
            That(rejected.Count >= 5 && rejected.All(entry => entry.GetProperty("ClientCode").GetInt32() is 0xd2 or 0x7e or 0x69),
                $"★ 每次拒绝都写 auction_buy_rejected 并带上实际下发的白名单码（{rejected.Count} 条）");
            var bought = Logged(Path.Combine(logDirectory, "server.jsonl"), "auction_buy");
            That(bought.Count == 1 && bought[0].GetProperty("AmountBasis").GetString() == "UnitEqualsTotal" &&
                bought[0].GetProperty("TotalPrice").GetInt64() == 24_000 &&
                bought[0].GetProperty("SellerProceeds").GetInt64() == 22_800 &&
                bought[0].GetProperty("GoldRefresh").GetString() == "none",
                "★ 成交日志记下口径读数（UnitEqualsTotal）、成交价、卖方实收，并写明不发金币刷新");
            That(bought[0].GetProperty("Custody").GetArrayLength() == 2,
                "★ 成交日志同时记下两封托管行各自的结果（含是否被跳过及原因）");
        }
        finally
        {
            TestFiles.DeleteDirectory(root, recursive: true);
        }
    }

    /// <summary>购买请求体（前导 0 + 一口价 + 挂牌 id + 到期回显 + 13B 保留 + 模板 id）。</summary>
    static byte[] Body(int listingId, int amount, int templateId, long expiresAtUnix, int length = AuctionBuyRequest.BodySize)
    {
        var body = new byte[length];
        BitConverter.GetBytes(amount).CopyTo(body, AuctionBuyRequest.AmountOffset);
        BitConverter.GetBytes(listingId).CopyTo(body, AuctionBuyRequest.ListingIdOffset);
        BitConverter.GetBytes((int)expiresAtUnix).CopyTo(body, AuctionBuyRequest.ExpiresEchoOffset);
        BitConverter.GetBytes(templateId).CopyTo(body, AuctionBuyRequest.ItemTemplateIdOffset);
        return body;
    }

    /// <summary>上架请求体（实机样本形状：主背包槽 27、数量 1、起拍价空、一口价 17000、24 小时）。</summary>
    static byte[] RegisterBody()
    {
        var body = new byte[AuctionRegisterRequest.BodySize];
        body[2] = 27;
        BitConverter.GetBytes(SampleTemplate).CopyTo(body, 4);
        BitConverter.GetBytes(1).CopyTo(body, 8);
        BitConverter.GetBytes(-1).CopyTo(body, 12);
        BitConverter.GetBytes(17_000).CopyTo(body, 16);
        BitConverter.GetBytes(17_000).CopyTo(body, 20);
        BitConverter.GetBytes(24).CopyTo(body, 24);
        return body;
    }

    /// <summary>插入一条机器人/真实卖家挂牌并返回 listing_id（会话组自己建库，不复用夹具）。</summary>
    static int Insert(string path, CharacterRepository repository, long? sellerId, string sellerName, int count, int buyout, long now)
    {
        var core = InventoryRewardGrantService.Create(Templates[SampleTemplate], count);
        repository.InsertListings([new(sellerId, sellerName, SampleTemplate, (byte)core.Kind, count,
            AuctionPackets.ItemEntrySnapshot(core), Math.Max(0, buyout - 1_000), buyout, now, now + 86_400)]);
        return (int)Query(path, "SELECT max(listing_id) FROM auction_listings");
    }

    /// <summary>整库逐表 dump：任何一个字节的变化都会让「零副作用」断言失败。</summary>
    static string Dump(string path)
    {
        using var db = Open(path);
        using var command = db.CreateCommand();
        var tables = new List<string>();
        command.CommandText = "SELECT name FROM sqlite_schema WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
        using (var reader = command.ExecuteReader())
            while (reader.Read())
                tables.Add(reader.GetString(0));
        var rows = new List<object>();
        foreach (string table in tables)
        {
            command.CommandText = "SELECT * FROM [" + table + "] ORDER BY rowid";
            using var reader = command.ExecuteReader();
            while (reader.Read())
                rows.Add(new { Table = table, Values = Enumerable.Range(0, reader.FieldCount).Select(reader.GetValue).ToArray() });
        }
        return JsonSerializer.Serialize(rows);
    }

    static long Query(string path, string sql)
    {
        using var db = Open(path);
        using var command = db.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    static SqliteConnection Open(string path)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false,
            ForeignKeys = true
        }.ToString());
        db.Open();
        return db;
    }

    /// <summary>读回服务器日志里某一类记录（真实 jsonl 行，共享读写打开）。</summary>
    static List<JsonElement> Logged(string path, string kind)
    {
        var details = new List<JsonElement>();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            using var document = JsonDocument.Parse(line);
            if (document.RootElement.GetProperty("Kind").GetString() == kind)
                details.Add(document.RootElement.GetProperty("Detail").Clone());
        }
        return details;
    }

    /// <summary>托管邮件的只读投影（自测直接读库核对，不依赖业务层的读用例）。</summary>
    readonly record struct MailRow(uint MailId, string Key, long RecipientId, string SenderName, long Gold,
        long? Expires, int State, bool System, int Attachments)
    {
        public uint AttachmentId { get; init; }
    }

    /// <summary>托管道具行的只读投影。</summary>
    readonly record struct HeldRow(long Uid, long CharacterId, string Core, long Slots);

    /// <summary>货币审计行的只读投影。</summary>
    readonly record struct CurrencyRow(string Action, int Kind, long Before, long After);

    /// <summary>真实文件库 + 买方账号 + 卖方账号（另一个账号的真实角色，用来验证跨账号托管）。</summary>
    sealed class Fixture : IDisposable
    {
        readonly string root;
        public string Path { get; }
        public AccountOwners Owners { get; } = new();
        public AccountOwners.Lease BuyerOwner { get; }
        public AccountOwners.Lease SellerOwner { get; }
        public CharacterRepository Repository { get; }
        public AuctionService Service { get; }
        public Character Buyer { get; set; }
        public Character Seller { get; }
        public long Now { get; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        public Fixture(long buyerGold = 100_000_000, bool withItem = false)
        {
            root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "S5A9-auction-buy-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            Path = System.IO.Path.Combine(root, "game.db");
            BuyerOwner = Owners.Acquire("auction-buyer", () => { });
            SellerOwner = Owners.Acquire("auction-seller", () => { });
            Repository = new(Path);
            var catalog = new ItemCatalog(null!, null!, null!, 0);
            catalog.PrefillTemplates(Templates);
            Service = new AuctionService(Repository, new AuctionCategoryResolver(catalog), catalog,
                new EpicPieceCatalog("[piece list]\n60000 70001\n[/piece list]"),
                AuctionPackets.ItemEntrySnapshot, AuctionPackets.ItemCoreFromEntry);
            // 走真实上架路径的用例需要在主背包槽 27 放一件可上架装备（其余用例不需要、也不该依赖它）。
            ImmutableArray<InventoryItem> items = withItem
                ? [new(0, new(InventoryListType.Main, 27), new ItemCore(SampleTemplate, ItemKind.Equipment, 999_999_998) { Attr = 2, Durability = 45 })]
                : [];
            Buyer = Repository.Create(BuyerOwner, new("Buyer", Room, null) { Level = 95, Inventory = new(items, Gold: (uint)buyerGold) });
            Seller = Repository.Create(SellerOwner, new("Seller", Room, null) { Level = 95, Inventory = new([], Gold: 777) });
        }

        /// <summary>插入一条挂牌并返回它的 listing_id。卖方可为 NULL（机器人）或不存在的角色 id。</summary>
        public int AddListing(long? sellerId, string sellerName, int templateId, int count, int buyout, long? expires = null)
        {
            var core = InventoryRewardGrantService.Create(Templates[templateId], count);
            // 表约束要求 expires >= posted：造「已过期」的行时把上架时间一起放回过去。
            long expiry = expires ?? Now + 86_400;
            long posted = Math.Min(Now, expiry - 1);
            Repository.InsertListings([new(sellerId, sellerName, templateId, (byte)core.Kind, count,
                AuctionPackets.ItemEntrySnapshot(core), Math.Max(0, buyout - 1_000), buyout, posted, expiry)]);
            return (int)QueryLong("SELECT max(listing_id) FROM auction_listings");
        }

        /// <summary>按库内那一行构造请求（金额默认取库里的总额口径）。</summary>
        public AuctionBuyRequest Request(int listingId, int? amount = null, int? templateId = null)
        {
            var listing = Service.FindListing(listingId)!;
            return AuctionBuyRequest.Parse(Body(listingId, amount ?? checked((int)((long)listing.BuyoutPrice * listing.ItemCount)),
                templateId ?? listing.ItemTemplateId, listing.ExpiresAtUnix));
        }

        public AuctionBuyDecision Plan(int listingId, int? amount = null, int? templateId = null)
        {
            // 行不存在（含 id 非正）时按「没有这一行」交给规划层：请求体只能用手上的字段拼。
            var listing = listingId > 0 ? Service.FindListing(listingId) : null;
            return listing is null
                ? Service.PlanBuy(Buyer, null, AuctionBuyRequest.Parse(Body(listingId, amount ?? 1, templateId ?? SampleTemplate, Now)), Now)
                : Service.PlanBuy(Buyer, listing, Request(listingId, amount, templateId), Now);
        }

        public MailRow[] Mails()
        {
            using var db = Open();
            using var command = db.CreateCommand();
            command.CommandText = """
                SELECT m.mail_id,m.delivery_key,m.recipient_id,m.sender_name,m.gold,m.expires_at,m.state,m.system_mail,
                       (SELECT count(*) FROM mailbox_attachments a WHERE a.mail_id=m.mail_id),
                       (SELECT coalesce(max(a.attachment_id),0) FROM mailbox_attachments a WHERE a.mail_id=m.mail_id)
                FROM mailbox_messages m ORDER BY m.mail_id;
                """;
            var rows = new List<MailRow>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
                rows.Add(new(checked((uint)reader.GetInt64(0)), reader.GetString(1), reader.GetInt64(2), reader.GetString(3),
                    reader.GetInt64(4), reader.IsDBNull(5) ? null : reader.GetInt64(5), reader.GetInt32(6),
                    reader.GetInt32(7) != 0, reader.GetInt32(8)) { AttachmentId = checked((uint)reader.GetInt64(9)) });
            return [.. rows];
        }

        public HeldRow HeldItem(uint mailId)
        {
            using var db = Open();
            using var command = db.CreateCommand();
            command.CommandText = """
                SELECT i.item_uid,i.character_id,i.item_core,
                       (SELECT count(*) FROM character_inventory_slots s WHERE s.item_uid=i.item_uid)
                FROM mailbox_attachments a JOIN character_inventory_items i ON i.item_uid=a.item_uid
                WHERE a.mail_id=$mail;
                """;
            command.Parameters.AddWithValue("$mail", mailId);
            using var reader = command.ExecuteReader();
            reader.Read();
            return new(reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), reader.GetInt64(3));
        }

        public CurrencyRow[] CurrencyAudit(string action)
        {
            using var db = Open();
            using var command = db.CreateCommand();
            command.CommandText = "SELECT action_name,currency_kind,balance_before,balance_after FROM currency_audit_log WHERE action_name=$action ORDER BY audit_id";
            command.Parameters.AddWithValue("$action", action);
            var rows = new List<CurrencyRow>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
                rows.Add(new(reader.GetString(0), reader.GetInt32(1), reader.GetInt64(2), reader.GetInt64(3)));
            return [.. rows];
        }

        public long Gold(long characterId) => QueryLong($"SELECT gold FROM characters WHERE id={characterId}");

        public int ListingState(int listingId) => (int)QueryLong($"SELECT state FROM auction_listings WHERE listing_id={listingId}");

        public long ListingSettled(int listingId) =>
            Convert.ToInt64(Query($"SELECT coalesce(settled_at_unix,0) FROM auction_listings WHERE listing_id={listingId}"), CultureInfo.InvariantCulture);

        public long QueryLong(string sql) => Convert.ToInt64(Query(sql), CultureInfo.InvariantCulture);

        public object? Query(string sql)
        {
            using var db = Open();
            using var command = db.CreateCommand();
            command.CommandText = sql;
            return command.ExecuteScalar();
        }

        public void Execute(string sql)
        {
            using var db = Open();
            using var command = db.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        /// <summary>整库逐表 dump：任何一个字节的变化都会让「零副作用」断言失败。</summary>
        public string Snapshot() => Dump(Path);

        SqliteConnection Open()
        {
            var db = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path,
                Pooling = false,
                ForeignKeys = true
            }.ToString());
            db.Open();
            return db;
        }

        public void Dispose()
        {
            SellerOwner.Dispose();
            BuyerOwner.Dispose();
            TestFiles.DeleteDirectory(root, true);
        }
    }

    /// <summary>与运行中实例无关的预置模板：整件装备 + 一堆材料（取值即断言的依据）。</summary>
    static readonly Dictionary<int, ItemTemplate> Templates = new()
    {
        [SampleTemplate] = new(SampleTemplate, ItemKind.Equipment, 15, 1, 45, 1, 1, 0, 1, "[free]", 65535, 0, "残缺的牛皮背心"),
        [StackTemplate] = new(StackTemplate, ItemKind.Material, 0, 1, 0, 999, 1, 0, 1, "[free]", 65535, 0, "粗布腰带随机礼盒")
    };
}
