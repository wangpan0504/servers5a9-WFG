using static DfoServer.Testing.TestCheck;
using System.Collections.Immutable;
using System.Text.Json;
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
/// 拍卖行上架（CMD 183 `AUCTION_REGIST_ITEM`）的解析、业务校验、事务与提交后行为。
///
/// 分四组：
///   1. <see cref="Parsing"/>：**用实机抓到的真实样本逐字段断言**（37 字节、slot=27、itemId=10400、数量 1、
///      起拍价 -1、一口价 17000、时长 24），并把「主体不足 28 字节即拒绝」与时长白名单回落钉死；
///   2. <see cref="Rejections"/>：槽位/物品交叉校验、数量、价格上限、不可交易/已绑定/计时/锁定、
///      在售上限 —— 每条都要断言**没有产生任何扣减计划**（拒绝不留半成品）；
///   3. <see cref="Commit"/>：扣物品 + 插挂牌的**同事务**、拆堆上架、失败整体回滚，
///      以及「上架成功后自己搜不到、别人搜得到」（官方排除自己的规则）；
///   4. <see cref="Wire"/>：183 应答的字节（`flag + mode`）、白名单与「未知码拒发」。
///
/// 没有 PVF：模板用 <see cref="ItemCatalog.PrefillTemplates"/> 显式预置，走的是真实的 `Get` 命中路径；
/// 数据库用真实文件库，因此 revision / 审计 / 回滚都是真的。
/// </summary>
internal static class AuctionRegisterTests
{
    /// <summary>实机样本（点击「开始拍卖」发出，界面值：10400 残缺的牛皮背心、数量 1、起拍价空、一口价 17000、24 小时）。</summary>
    const string Sample =
        "00001B00A028000001000000FFFFFFFF684200006842000018000000000000000000000000";

    /// <summary>实机样本里的物品（残缺的牛皮背心）。</summary>
    const int SampleTemplate = 10400;
    /// <summary>可堆叠样本（粗布腰带随机礼盒），用于拆堆与数量边界。</summary>
    const int StackTemplate = 10014751;
    /// <summary>`[sealing]` 模板：未解封（`SealFlag = 0`）时不可交易。</summary>
    const int SealedTemplate = 10000201;
    /// <summary>`[trade limit]` 模板：按 `Attr` 高 3 位的剩余交易次数判定。</summary>
    const int TradeLimitTemplate = 10000202;

    public static void Run()
    {
        int checks = Count;
        Parsing();
        Rejections();
        Commit();
        Wire();
        Console.WriteLine($"PASS {Count - checks} auction register assertions");
    }

    /// <summary>
    /// 请求体解析：**逐字段对照实机样本**。字段位置错一位就会扣错道具，因此这里不做「只验长度」的弱断言。
    /// </summary>
    static void Parsing()
    {
        byte[] body = Convert.FromHexString(Sample);
        That(body.Length == 37 && AuctionRegisterRequest.BodySize == 28,
            "实机样本 37 字节 = 28 字节主体 + 9 字节尾部补 0");
        var request = AuctionRegisterRequest.Parse(body);
        That(request.Leading == 0, "样本 [0] 前导 0");
        That(request.SourceList == 0, "样本 [1] 源列表类型 = 0（推断字段，主背包）");
        That(request.SourceSlot == 27, "样本 [2..3] 源槽位 = 27（推断字段）");
        That(request.ItemTemplateId == 10400, "样本 [4..7] 物品模板 id = 10400");
        That(request.ItemCount == 1, "样本 [8..11] 数量 = 1");
        That(request.StartingPrice == -1, "样本 [12..15] 起拍价 = -1（界面上留空）");
        That(request.BuyoutPrice == 17000, "样本 [16..19] 一口价 = 17000");
        That(request.BuyoutEcho == 17000 && request.BuyoutEchoMatches, "样本 [20..23] 是一口价回显，与 [16..19] 同值");
        That(request.DurationHours == 24, "样本 [24..27] 上架时长 = 24 小时");
        That(Convert.ToHexString(body.AsSpan(28)).All(value => value == '0'),
            "样本 [28..36] 尾部 9 字节全 0：长度可按版本容错（参考实现 28+9，本客户端 37），不参与解析");
        That(AuctionRegisterRequest.Parse(body.AsSpan(0, AuctionRegisterRequest.BodySize).ToArray()).ItemTemplateId == 10400,
            "主体正好 28 字节时同样解析成功（尾部补 0 不是必需）");

        // 主体不足 28 字节：字段残缺，**必须拒绝**而不是按能读到的部分扣道具。
        for (int length = 0; length < AuctionRegisterRequest.BodySize; length++)
        {
            int captured = length;
            Reject<InvalidDataException>(() => AuctionRegisterRequest.Parse(body.AsSpan(0, captured).ToArray()),
                $"主体 {captured} 字节不足 28 字节即拒绝");
        }

        // 时长白名单 6/12/24/48：白名单内原样使用，白名单外回落 24 并标记 Fallback（由 Handler 记日志）。
        That(AuctionDurationResolution.Allowed.SequenceEqual(new[] { 6, 12, 24, 48 }), "上架时长白名单 = 6/12/24/48 小时");
        foreach (int hours in new[] { 6, 12, 24, 48 })
        {
            var kept = AuctionDurationResolution.Resolve(hours);
            That(kept.Requested == hours && kept.Applied == hours && !kept.Fallback, $"时长 {hours} 在白名单内，原样使用");
        }
        foreach (int hours in new[] { 0, 1, 5, 7, 23, 25, 47, 49, 72, -24 })
        {
            var fallback = AuctionDurationResolution.Resolve(hours);
            That(fallback.Requested == hours && fallback.Applied == AuctionDurationResolution.FallbackHours && fallback.Fallback,
                $"时长 {hours} 不在白名单，回落到 {AuctionDurationResolution.FallbackHours} 小时并标记回落");
        }
    }

    /// <summary>
    /// 业务校验：每条拒绝都必须**没有扣减计划**（`Options` 为空），且映射到白名单内的客户端码。
    /// </summary>
    static void Rejections()
    {
        using var f = new Fixture();

        // ① 槽位线索与实际内容不符 → 走「按 itemId 扫描」的回退路径，而不是按线索扣道具。
        var mismatched = f.Plan(body => body[2] = 99);
        var scanned = mismatched.Options;
        That(scanned is { FallbackReason: not null } && scanned.Source.Index == 27 && scanned.Source.List == InventoryListType.Main,
            "客户端槽位线索（99）与实际内容不符时回退到按 itemId 扫描，采信真实槽 27 并记录原因");
        That(scanned is not null && Convert.ToHexString(scanned.ItemEntry.AsSpan(2, 4)) == "A0280000",
            "挂牌条目的 itemId 取自背包实例快照（10400），不是请求体原样拷贝");

        // ② 有槽位线索指向**另一件**道具：同样不扣错误的那件。
        var wrongOccupant = f.Plan(body => body[2] = 28);
        That(wrongOccupant.Options is { FallbackReason: not null } other && other.ItemTemplateId == 10400 &&
            other.Source.Index == 27,
            "线索指向另一件装备所在槽（28）时不被采信，仍定位到 10400 的槽 27");
        // 线索合法时不做扫描：样本本身的槽位与内容一致。
        That(f.Plan(_ => { }).Options is { FallbackReason: null }, "槽位线索与内容一致时不走回退路径");

        // ③ 背包里根本没有这个模板 → 拒绝，且不产生任何变更计划。
        var missing = f.Plan(body => BitConverter.GetBytes(424242).CopyTo(body, 4));
        That(missing.Error == AuctionRegisterError.ItemNotFound && missing.Options is null,
            "背包里没有请求的模板时拒绝（绝不凭空扣一件）");

        // ④ 不可交易 / 已绑定 / 计时 / 锁定：都拒绝，且原因是 ItemNotTradable（→ 白名单通用码）。
        // 槽位按物品类别分配（装备 9..41、材料 121..153）：容纳类别由背包模块固定，自测不绕过该校验。
        var blocked = new (string Case, short Slot, int TemplateId, ItemCore Core, AuctionRegisterError Error)[]
        {
            ("TradeRestriction 非 0（绑定/交易限制）", 27, 10400, f.Core(10400) with { TradeRestriction = 1 }, AuctionRegisterError.ItemNotTradable),
            ("[sealing] 未解封", 29, SealedTemplate, f.Core(SealedTemplate), AuctionRegisterError.ItemNotTradable),
            ("[trade limit] 剩余次数为 0", 30, TradeLimitTemplate, f.Core(TradeLimitTemplate), AuctionRegisterError.ItemNotTradable),
            ("计时道具（ExpireTime > 0）", 27, 10400, f.Core(10400) with { ExpireTime = 1 }, AuctionRegisterError.ItemNotTradable),
            ("装备安全锁", 27, 10400, f.Core(10400) with { EquipmentLockId = 3 }, AuctionRegisterError.ItemNotTradable),
            ("排序锁", 27, 10400, f.Core(10400) with { SortLockFlag = 1 }, AuctionRegisterError.ItemNotTradable),
            ("账号绑定（[account]）", 28, 10015212, f.Core(10015212), AuctionRegisterError.ItemNotTradable),
            ("模板与实例种类不符", 27, 10400, f.Core(10400) with { Kind = ItemKind.Material }, AuctionRegisterError.ItemNotTradable),
            ("账号钱包货币不是可上架实体", 122, 3037, f.Core(3037), AuctionRegisterError.ItemNotTradable)
        };
        foreach (var (name, slot, templateId, core, expected) in blocked)
        {
            var decision = f.PlanWith(slot, templateId, core);
            That(decision.Error == expected && decision.Options is null,
                $"{name} 拒绝上架且不产生扣减计划");
            That(AuctionRegisterErrors.Whitelist.Contains(AuctionRegisterErrors.ToClientCode(expected)),
                $"{name} 映射到客户端白名单内的错误码");
        }
        // 边界：Attr 的高 3 位还有交易次数（1 次）时允许上架，用尽（0）才拒绝。
        That(f.PlanWith(27, 10400, f.Core(10400) with { Attr = 0x21 }).Options is not null,
            "[trade limit] 还剩 1 次交易次数时允许上架（按剩余次数判定，不是一概禁止）");
        That(f.PlanWith(27, 10400, f.Core(10400) with { ExpireTime = 0 }).Options is not null, "永久实例允许上架");

        // ⑤ 数量：非正、超过当前堆都拒绝；整件类忽略请求数量按 1 计。
        That(f.Plan(body => BitConverter.GetBytes(0).CopyTo(body, 8)).Error == AuctionRegisterError.InvalidRequest, "数量为 0 拒绝");
        That(f.Plan(body => BitConverter.GetBytes(-1).CopyTo(body, 8)).Error == AuctionRegisterError.InvalidRequest, "数量为负拒绝");
        That(f.Plan(body => BitConverter.GetBytes(2).CopyTo(body, 8)).Options is { ItemCount: 1 },
            "整件（不可堆叠）类忽略请求里的数量，按 1 件上架");
        That(f.PlanWith(121, 10014751, f.Core(10014751), count: 6).Error == AuctionRegisterError.InvalidRequest,
            "堆叠类请求数量超过当前堆时拒绝");
        That(f.PlanWith(121, 10014751, f.Core(10014751), count: 0).Error == AuctionRegisterError.InvalidRequest,
            "堆叠类数量非正拒绝");

        // ⑥ 价格：上限取 characters.auction_gold_limit（默认 4 亿），超限拒绝；起拍价不得高于一口价。
        That(f.Limit == 400_000_000, "价格上限取自角色的 auction_gold_limit（默认 4 亿）");
        That(f.Plan(body => BitConverter.GetBytes(400_000_001).CopyTo(body, 16)).Error == AuctionRegisterError.PriceOutOfRange,
            "一口价超上限拒绝");
        That(f.Plan(body => BitConverter.GetBytes(400_000_001).CopyTo(body, 12)).Error == AuctionRegisterError.PriceOutOfRange,
            "起拍价超上限拒绝");
        That(f.Plan(body => BitConverter.GetBytes(400_000_000).CopyTo(body, 16)).Options is { BuyoutPrice: 400_000_000 },
            "正好等于上限的一口价允许（边界是闭区间）");
        That(f.Plan(body => { BitConverter.GetBytes(20_000).CopyTo(body, 12); BitConverter.GetBytes(10_000).CopyTo(body, 16); })
                .Error == AuctionRegisterError.PriceOutOfRange,
            "起拍价高于一口价时拒绝");
        That(f.Plan(body => BitConverter.GetBytes(-2).CopyTo(body, 16)).Error == AuctionRegisterError.InvalidRequest,
            "一口价小于 -1 拒绝");
        That(f.Plan(body => { BitConverter.GetBytes(5_000).CopyTo(body, 12); BitConverter.GetBytes(-1).CopyTo(body, 16); })
                .Options is { StartingPrice: 5_000, BuyoutPrice: -1 },
            "一口价 -1 = 纯竞拍，起拍价照常保留（表的一口价允许 -1）");
        That(f.Plan(body => BitConverter.GetBytes(0).CopyTo(body, 16)).Options is { StartingPrice: 1, BuyoutPrice: 0 },
            "一口价为 0 且起拍价留空时归一为 1 金币起拍（表约束起拍价 ≥ 0）");

        // ⑦ 起拍价留空（-1）的归一：有一口价就用一口价，否则 1 金币（样本就是这一种）。
        That(f.Plan(_ => { }).Options is { StartingPrice: 17_000, BuyoutPrice: 17_000, ItemCount: 1, DurationHours: 24 },
            "起拍价留空 + 一口价 17000 时，起拍价归一为 17000（一口价即成交价）");

        // ⑧ 在售数量上限：达到上限后拒绝（用独立的夹具，避免污染上面的样本用例）。
        using var capped = new Fixture();
        That(capped.Listings.CountSellerListings(capped.Character.Id, capped.Now) == 0, "新角色在售挂牌数为 0");
        capped.SeedSelfListings(AuctionService.SellerListingCap);
        That(capped.Listings.CountSellerListings(capped.Character.Id, capped.Now) == AuctionService.SellerListingCap,
            $"在售挂牌数按卖家统计到上限 {AuctionService.SellerListingCap}");
        That(capped.PlanWith(27, 10400, capped.Core(10400)).Error == AuctionRegisterError.ListingLimit,
            "达到在售上限后拒绝上架（不产生扣减计划）");

        // 拒绝路径的共同性质：全部映射到白名单内的码，且成功（None）没有码可发。
        foreach (var error in Enum.GetValues<AuctionRegisterError>().Where(value => value != AuctionRegisterError.None))
            That(AuctionRegisterErrors.Whitelist.Contains(AuctionRegisterErrors.ToClientCode(error)),
                $"领域错误 {error} 映射到客户端白名单内的错误码");
        Reject<InvalidDataException>(() => AuctionRegisterErrors.ToClientCode(AuctionRegisterError.None),
            "成功没有错误码（调用方不能把 None 当失败发出去）");
    }

    /// <summary>
    /// 提交路径：**背包扣减与挂牌插入同事务**、拆堆上架、失败整体回滚，以及提交后的搜索可见性。
    /// </summary>
    static void Commit()
    {
        using var f = new Fixture();

        // ① 实机样本走完整链路：解析 → 规划 → 提交（同一事务）。
        var plan = f.PlanRaw().Options!;
        That(plan.Source == new InventorySlot(InventoryListType.Main, 27) && plan.ItemCount == 1,
            "样本定位到主背包槽 27、上架 1 件");
        That(plan.FallbackReason is null, "样本的槽位线索与背包内容一致，不走回退路径");
        var before = f.Character;
        var change = new InventoryResult(true, 0, plan.Inventory) { WholeListRefresh = true };
        That(change.WholeListRefresh && !new InventoryResult(true, 0, plan.Inventory).WholeListRefresh,
            "整列刷新只在显式置位时生效（差量仍是默认行为，不能顺手放大每一笔背包变化）");
        var committed = f.Register(plan);
        That(committed.Error == AuctionRegisterError.None && committed.Character is not null,
            "上架提交成功并返回提交后的角色快照");
        var after = committed.Character!;
        That(after.Revision == before.Revision + 1, "上架使角色 revision 递增一次（与背包扣减同一个提交）");
        That(after.Inventory.Find(new(InventoryListType.Main, 27)) is null, "源槽的道具在提交后从背包移除");
        That(f.ReadInventoryItemCount() == before.Inventory.Items.Length - 1, "独立读库确认源道具已删除（不是只改了内存快照）");
        That(f.QueryLong("SELECT count(*) FROM auction_listings WHERE seller_character_id=" + f.Character.Id) == 1,
            "独立读库确认挂牌已插入");
        That(f.QueryString("SELECT item_template_id||':'||item_count||':'||starting_price||':'||buyout_price||':'||state||':'||seller_name " +
                "FROM auction_listings") == $"10400:1:17000:17000:0:{f.Character.Name}",
            "挂牌字段：模板 10400、数量 1、起拍价归一为 17000、一口价 17000、state=0 在售、卖家名取角色名");
        That(f.QueryLong("SELECT expires_at_unix-posted_at_unix FROM auction_listings") == 24 * 3600,
            "到期时间 = 上架时间 + 24 小时（时长白名单内的值原样使用）");
        var entry = (byte[])f.Query("SELECT item_entry FROM auction_listings")!;
        That(entry.Length == 123 && BitConverter.ToInt32(entry, 2) == 10400,
            "挂牌保存 123B 条目快照，排列由物品模块的编码入口产生（itemId 落在 [2..5]）");

        // ② 上架成功后「排除自己」：自己搜不到，别人搜得到（本功能的验收点之一）。
        That(f.Search(AuctionCategoryFilter.All, excludeSeller: f.Character.Id).TotalCount == 0,
            "★ 上架成功后该商品不出现在自己的搜索结果里（官方排除自己的规则）");
        That(f.Search(AuctionCategoryFilter.All, excludeSeller: 999_999).TotalCount == 1,
            "同一件挂牌对其它角色可见（排除只按卖家角色 id）");
        That(f.Search(AuctionCategoryFilter.All, excludeSeller: 999_999).Listings.Single().ExpiresAtUnix ==
                f.Now + 24 * 3600,
            "搜索结果里的到期时间与挂牌一致（客户端据此显示剩余时间）");
        That(f.Service.AveragePrice(10400, f.Now).Count == 1, "该模板的在售均价把新挂牌计入（与搜索同源的在售口径）");
        That(f.QueryLong("SELECT count(*) FROM inventory_audit_log WHERE action_name='auction_register'") == 1,
            "上架在背包审计里留了一条 auction_register 记录（与扣减同事务）");

        // ③ 拆堆上架：只扣本次数量，余量留在原槽并保留原 UID。
        using var split = new Fixture();
        var stackDecision = split.PlanWith(121, 10014751, split.Core(10014751), count: 2);
        That(stackDecision.Options is { ItemCount: 2 } stack &&
            stack.Inventory.After.Find(new(InventoryListType.Main, 121)) is { } remaining &&
            remaining.Core.Value == 3 && remaining.Uid == split.Uid(121),
            "拆堆上架只扣 2 个，槽内余量 3 与 UID 保留（不整堆拿走、不重建实例）");
        var splitCommitted = split.Register(stackDecision.Options!);
        That(splitCommitted.Character!.Inventory.Find(new(InventoryListType.Main, 121)) is { Core.Value: 3 },
            "提交后背包余量 3（与规划一致）");
        That(split.QueryLong("SELECT item_count FROM auction_listings WHERE item_template_id=10014751") == 2,
            "挂牌只记本次上架的 2 个，不是原堆总数");
        That(split.QueryLong("SELECT count(*) FROM inventory_audit_log WHERE action_name='auction_register' AND count_delta=-2") == 1,
            "审计里的数量变化是 -2（本次实际扣减量）");

        // ④ 失败整体回滚：挂牌插入中途故障时，背包扣减、审计与 revision 一并回退，不留半成品。
        using var rollback = new Fixture();
        string snapshot = rollback.Snapshot();
        var rollbackPlan = rollback.PlanRaw().Options!;
        rollback.Execute("CREATE TRIGGER fail_listing BEFORE INSERT ON auction_listings BEGIN SELECT RAISE(ABORT,'listing failure'); END");
        Reject<SqliteException>(() => rollback.Register(rollbackPlan), "挂牌插入中途故障向上抛出（不发成功）");
        That(rollback.Snapshot() == snapshot,
            "★ 提交失败整体回滚：背包、审计、revision 与挂牌表都回到提交前（不留半成品）");
        That(rollback.ReadInventoryItemCount() == rollback.Character.Inventory.Items.Length &&
            rollback.Listings.CountListings() == 0 && rollback.Character.Revision == 0,
            "回滚后独立读库与内存快照一致：道具还在背包、挂牌表为空");
        rollback.Execute("DROP TRIGGER fail_listing");
        // 去掉故障后同一份计划可以重新提交（失败没有留下「已扣过」的残留状态）。
        var retried = rollback.Register(rollbackPlan);
        That(retried.Error == AuctionRegisterError.None && rollback.Listings.CountListings() == 1 && retried.Character!.Revision == 1,
            "故障排除后同一计划可重新提交，且只扣一次、只挂一条");

        // ⑤ 陈旧计划：提交前角色已被别的提交改动过，revision/背包快照对不上时必须拒绝。
        using var stale = new Fixture();
        var stalePlan = stale.PlanRaw().Options!;
        stale.Character = stale.Repository.CommitInventory(stale.Owner, stale.Character,
            new(stale.Character.Inventory, stale.Character.Inventory with { Gold = 7 }, "other_mutation"));
        string staleSnapshot = stale.Snapshot();
        Reject<InvalidOperationException>(() => stale.Register(stalePlan), "陈旧计划在写事务内被 revision/快照复核拒绝");
        That(stale.Snapshot() == staleSnapshot && stale.Listings.CountListings() == 0, "陈旧计划既没扣背包也没写挂牌");
    }

    /// <summary>183 应答与错误码白名单的字节层断言（本仓库帧层不补 flag，所以这里必须看到 <c>01 00</c>）。</summary>
    static void Wire()
    {
        Bytes(AuctionPackets.RegisterSuccess(), [0x01, 0x00],
            "★ 上架成功应答 = flag(01) + mode(00)：mode=0 是客户端清「上架进行中锁」0x20c 的唯一路径");
        Bytes(AuctionPackets.RegisterFailure(0xd2), [0x00, 0xd2, 0], "失败应答 = flag(00) + error:u16");
        Bytes(AuctionPackets.RegisterFailure(0x72), [0x00, 0x72, 0, 0], "114还必须保留后继读取的面板mode");
        That(AuctionPackets.RegisterSuccessFlag == 0x01 && AuctionPackets.RegisterFailureFlag == 0x00,
            "flag非0才是成功分支；失败公共前缀为三字节，114还读取面板字节");
        That(AuctionPackets.RegisterModeNormal == 0, "普通拍卖行上架的 mode = 0（type 7 浏览窗口）");

        That(AuctionRegisterErrors.Whitelist.SequenceEqual(new byte[] { 0xd2, 0x69, 0x72, 0x7e, 0xce, 0x7b, 0x89 }),
            "保留旧领域七码兼容白名单，不将它冒充客户端全部原生分支");
        foreach (byte code in AuctionRegisterErrors.Whitelist)
            That(AuctionPackets.RegisterFailure(code)[1] == code, $"白名单码 0x{code:X2} 可以下发");
        foreach (byte code in new byte[] { 0x00, 0x01, 0x7f, 0xd1, 0xd3, 0xff })
            Reject<InvalidDataException>(() => AuctionPackets.RegisterFailure(code), $"旧领域兼容入口仍拒绝未映射码0x{code:X2}");
        That(AuctionRegisterErrors.ToClientCode(AuctionRegisterError.ItemNotFound) == AuctionRegisterErrors.Silent,
            "「道具已经不在背包」映射到静默码（过期操作不弹框）");
        That(AuctionRegisterErrors.ToClientCode(AuctionRegisterError.ItemNotTradable) == AuctionRegisterErrors.Generic &&
            AuctionRegisterErrors.ToClientCode(AuctionRegisterError.PriceOutOfRange) == AuctionRegisterErrors.Generic &&
            AuctionRegisterErrors.ToClientCode(AuctionRegisterError.ListingLimit) == AuctionRegisterErrors.Generic &&
            AuctionRegisterErrors.ToClientCode(AuctionRegisterError.InvalidRequest) == AuctionRegisterErrors.Generic,
            "其余失败一律映射到通用提示码 0xd2（有提示、绝不假装成功）");
    }

    /// <summary>
    /// 一个真实文件库 + 一个真实角色所需的最小夹具。模板显式预置，因此不需要 PVF；
    /// 数据库是真的，所以 revision、审计与回滚都是真的。
    /// </summary>
    sealed class Fixture : IDisposable
    {
        readonly string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "S5A9-auction-reg-" + Guid.NewGuid().ToString("N"));

        public string Path { get; }
        public AccountOwners Owners { get; } = new();
        public AccountOwners.Lease Owner { get; }
        public CharacterRepository Repository { get; }
        public AuctionListingRepository Listings { get; }
        public AuctionService Service { get; }
        public Character Character { get; set; }
        public long Now { get; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        public int Limit => Character.Inventory.AuctionGoldLimit;

        public Fixture()
        {
            Directory.CreateDirectory(root);
            Path = System.IO.Path.Combine(root, "game.db");
            Owner = Owners.Acquire("auctioneer", () => { });
            Repository = new(Path);
            Listings = new(Path);
            // 装备样本 + 1 件账号绑定装备 + 1 堆材料 + 1 堆钱包货币：覆盖可上架、拆堆、绑定拒绝与虚拟资产拒绝。
            // ① UID 必须为 0：建角时插入的是**新实例**，非 0 的 UID 会被写成「转移已有实例」而拒绝（与邮箱夹具同一约定）。
            // ② 槽位必须落在各物品类别自己的范围内（装备 9 起、材料 121 起），否则角色创建时的背包校验会拒绝。
            // ③ 29/30 槽放 `[sealing]`（SealFlag=0）与 `[trade limit]`（Attr=0）实例：这两类准入由**模板的
            //    AttachType** 决定，因此必须让它成为背包里真实存在的那件道具，而不是把请求的模板 id 换成它。
            var inventory = new InventorySnapshot([
                new(0, new(InventoryListType.Main, 27), new ItemCore(SampleTemplate, ItemKind.Equipment, 999_999_998) { Attr = 2, Durability = 45 }),
                new(0, new(InventoryListType.Main, 28), new ItemCore(10015212, ItemKind.Equipment, 999_999_998)),
                new(0, new(InventoryListType.Main, 29), new ItemCore(SealedTemplate, ItemKind.Equipment, 999_999_998) { SealFlag = 0 }),
                new(0, new(InventoryListType.Main, 30), new ItemCore(TradeLimitTemplate, ItemKind.Equipment, 999_999_998) { Attr = 0 }),
                new(0, new(InventoryListType.Main, 121), new ItemCore(StackTemplate, ItemKind.Material, 5)),
                new(0, new(InventoryListType.Main, 122), new ItemCore(3037, ItemKind.Material, 9))
            ]);
            Character = Repository.Create(Owner, new("Auctioneer", new(38, 1, 450, 234, 0), null) { Level = 95, Inventory = inventory with { Gold = 1_000_000 } });
            var catalog = new ItemCatalog(null!, null!, null!, 0);
            catalog.PrefillTemplates(Templates);
            var categories = new AuctionCategoryResolver(catalog);
            categories.PrefillSlots(new Dictionary<int, int> { [SampleTemplate] = 15, [StackTemplate] = 0 });
            // 存储合同由角色仓储实现（上架要与背包扣减同事务），与 Runtime 的装配一致。
            Service = new AuctionService(Repository, categories, catalog,
                // 碎片编号刻意与夹具模板错开：模板不得被当成史诗碎片而拒绝。
                new EpicPieceCatalog("[piece list]\n60000 70001\n[/piece list]"), AuctionPackets.ItemEntrySnapshot, AuctionPackets.ItemCoreFromEntry);
        }

        /// <summary>
        /// 预置模板：字段取值即本组断言的依据，与 PVF 无关。
        /// `[free]` = 可自由交易；`[sealing]` 未解封拒绝；`[trade limit]` 按剩余交易次数；
        /// `[account]` 账号绑定一律拒绝（拍卖行必然跨账号）。
        /// </summary>
        static readonly Dictionary<int, ItemTemplate> Templates = new()
        {
            [SampleTemplate] = new(SampleTemplate, ItemKind.Equipment, 15, 1, 45, 1, 1, 0, 1, "[free]", 65535, 0, "残缺的牛皮背心"),
            [StackTemplate] = new(StackTemplate, ItemKind.Material, 0, 1, 0, 999, 1, 0, 1, "[free]", 65535, 0, "粗布腰带随机礼盒"),
            [SealedTemplate] = new(SealedTemplate, ItemKind.Equipment, 15, 1, 45, 1, 1, 0, 1, "[sealing]", 65535, 0, "封装胸甲"),
            [TradeLimitTemplate] = new(TradeLimitTemplate, ItemKind.Equipment, 15, 1, 45, 1, 1, 0, 1, "[trade limit]", 65535, 0, "限次交易胸甲"),
            [10015212] = new(10015212, ItemKind.Equipment, 15, 1, 45, 1, 1, 0, 1, "[account]", 65535, 0, "账号绑定胸甲"),
            [3037] = new(3037, ItemKind.Material, 0, 0, 0, 999, 1, 0, 1, "[free]", 65535, 0, "无色小晶块")
        };

        /// <summary>夹具里某槽当前实例的核心（改字段前先取原值，避免手抄错）。</summary>
        public ItemCore Core(int templateId)
        {
            foreach (var item in Character.Inventory.Items)
            {
                if (item.Core.ItemId == templateId)
                    return item.Core;
            }
            throw new InvalidOperationException("夹具里没有模板 " + templateId);
        }

        public long Uid(short slot) => Character.Inventory.Find(new(InventoryListType.Main, slot))!.Uid;

        /// <summary>把指定槽的实例核心换成给定取值后规划：用于不可交易/锁定/计时/种类不符等拒绝用例。</summary>
        public AuctionRegisterDecision PlanWith(short slot, int templateId, ItemCore core, int count = 1)
        {
            var body = Convert.FromHexString(Sample);
            BitConverter.GetBytes(templateId).CopyTo(body, 4);
            BitConverter.GetBytes(count).CopyTo(body, 8);
            body[2] = (byte)slot;
            body[3] = (byte)(slot >> 8);
            var request = AuctionRegisterRequest.Parse(body);
            var inventory = Character.Inventory;
            var existing = inventory.Find(new(InventoryListType.Main, slot));
            if (existing is not null)
                inventory = inventory with { Items = inventory.Items.Replace(existing, existing with { Core = core }) };
            return Service.PlanRegister(Character with { Inventory = inventory }, request, Now);
        }

        /// <summary>用实机样本（可选改动若干字节）规划。</summary>
        public AuctionRegisterDecision Plan(Action<byte[]> mutate)
        {
            var body = Convert.FromHexString(Sample);
            mutate(body);
            return Service.PlanRegister(Character, AuctionRegisterRequest.Parse(body), Now);
        }

        /// <summary>用实机样本走一次完整规划（夹具里的样本一定合法，`Options` 非 null）。</summary>
        public AuctionRegisterDecision PlanRaw() => Plan(_ => { });

        public AuctionListingRegisterResult Register(AuctionRegisterPlan plan)
        {
            // 走存储合同的写路径（与 Handler 同一条）：背包扣减 + 挂牌插入同事务。
            var result = Repository.RegisterListing(Owner, Character, plan);
            if (result.Character is not null)
                Character = result.Character;
            return result;
        }

        public AuctionSearchPage Search(int categoryId, int? excludeSeller) =>
            Service.Search(new(0, null, categoryId), excludeSeller, Now);

        /// <summary>给当前角色堆够在售挂牌（用于在售上限用例）：走真实批量插入路径。</summary>
        public void SeedSelfListings(int count)
        {
            var drafts = ImmutableArray.CreateBuilder<AuctionListingDraft>(count);
            for (int index = 0; index < count; index++)
            {
                drafts.Add(new(Character.Id, Character.Name, SampleTemplate, (byte)ItemKind.Equipment, 1,
                    AuctionPackets.ItemEntrySnapshot(Core(SampleTemplate)), 1_000, 2_000, Now, Now + 86_400));
            }
            Listings.InsertListings(drafts.ToImmutable());
        }

        public void Execute(string sql)
        {
            using var db = Open();
            using var command = db.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        public object? Query(string sql)
        {
            using var db = Open();
            using var command = db.CreateCommand();
            command.CommandText = sql;
            return command.ExecuteScalar();
        }

        public long QueryLong(string sql) => Convert.ToInt64(Query(sql));
        public string QueryString(string sql) => Convert.ToString(Query(sql)) ?? "";
        public long ReadInventoryItemCount() => QueryLong("SELECT count(*) FROM character_inventory_items");

        /// <summary>整库快照（表 + 行）：回滚断言必须看到「背包、审计、revision、挂牌」都回退，而不是只看一处。</summary>
        public string Snapshot()
        {
            using var db = Open();
            using var command = db.CreateCommand();
            var tables = new List<string>();
            command.CommandText = "SELECT name FROM sqlite_schema WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                    tables.Add(reader.GetString(0));
            }
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

        SqliteConnection Open() => Open(Path);

        static SqliteConnection Open(string path)
        {
            var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, ForeignKeys = true }.ToString());
            db.Open();
            return db;
        }

        public void Dispose()
        {
            Owner.Dispose();
            Directory.Delete(root, true);
        }
    }
}
