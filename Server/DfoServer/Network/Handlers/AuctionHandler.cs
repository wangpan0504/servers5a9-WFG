using DfoServer.Game.Auction;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;
using System.Globalization;

namespace DfoServer.Network.Handlers;

/// <summary>
/// 拍卖行搜索（CMD 186 按道具搜索 / CMD 187 分类搜索）、均价查询（CMD 182）、上架（CMD 183）、
/// 取消上架（CMD 184）与「我的拍卖品」三个页签（CMD 188 我的上架 / 189 我的竞价 / 190 拍卖历史）。
///
/// 请求体偏移已在匹配客户端确认：
///   187（20B）：<c>00 | 00000000 | E12E | 07 | 001F | 00×9 | 08</c>
///     ⇒ [0]=mode、[1..4]=翻页记录偏移、[5..6]=分类 id、[7]=品质（默认 7=不限）。
///   186（26B）：<c>00 | 00000000 | 001F | 01 | B79C | DD0B0000 | 00×11 | 08</c>
///     ⇒ [8..9]=分类 id、[10..13]=itemId。
///   182（13B，D95 抓包）：<c>00 | F2550000 | 00×8</c> ⇒ [1..4]=itemId（小端 i32），其余字节忽略但记日志。
///     参考实现的同一命令是 8 字节（<c>00 | itemId | 00×3</c>），因此按 [1..4] 取 itemId 对两个版本都成立，
///     长度只做「至少 5 字节」的下限校验，不做等长断言。
///   183（37B，D95 抓包）：见 <see cref="AuctionRegisterRequest"/> 的偏移表；
///     主体 28B + 尾部补 0，主体不足 28B **拒绝**（不按残缺字段扣道具）。
/// 分类过滤覆盖**顶层大类与已对账的子叶**（首饰 12001/12002/12003、消耗品 13001–13006、
/// 宠物装备 14001–14003、副职业 33001–33004、武器 8 族 32 叶、防具 5 材质 × 5 部位）；
/// 子叶维度未对账的大类（特殊装备/设计图）按所属顶层大类过滤，完全没见过的 id 才不过滤。
/// 规则与依据在 <see cref="AuctionCategoryFilter"/>。
/// 每次搜索把收到的分类 id、解析状态、实际下推的维度与取舍说明记入 <c>auction_search_category</c> 日志，
/// 与 <c>auction_search_result</c> 的总数一起作为继续确认分类表的证据。
///
/// 应答一律走 CMD 路径、opcode 186：187 请求也用 186 应答（客户端没有为 187 注册回调的实机结论）。
/// 182 的应答是**双应答**（先 mode=0 播种价格、再 mode=1 开窗，顺序不能颠倒），见
/// <see cref="AuctionAveragePriceReply"/>；均价取自该模板当前在售挂牌，不编造定价策略。
/// 默认（探针关闭）走真实挂牌数据；<c>--auction-probe</c> 的 **搜索候选、均价候选与「我的拍卖品」候选各有独立下标**
/// （「我的拍卖品」再按 188/189/190 各一个），因此「每拖一次物品换一个均价候选」「每点一个页签换一种记录布局」
/// 都不会改变搜索返回的内容。搜索应答的 <c>mode</c> 字节也可由 <c>bidmode=0,1</c> 轮换：它只换窗口类型
/// （客户端按应答 mode 分流到 GetWindow(6)/(7) 两套填充），**记录内容完全相同**，用于验证窗口 6/7 分流
/// （已实机闭环）；它**不影响竞拍入口** —— 本版 D95 客户端没有竞拍按钮，入口判据不存在。
/// 其余拍卖行请求（传奇拍卖 637、罗杰·莱文拍卖 993/994）未注册，保持未处理，不返回通用成功。
/// **一口价购买（CMD 185）已接入**：请求布局由两个实机样本差分定案（30 字节，见
/// <see cref="AuctionBuyRequest"/>），业务规则在 <see cref="AuctionService.PlanBuy"/>，
/// 事务在 <see cref="AuctionService.Buy"/>（跨账号：只取买方一把租约，卖方所得走托管邮件，
/// 方案见 Docs/规范/并发事务与响应延迟.md 的「跨账号转移：拍卖行一口价购买」一节）。
/// **CMD 334 仍是请求抓取桩**（布局未定，只记日志、不回包、不改状态）。
///
/// 183 上架是本 Handler 里唯一**会改背包资产**的路径，三步顺序不能变：
///   ① 只读规划（<see cref="AuctionService.PlanRegister"/>，纯计算，不写库）；
///   ② 取 owner 后**背包扣减 + 挂牌插入同事务**提交，并安装提交后的在线快照
///      （<see cref="Game.Session.GameSession.RegisterListing"/>，事务仍由 <see cref="AuctionService.Register"/> 拥有），
///      失败抛异常不发成功；
///   ③ 先回 183 应答（`flag + mode=0`，清客户端上架锁），再用**现有背包投影**补发整列刷新 `0x000D`。
/// 第 ③ 步的整列刷新不是可选项：参考实现 round13 实测「只刷源槽」会让客户端把源槽一直当成
/// 被上架窗口持有（背包不可操作、无法再次上架），只有整列重建才能解除残留的槽位锁。
/// ★ 第 ③ 步的请求体由第 ② 步**一起产出**（含提交回执）：投影只发布「已安装快照」产出的回执，
/// 上架不走常规背包写用例，所以回执必须由会话在安装快照时给出，Handler 不自己拼一个没有回执的结果
/// （那样会在投影里抛「背包刷新缺少提交回执」并把连接带断）。
/// </summary>
internal sealed class AuctionHandler(ItemCatalog items, AuctionService auctions, InventoryProjection inventory,
    Func<AuctionProbeSettings> nextSearch, Func<AuctionProbeSettings> nextAverage,
    Func<AuctionMyListPanel, AuctionProbeSettings> nextMyList, ServerLog log) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands)
    {
        commands.Register(CmdPacketType.AUCTION_SEARCH_BY_ITEMKEY, SearchByItemKey, nameof(AuctionHandler));
        commands.Register(CmdPacketType.AUCTION_SEARCH_BY_NOITEMKEY, SearchByNoItemKey, nameof(AuctionHandler));
        commands.Register(CmdPacketType.AUCTION_ASK_AVERAGE_PRICE, AveragePrice, nameof(AuctionHandler));
        commands.Register(CmdPacketType.AUCTION_REGIST_ITEM, RegisterItem, nameof(AuctionHandler));
        commands.Register(CmdPacketType.AUCTION_REGIST_CANCEL, CancelRegist, nameof(AuctionHandler));
        commands.Register(CmdPacketType.AUCTION_MY_REGISTED_ITEM_INFO, MyRegistedItems, nameof(AuctionHandler));
        commands.Register(CmdPacketType.AUCTION_MY_BIDDING_INFO, MyBidding, nameof(AuctionHandler));
        commands.Register(CmdPacketType.AUCTION_MY_AUCTION_HISTORY, MyAuctionHistory, nameof(AuctionHandler));
        // 一口价购买：185 是本仓库唯一的跨账号钱货转移路径（买方扣款 + 挂牌成交 + 两封托管邮件同事务）；
        // 334 的请求布局仍只有参考实现（前导与挂牌 id 偏移自标未定），保持「只抓请求体」的桩。
        commands.Register(CmdPacketType.AUCTION_BIDDING, Bidding, nameof(AuctionHandler));
        commands.Register(CmdPacketType.AUCTION_BUY_ITEM_APIECE, BuyItemApieceCapture, nameof(AuctionHandler));
    }

    Task<CommandResult> SearchByItemKey(GameConnectionContext context, Packet packet) => SearchAsync(context, packet, byItemKey: true);

    Task<CommandResult> SearchByNoItemKey(GameConnectionContext context, Packet packet) => SearchAsync(context, packet, byItemKey: false);

    async Task<CommandResult> SearchAsync(GameConnectionContext context, Packet packet, bool byItemKey)
    {
        byte[] body = packet.Body;
        int mode = body.Length >= 1 ? body[0] : 0;
        int offset = body.Length >= 5 ? BitConverter.ToInt32(body, 1) : 0;
        int categoryOffset = byItemKey ? 8 : 5;
        int categoryId = body.Length >= categoryOffset + 2 ? body[categoryOffset] | (body[categoryOffset + 1] << 8) : -1;
        int itemId = byItemKey && body.Length >= 14 ? BitConverter.ToInt32(body, 10) : 0;
        // mode=0 普通拍卖，mode=1 金币寄售。其它值没有实机样本，仍只回空列表，避免把装备灌进未知窗口。
        // 金币搜索不消耗搜索探针：记录布局与普通搜索相同（2026-10-12 实机把普通记录画进了寄售面板），
        // 轮换实验布局不该推进到这个窗口。
        if (mode == AuctionMyListReply.ConsignmentWindowMode)
        {
            log.Write("auction_search_consignment", new
            {
                ByItemKey = byItemKey,
                Connection = context.Id,
                Length = body.Length,
                Body = Convert.ToHexString(body),
                Mode = mode,
                CategoryId = categoryId,
                ItemId = itemId,
                Note = "金币寄售窗口：只检索金币券挂牌，不返回普通拍卖装备"
            });
            return await ListingsAsync(context, mode, (byte)mode, offset, categoryId, itemId, byItemKey,
                AuctionProbeSettings.Disabled, goldWindow: true, "auction_search_consignment_reply");
        }
        if (mode != AuctionMyListReply.NormalWindowMode)
        {
            log.Write("auction_search_consignment", new
            {
                ByItemKey = byItemKey,
                Connection = context.Id,
                Length = body.Length,
                Body = Convert.ToHexString(body),
                Mode = mode,
                CategoryId = categoryId,
                ItemId = itemId,
                Note = "未知拍卖窗口：只回空列表"
            });
            await context.Reply(CmdPacketType.AUCTION_SEARCH_BY_ITEMKEY,
                AuctionPackets.SearchReply((byte)mode, 0, [], AuctionSearchRecordProbe.Default,
                    DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
                "auction_search_consignment_reply");
            return CommandResult.Handled;
        }
        var probe = nextSearch();
        // 探针候选的应答 opcode：186 请求固定用 186 应答，187 请求按候选选择（与探测阶段的既有行为一致）。
        var probeReply = byItemKey || probe.ReplyOpcode == (int)CmdPacketType.AUCTION_SEARCH_BY_ITEMKEY
            ? CmdPacketType.AUCTION_SEARCH_BY_ITEMKEY
            : CmdPacketType.AUCTION_SEARCH_BY_NOITEMKEY;
        // 应答的 mode 字节：默认回显请求首字节；`--auction-probe bidmode=0,1` 时按轮换覆盖
        // （客户端按应答 mode 分流到 GetWindow(6)/(7) 两套填充，用于验证窗口 6/7 分流；与竞拍入口无关）。
        byte replyMode = probe.ReplyMode((byte)mode);
        log.Write("auction_search", new
        {
            ByItemKey = byItemKey,
            Length = body.Length,
            Body = Convert.ToHexString(body),
            Mode = mode,
            ReplyMode = replyMode,
            ModeProbe = probe.SearchMode,
            Offset = offset,
            CategoryId = categoryId,
            ItemId = itemId,
            Source = probe.Enabled ? "probe" : "listings",
            Probe = probe.Enabled,
            Preset = probe.Enabled ? probe.PresetIndex : -1,
            Layout = probe.Enabled ? probe.Layout.Name : null,
            ProbeItem = probe.Enabled ? probe.ItemTemplateId : 0,
            ReplyOpcode = probe.Enabled ? (int)probeReply : 0,
            Kind = probe.Enabled ? (probe.Notify ? "noti" : "cmd") : null,
            // ⚠ 这两条是**探针合成行**的条数，探针关闭时恒为 0，**不是**本次搜索的结果：
            // 真实搜索的匹配总数与本批条数在同一次请求的第二条日志 auction_search_result 里
            // （`Source=listings` 的那条，含每行挂牌）。字段名带 Probe 前缀就是为了让「0」不再被读成
            // 「搜索被过滤成 0 条」——2026-10-11 22:1x 那次分类搜索「全 0」的误判，正是把这里的 0
            // 当成了搜索结果；真实结果是 cat=10000 → 4 条、cat=10102 → 1 条（同一次请求的 result 行可证）。
            ProbeTotal = probe.Enabled ? probe.Total : 0,
            ProbeRows = probe.Enabled ? probe.Rows : 0
        });
        if (probe.Enabled)
            return await ProbeAsync(context, replyMode, probe, probeReply);
        return await ListingsAsync(context, mode, replyMode, offset, categoryId, itemId, byItemKey, probe, goldWindow: false,
            "auction_search_reply");
    }
    /// <summary>真实挂牌读路径：186/187 都按 186 应答，返回分类过滤后的匹配总数与本页（10 条）记录。</summary>
    async Task<CommandResult> ListingsAsync(GameConnectionContext context, int mode, byte replyMode, int offset, int categoryId,
        int itemId, bool byItemKey, AuctionProbeSettings probe, bool goldWindow, string replyAction)
    {
        var character = context.Session.Selected;
        if (character is null)
        {
            // 没有选中角色就无法按官方规则排除自己上架的挂牌：保持未处理，不返回可能违反规则的空列表。
            log.Write("auction_search_no_character", new { Connection = context.Id });
            return CommandResult.Unhandled;
        }
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        // 分类搜索不带 itemId；按道具搜索的 itemId 为 0 时同样不过滤，避免用无效模板过滤掉全部挂牌。
        int? itemTemplateId = byItemKey && itemId > 0 ? itemId : null;
        var page = auctions.Search(new(offset, itemTemplateId, categoryId), character.Id, now, goldWindow);
        // Search 一定带回分类过滤说明（仓储不解释分类，由读用例补上），日志与过滤因此共用同一份解析结果：
        // 分类名、是否命中顶层大类、实际下推的维度都来自这里，
        // 「客户端点了哪个节点 / 服务端到底过滤了什么」可以直接对照。
        var plan = page.Plan!;
        var category = plan.Category;
        log.Write("auction_search_category", new
        {
            CategoryId = categoryId,
            Hex = "0x" + categoryId.ToString("X4", CultureInfo.InvariantCulture),
            Status = category.Status.ToString(),
            MappedId = category.MappedId,
            Name = category.Name,
            KindFilter = plan.Kinds == 0 ? null : plan.Filter.Kinds.ToArray(),
            SlotFilter = plan.SlotDimensions is { } slots ? AuctionCategoryDimensions.ToList(slots.Slots).ToArray() : null,
            // 取舍说明来自解析结果本身（子叶证据 / 未对账原因），日志与映射表共用一份措辞：
            // 「客户端点了哪个节点、服务端为什么这样过滤」可以直接对照。
            Note = category.Note
        });
        log.Write("auction_search_result", new
        {
            Source = "listings",
            Mode = mode,
            // 本次**实际写进应答**的 mode（默认 = Mode；`bidmode=` 轮换时两者不同），
            // 「竞拍入口出现在哪一次搜索」可以直接与它对照。
            ReplyMode = replyMode,
            ModeProbe = probe.SearchMode,
            Offset = offset,
            CategoryId = categoryId,
            ItemTemplateId = itemTemplateId ?? 0,
            ExcludeSeller = character.Id,
            Total = page.TotalCount,
            Rows = page.Listings.Length,
            // 搜索记录**逐字段**探针：实机每搜一次换一个候选（每个候选只改记录里的一个字段），
            // 界面上「竞拍按钮」是否出现就是判据；contract = 一个字段都不改（逐字节等于合同编码）。
            RecordProbe = probe.SearchRecord.Name,
            RecordField = probe.SearchRecord.Field.ToString(),
            RecordNote = probe.SearchRecord.Note,
            Listings = page.Listings.Select(listing => new { listing.ListingId, listing.SellerName, listing.ItemTemplateId,
                listing.ItemCount, Price = listing.DisplayPrice, listing.BuyoutPrice, listing.ExpiresAtUnix }).ToArray()
        });
        await context.Reply(CmdPacketType.AUCTION_SEARCH_BY_ITEMKEY,
            AuctionPackets.SearchReply(replyMode, page.TotalCount, page.Listings, probe.SearchRecord, now), replyAction);
        return CommandResult.Handled;
    }

    /// <summary>
    /// 开发诊断路径（<c>--auction-probe</c>，默认关闭）：按候选轮换回包，用于在实机上收敛尚未确认的布局。
    /// 布局已定案，因此这里只在显式开启时使用，不再是默认行为。
    /// 请求里的 mode 与本次实际应答的 mode 都由调用方的 <c>auction_search</c> 日志记录，这里不再重复收参。
    /// </summary>
    async Task<CommandResult> ProbeAsync(GameConnectionContext context, byte replyMode, AuctionProbeSettings probe,
        CmdPacketType probeReply)
    {
        IReadOnlyList<AuctionReplyRow> rows;
        try
        {
            // 哨兵候选只回空列表头部，用于单独确认应答通道；其余候选回真实条目构造的探针行。
            rows = probe.Layout.RecordSize == 0 ? [] : BuildProbeRows(probe);
        }
        catch (Exception error) when (error is InvalidDataException or KeyNotFoundException or ArgumentException)
        {
            // 探针模板不可用：明确失败，不伪装成空搜索结果。
            log.Write("auction_probe_failed", new { error.Message, probe.ItemTemplateId });
            return CommandResult.Unhandled;
        }
        byte[] payload = AuctionPackets.SearchReply(replyMode, probe.Total, rows, probe.Layout, probe.Padding);
        if (probe.Notify)
            await context.Notify((NotiPacketType)probe.ReplyOpcode, payload, "auction_search_reply");
        else
            await context.Reply(probeReply, payload, "auction_search_reply");
        return CommandResult.Handled;
    }

    /// <summary>
    /// 探针行：用真实模板造真实条目字节，因此客户端能否渲染出这件道具，
    /// 直接反映「记录尺寸 / 道具块偏移 / 反排」这一组候选是否正确。
    /// </summary>
    IReadOnlyList<AuctionReplyRow> BuildProbeRows(AuctionProbeSettings probe)
    {
        if (probe.Rows <= 0)
            return [];
        var template = items.Get(probe.ItemTemplateId);
        var core = InventoryRewardGrantService.Create(template, probe.StackCount);
        byte[] block = AuctionPackets.ItemBlock(AuctionPackets.ItemEntrySnapshot(core), (int)core.Kind, probe.Layout);
        long expires = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 86_400;
        var rows = new AuctionReplyRow[probe.Rows];
        for (int index = 0; index < rows.Length; index++)
        {
            rows[index] = new AuctionReplyRow(
                ListingId: index + 1,
                ExpiresAtUnix: (int)expires,
                CurrentPrice: probe.CurrentPrice + index * 100,
                BuyoutTotal: probe.BuyoutTotal,
                ItemBlock: block,
                RemainSeconds: 86_400);
        }
        return rows;
    }

    /// <summary>
    /// CMD 182 均价查询（客户端把物品拖到上架框时发出）。
    ///
    /// **为什么必须应答**：客户端在等这个包来填「当前均价」并解锁竞拍价/一口价输入框；
    /// 不应答会让界面永远停在「正在统计拍卖行价格…」，无法上架。
    ///
    /// 请求体按 **[1..4] = itemId（小端 i32）** 解析并**容忍长度差异**：D95 实测 13 字节、参考实现 8 字节，
    /// 两者 [1..4] 都是 itemId，所以只校验「至少 5 字节」，其余字节忽略但记进日志——
    /// 长度不同说明客户端版本有差异，日志是唯一能事后分辨证据。
    /// 长度不足 5 字节时**明确失败**（<see cref="CommandResult.Unhandled"/>），不返回通用成功：
    /// 拿不到 itemId 就无法给均价，返回 0 会让客户端把「无均价」当成统计结果。
    /// 未选中角色同样是 <see cref="CommandResult.Unhandled"/>（与搜索路径一致，不假装成功）。
    ///
    /// 应答是**双应答**：先 <c>mode=0</c>（参考实现里唯一把价格写进客户端全局价格表、置「价格就绪」标志的分支），
    /// 再 <c>mode=1</c>（只填物品名并启用控件）。顺序不能颠倒，否则价格进不了价格表、输入框依旧 disabled。
    /// 数据体的**结构**（含「CMD 应答体是否自带 1 字节 flag」）与顺序都来自另一个客户端版本、
    /// 在本客户端未验证，因此逐项做成候选、由 <c>--auction-probe avg=</c> 轮换；首选候选 `flag58` 见
    /// <see cref="AuctionAveragePriceReply.Flag58"/>：CMD 应答体首字节必须是 flag，传输层不补这一字节。
    /// </summary>
    async Task<CommandResult> AveragePrice(GameConnectionContext context, Packet packet)
    {
        byte[] body = packet.Body;
        // 至少要有前导字节 + 4 字节 itemId：不足就是无法解析的请求，明确不处理。
        if (body.Length < 5)
        {
            log.Write("auction_average_price_rejected", new { Reason = "请求体不足 5 字节", Length = body.Length, Body = Convert.ToHexString(body) });
            return CommandResult.Unhandled;
        }
        int itemId = BitConverter.ToInt32(body, 1);
        // 均价候选有自己的轮换下标：每拖一次物品前进一个均价候选，不会顺带改掉搜索回什么。
        var probe = nextAverage();
        var candidate = probe.Enabled ? probe.Average : AuctionAveragePriceReply.Default;
        log.Write("auction_average_price", new
        {
            Length = body.Length,
            Body = Convert.ToHexString(body),
            ItemId = itemId,
            Leading = body[0],
            // 参考实现是 8 字节（[5..7] 三个 0），D95 是 13 字节（[5..12] 八个 0）：把尾部字节原样记下来，
            // 两个版本的差异只体现在这里，不参与解析。
            Tail = Convert.ToHexString(body.AsSpan(5)),
            Source = probe.Enabled ? "probe" : "listings",
            Candidate = candidate.Name
        });
        if (itemId <= 0)
        {
            log.Write("auction_average_price_rejected", new { Reason = "itemId 非正", ItemId = itemId });
            return CommandResult.Unhandled;
        }
        var character = context.Session.Selected;
        if (character is null)
        {
            // 没有选中角色时与搜索路径同款处理：保持未处理，不返回可能是伪造数据的均价。
            log.Write("auction_average_price_no_character", new { Connection = context.Id, ItemId = itemId });
            return CommandResult.Unhandled;
        }
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        // key 的取值：有在售取均价、无在售取该模板的商店售价、都取不到才 1。
        // 回 0 会让客户端判成「没有价格数据」（0x1455900 判 [+0x1a4] != 0）并卡在「正在统计拍卖行价格……」，
        // 同时预填 0 的一口价必然低于商店售价 ⇒ 上架被客户端直接拒绝。
        var quote = auctions.ResolveAverageKey(itemId, now);
        // 安全底线第二道（第一道在候选表构造与编码器里）：任何能崩客户端的数据体都不下发。
        // 表里已不存在 <8B 的候选，这里是「以后有人加候选时也发不出去」的保险。
        if (!candidate.IsSafe)
        {
            log.Write("auction_average_price_unsafe_skipped", new
            {
                Candidate = candidate.Name,
                Shape = candidate.Shape,
                BodyLength = candidate.BodyLength,
                Minimum = AuctionAveragePriceReply.MinimumBodyBytes
            });
            return CommandResult.Unhandled;
        }
        // 先编码再记录：日志里的体长与十六进制就是**真正要下发的字节**，客户端反馈可以直接对照。
        byte[] first = AuctionPackets.AveragePriceReply(candidate.FirstMode, quote.Key, candidate);
        byte[]? second = candidate.SecondMode == AuctionAveragePriceReply.NoSecondMode
            ? null
            : AuctionPackets.AveragePriceReply(candidate.SecondMode, quote.Key, candidate);
        log.Write("auction_average_price_result", new
        {
            ItemId = itemId,
            // Average/Count = 在售统计；Floor = 商店售价下限；Key = 实际下发的参考价；Source 说明它来自哪一档。
            Average = quote.Average,
            Count = quote.Count,
            Floor = quote.Floor,
            Key = quote.Key,
            Source = quote.Source.ToString(),
            KeyNote = quote.Note,
            Candidate = candidate.Name,
            Shape = candidate.Shape,
            BodyLength = first.Length,
            Modes = second is null ? new[] { candidate.FirstMode } : new[] { candidate.FirstMode, candidate.SecondMode },
            // 结构化摘要：本次到底按什么结构发、发几条、发不发 795 —— 与客户端随后的反馈一一对应。
            Sends = (second is null ? 1 : 2) + (candidate.SendListRefresh ? 1 : 0),
            ListRefresh = candidate.SendListRefresh,
            VectorLength = candidate.VectorLength,
            CountEntries = candidate.CountEntries,
            TailBytes = candidate.TailBytes,
            ZeroBytes = candidate.ZeroBytes,
            Note = candidate.Note
        });
        if (candidate.ReplyOpcode == AuctionAveragePriceReply.NoReplyOpcode)
        {
            // 哨兵候选：故意什么都不发，用于确认「客户端是否真的在等 182 的应答」。
            log.Write("auction_average_price_reply", new { Candidate = candidate.Name, Skipped = "哨兵候选不发应答" });
            return CommandResult.Handled;
        }
        await SendAveragePriceAsync(context, candidate, candidate.FirstMode, first);
        if (second is not null)
            await SendAveragePriceAsync(context, candidate, candidate.SecondMode, second);
        if (candidate.SendListRefresh)
        {
            // 均价列表刷新（参考实现 §4.3，D95 未验证）：只在候选显式要求时补发。
            await context.Reply((CmdPacketType)AuctionAveragePriceReply.ListRefreshOpcode,
                AuctionPackets.AveragePriceListRefresh(), "auction_average_price_list");
        }
        return CommandResult.Handled;
    }

    /// <summary>
    /// 发一条均价应答。数据体**已包含 CMD 路径要求的 flag**：带 flag 的候选由编码器写在体首，
    /// 帧头只有 kind/opcode/长度/CRC，不承担它（见 AuctionAveragePriceReply.BodyFlag）。
    /// 应答 opcode 取候选值：182 是命令表里已注册的那个；其它值（如 183）是**候选探针**——
    /// 用来分辨客户端是否把回调注册在别的 opcode 上，因此按候选原样发出，不在这里改写。
    /// 日志逐条记录真正下发的字节（长度 + 十六进制），D95 的 217 反馈可以直接与它对照。
    /// </summary>
    async Task SendAveragePriceAsync(GameConnectionContext context, AuctionAveragePriceReply candidate, int mode, byte[] body)
    {
        log.Write("auction_average_price_reply", new
        {
            Mode = mode,
            candidate.Name,
            candidate.Shape,
            candidate.ReplyOpcode,
            Length = body.Length,
            Body = Convert.ToHexString(body)
        });
        await context.Reply((CmdPacketType)candidate.ReplyOpcode, body, "auction_average_price_reply");
    }

    /// <summary>
    /// CMD 188 `AUCTION_MY_REGISTED_ITEM_INFO`（「我的拍卖品 → 上架的物品」）。
    /// 用户上架成功后必须能在这里看到自己的挂牌：搜索按官方规则**排除自己**，因此「我的上架」是唯一入口，
    /// 不应答时表现上等同于「物品被吞」。
    /// </summary>
    Task<CommandResult> MyRegistedItems(GameConnectionContext context, Packet packet) =>
        MyListAsync(context, packet, AuctionMyListPanel.Registed);

    /// <summary>CMD 189 `AUCTION_MY_BIDDING_INFO`（「我的拍卖品 → 我的竞价」）：竞价未接入，恒回空列表。</summary>
    Task<CommandResult> MyBidding(GameConnectionContext context, Packet packet) =>
        MyListAsync(context, packet, AuctionMyListPanel.Bidding);

    /// <summary>CMD 190 `AUCTION_MY_AUCTION_HISTORY`（「拍卖历史」）：该角色的全部状态挂牌。</summary>
    Task<CommandResult> MyAuctionHistory(GameConnectionContext context, Packet packet) =>
        MyListAsync(context, packet, AuctionMyListPanel.History);

    /// <summary>
    /// 「我的拍卖品」三个页签的共用实现（CMD 188 / 189 / 190）。
    ///
    /// **请求体恒 1 字节 = 窗口 `mode`**（`0` = 普通拍卖行、`1` = 金币寄售），应答**必须回显**它：
    /// 参考实现里 `mode` 决定记录进哪个窗口类的面板，回错窗口 ⇒ 用户可见的网格始终为空。
    /// 空请求体解析不出 mode：页签「点了没反应」比回错窗口更糟，因此回落到普通拍卖行窗口（`0`，
    /// 本客户端的「我的拍卖品」就挂在这个窗口里）并把 `ModeFallback` 记进日志。
    ///
    /// ★ **`mode=1`（金币寄售窗口）一律回空列表**：那个窗口的「确认寄售的金币」列的是**寄售金币**，
    /// 与拍卖行挂牌是两套数据；回拍卖行挂牌会把装备画进寄售面板（2026-10-12 实机现象）。
    /// 只发 `flag + mode + count=0` 并记 `auction_my_list_consignment`：金币寄售未接入，既不伪造数据、
    /// 也不让面板停在无响应，且**不消耗**记录布局候选。
    ///
    /// **空列表也必须正确应答**（`flag + mode + count=0`，3 或 4 字节）：页签至少不再无响应，
    /// 记录体按候选布局逐步补（候选与未验证项见 <see cref="AuctionMyListReply"/>）。
    ///
    /// 数据来源走既有仓储合同（<see cref="AuctionService.SellerListings"/>），Handler 里不写 SQL：
    /// 188 = 该角色 `state=0` 在售未过期；190 = 该角色全部状态（本仓库尚未接入成交/下架/过期流转，
    /// 因此今天两者行集合相同，条件仍分开）；189 = 该角色作为出价人的挂牌 —— 竞价未接入（没有 bid 表），
    /// 因此**恒回空列表**，不伪造「出价中」的行。
    ///
    /// 每条应答都记 `auction_my_list`：页签、候选名、记录尺寸/道具块偏移/计数宽度、count、体长与实际十六进制头，
    /// 实机每点一次页签就能换一个候选并直接对照界面。
    /// </summary>
    async Task<CommandResult> MyListAsync(GameConnectionContext context, Packet packet, AuctionMyListPanel panel)
    {
        byte[] body = packet.Body;
        var request = AuctionMyListRequest.Parse(body);
        byte mode = request.Mode;
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        // 未知窗口仍回空列表，并且不消耗「我的拍卖品」探针。
        if (mode is not (AuctionMyListReply.NormalWindowMode or AuctionMyListReply.ConsignmentWindowMode))
        {
            var consignment = AuctionMyListReply.DefaultFor(panel);
            byte[] empty = AuctionPackets.MyListReply(mode, [], consignment, now);
            log.Write("auction_my_list_consignment", new
            {
                Opcode = (int)panel,
                Panel = panel.ToString(),
                Connection = context.Id,
                Length = body.Length,
                Body = Convert.ToHexString(body),
                Mode = mode,
                Candidate = consignment.Name,
                BodyLength = empty.Length,
                Head = Convert.ToHexString(empty.AsSpan(0, Math.Min(empty.Length, 4))),
                Note = "未知拍卖窗口：只回空列表"
            });
            await context.Reply((CmdPacketType)(int)panel, empty, "auction_my_list_consignment");
            return CommandResult.Handled;
        }
        bool goldWindow = mode == AuctionMyListReply.ConsignmentWindowMode;
        var character = context.Session.Selected;
        if (character is null)
        {
            // 没有选中角色就不知道「谁的」挂牌：与搜索路径同款处理，保持未处理，不回一个可能是别人的列表。
            // 这里**先判角色再取候选**：被拒的请求不消耗候选下标，实机点一次就换一种布局的行为因此可预期。
            log.Write("auction_my_list_unavailable", new
            {
                Opcode = (int)panel,
                Connection = context.Id,
                Length = body.Length,
                Body = Convert.ToHexString(body)
            });
            return CommandResult.Unhandled;
        }
        // ★ 到期结算（到期未售 ⇒ 道具退回 + 保证金退还）必须在**读列表之前**做：
        //   ① 刚结算的行不该再出现在「我的上架」里（188 只取 state=0），而应出现在 190 的历史里；
        //   ② 这两个面板正是卖家会来看到期结果的地方 —— 本仓库没有定时任务，时间驱动的清扫挂在
        //      访问路径上（与邮箱 `ExpireMailbox` 同一形态）。失败不阻断列表：结算抛异常时按原样回列表，
        //      下一次访问重试（宁可晚退，不可把列表打成「点了没反应」）。
        SettleExpiredListings(context, character, panel);
        if (goldWindow)
        {
            // 金币窗口使用已确认的默认记录布局，不消耗探针。2026-10-12 实机证明这套记录能被寄售面板画出来。
            var consignment = AuctionMyListReply.DefaultFor(panel);
            IReadOnlyList<AuctionListing> goldRows = panel switch
            {
                AuctionMyListPanel.Registed => auctions.SellerListings(character.Id, now, includeInactive: false, goldMarket: true),
                AuctionMyListPanel.History => auctions.SellerListings(character.Id, now, includeInactive: true, goldMarket: true),
                _ => []
            };
            byte[] goldPayload = AuctionPackets.MyListReply(mode, goldRows, consignment, now);
            log.Write("auction_my_list_consignment", new
            {
                Opcode = (int)panel,
                Panel = panel.ToString(),
                Connection = context.Id,
                Length = body.Length,
                Body = Convert.ToHexString(body),
                Mode = mode,
                Candidate = consignment.Name,
                Count = goldRows.Count,
                BodyLength = goldPayload.Length,
                Head = Convert.ToHexString(goldPayload.AsSpan(0, Math.Min(goldPayload.Length, 4))),
                Note = "金币寄售窗口：只列出该角色的金币券挂牌"
            });
            await context.Reply((CmdPacketType)(int)panel, goldPayload, "auction_my_list_consignment");
            return CommandResult.Handled;
        }
        // 本次请求取一份候选快照：探针关闭时就是该页签的默认候选（参考实现的量）。
        var layout = nextMyList(panel).MyList;
        IReadOnlyList<AuctionListing> rows = panel switch
        {
            AuctionMyListPanel.Registed => auctions.SellerListings(character.Id, now, includeInactive: false, goldMarket: false),
            AuctionMyListPanel.History => auctions.SellerListings(character.Id, now, includeInactive: true, goldMarket: false),
            _ => []
        };
        byte[] payload = AuctionPackets.MyListReply(mode, rows, layout, now);
        log.Write("auction_my_list", new
        {
            Opcode = (int)panel,
            Panel = panel.ToString(),
            Length = body.Length,
            Body = Convert.ToHexString(body),
            Mode = mode,
            // 空请求体的回落是可事后分辨的：出现它就说明客户端这一版的请求体和参考实现不一样。
            ModeFallback = request.Fallback,
            Seller = character.Id,
            SellerName = character.Name,
            // Rows = 取到的挂牌条数；Count = 本次真正写进应答 count 的条数（哨兵/限幅后），两者都由候选解释。
            Rows = rows.Count,
            Count = layout.CountFor(rows.Count),
            Candidate = layout.Name,
            Layout = layout.Note,
            RecordSize = layout.RecordSize,
            ItemOffset = layout.ItemOffset,
            CountWidth = layout.CountWidth,
            RemainOffset = layout.RemainOffset,
            HeadOnly = layout.HeadOnly,
            BodyLength = payload.Length,
            // 头部 4 字节就够核对「flag 在体首 + mode 回显 + count」；体长另有字段。
            Head = Convert.ToHexString(payload.AsSpan(0, Math.Min(payload.Length, 4)))
        });
        await context.Reply((CmdPacketType)(int)panel, payload, "auction_my_list");
        return CommandResult.Handled;
    }

    /// <summary>
    /// 到期结算的接入点（CMD 188「我的上架」与 190「拍卖历史」的读路径）。
    ///
    /// 本仓库**没有定时任务**，因此「到期」这个时间事件挂在卖家查看自己挂牌的请求上
    /// （与邮箱 `ExpireMailbox` 在打开邮箱时清理同一形态）：结算完再读列表，
    /// 于是刚结算的行只出现在 190 的历史里，而 188 的在售列表里不再有它。
    ///
    /// **只结算本人**的在售挂牌（<see cref="AuctionService.PlanExpire"/> 按 seller 过滤）：
    /// 机器人挂牌没有可退的对象，不做无主的退款。189「我的竞价」不结算 —— 它恒回空列表、与到期无关。
    ///
    /// 失败策略：结算本身是**写**（状态流转 + 退回邮件），因此按仓储既有约定，写失败照旧向上抛
    /// （不吞异常、不编一个假成功）；只有「这一行投不出去」这类领域级的
    /// <see cref="InvalidOperationException"/> 被记下来 —— 让一次到期退回的失败不至于把
    /// 「我的拍卖品」整个面板打成「点了没反应」，下一次访问会重试。
    /// </summary>
    void SettleExpiredListings(GameConnectionContext context, Character seller, AuctionMyListPanel panel)
    {
        if (panel is AuctionMyListPanel.Bidding)
            return;
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var plan = auctions.PlanExpire(seller, now);
        if (plan.IsEmpty && plan.Skipped.IsDefaultOrEmpty)
            return;
        try
        {
            var result = context.Session.SettleExpiredListings(auctions, seller, plan);
            log.Write("auction_expire_settled", new
            {
                Opcode = (int)panel,
                Seller = seller.Id,
                SellerName = seller.Name,
                SettledAtUnix = plan.SettledAtUnix,
                Planned = plan.Rows.Length,
                Settled = result.SettledCount,
                // 退还金额逐行取自 `auction_listings.deposit_gold`（见 Rows 里的 Gold）；
                // 这里另记一份**当前政策常数**，便于事后分辨「历史行退 0」与「政策变了」。
                DepositPolicy = AuctionService.ListingDepositGold,
                Rows = result.Rows.Select(row => new
                {
                    row.ListingId,
                    row.ItemTemplateId,
                    row.Mail.Key,
                    row.Mail.Gold,
                    row.Delivered,
                    row.Note
                }).ToArray(),
                Skipped = result.Skipped.ToArray()
            });
            // 退回邮件的到达提醒走邮件模块既有入口（它自己核对 Ready / 选中角色 / 连接归属，
            // 失败不影响已经提交的结算）。
            if (result.SettledCount > 0)
                context.Session.NotifyMailbox(seller.Id);
        }
        catch (InvalidOperationException error)
        {
            log.Write("auction_expire_failed", new
            {
                Opcode = (int)panel,
                Seller = seller.Id,
                Planned = plan.Rows.Length,
                Error = error.Message
            });
        }
    }

    // —— 一口价购买：CMD 185 已实现（跨账号钱货转移），CMD 334 仍是请求抓取桩 ——

    /// <summary>
    /// CMD 185 `AUCTION_BIDDING`（一口价购买）。请求体布局与两个实测样本见
    /// <see cref="AuctionBuyRequest"/>；业务规则在 <see cref="AuctionService.PlanBuy"/>，
    /// 事务在 <see cref="AuctionService.Buy"/>（**买方扣款 + 挂牌置为已成交 + 两封托管邮件同一事务**）。
    ///
    /// 与上架/取消同一套三步编排（只读规划 → 会话级提交并安装快照 → 应答），差别只有两处：
    ///   ① 这是本仓库唯一的**跨账号**路径：只取买方一把租约，卖方所得走**托管邮件**
    ///      （见 <see cref="Game.Session.GameSession.BuyListing"/> 的说明）；
    ///   ② **提交后不补发任何背包/金币刷新包**：185 回调会本地乐观扣一次金币，补发 `0x000D`/`0x000E`
    ///      会被叠加成「再扣一次」；道具与卖方金币都由邮件模块的领取路径入包/入钱包。
    ///      成交后唯一的客户端可见动作是给买方本人的**新邮件到达提醒**（走邮件模块既有入口）。
    ///
    /// **失败一律回白名单码**（<see cref="AuctionBuyErrors"/>，三档：静默 `0x7e` / 通用 `0xd2` /
    /// 买自己的挂牌 `0x69`）：客户端把未知 errcode 当成成交，界面就会假装买到了而钱货没动。
    /// 不足 30 字节、未选中角色、门禁挡住时同样回白名单码，不静默、不返回通用成功。
    /// </summary>
    async Task<CommandResult> Bidding(GameConnectionContext context, Packet packet)
    {
        byte[] body = packet.Body;
        AuctionBuyRequest request;
        try
        {
            request = AuctionBuyRequest.Parse(body);
        }
        catch (InvalidDataException error)
        {
            // 不足 30 字节：拿不到 [26..29] 的物品模板 id，无法与挂牌交叉校验 ⇒ 明确失败
            // （不按残缺字段去动跨账号资产）。码取通用档，与 183/184 对不完整请求的做法一致。
            log.Write("auction_buy_rejected", new
            {
                Reason = error.Message,
                ClientCode = AuctionBuyErrors.Generic,
                Length = body.Length,
                Body = Convert.ToHexString(body)
            });
            await context.Reply(CmdPacketType.AUCTION_BIDDING,
                AuctionPackets.BuyFailure(AuctionBuyErrors.Generic), "auction_buy_rejected");
            return CommandResult.Handled;
        }
        var character = context.Session.Selected;
        if (character is null || !context.Session.Ready || context.Session.Phase != Game.Session.SessionPhase.Town ||
            context.BlocksTownActions)
        {
            log.Write("auction_buy_unavailable", new
            {
                Connection = context.Id,
                Length = body.Length,
                Body = Convert.ToHexString(body),
                Ready = context.Session.Ready,
                Phase = context.Session.Phase.ToString(),
                Blocked = context.BlocksTownActions
            });
            await context.Reply(CmdPacketType.AUCTION_BIDDING,
                AuctionPackets.BuyFailure(AuctionBuyErrors.Generic), "auction_buy_rejected");
            return CommandResult.Handled;
        }
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        // 只读定位（不占 owner）：拿不到这一行就无从判断「谁的、在不在售、一口价多少」。
        var listing = auctions.FindListing(request.ListingId);
        var decision = auctions.PlanBuy(character, listing, request, now);
        if (decision.Options is null)
        {
            byte code = AuctionBuyErrors.ToClientCode(decision.Error);
            log.Write("auction_buy_rejected", new
            {
                Reason = decision.Error.ToString(),
                Detail = decision.Detail,
                ClientCode = code,
                Length = body.Length,
                Body = Convert.ToHexString(body),
                request.Leading,
                RequestAmount = request.Amount,
                request.ListingId,
                RequestExpiresAtUnix = request.ExpiresAtUnix,
                request.ItemTemplateId,
                // 挂牌侧读数 = 金额口径的判定证据：把 RequestAmount 与 UnitPrice / TotalPrice 对照即可
                // 看出客户端发的是单价还是总额（数量 = 1 时两者同值，样本区分不了 ⇒ 需要数量 > 1 的挂牌）。
                ListingTemplateId = listing?.ItemTemplateId ?? 0,
                UnitPrice = listing?.BuyoutPrice ?? 0,
                ItemCount = listing?.ItemCount ?? 0,
                TotalPrice = listing is null ? 0 : (long)listing.BuyoutPrice * listing.ItemCount,
                ExpiresEchoMatches = listing is not null && listing.ExpiresAtUnix == request.ExpiresAtUnix,
                Seller = listing?.SellerCharacterId,
                State = listing?.State ?? -1,
                ListingExpiresAtUnix = listing?.ExpiresAtUnix ?? 0,
                // 请求里的保留区（定案说全 0）：只记录、不据此拒绝，换代时可用于分辨包体版本。
                Reserved = request.ReservedHex(body),
                CharacterId = character.Id,
                CharacterGold = character.Inventory.Gold
            });
            await context.Reply(CmdPacketType.AUCTION_BIDDING, AuctionPackets.BuyFailure(code), "auction_buy_rejected");
            return CommandResult.Handled;
        }
        var plan = decision.Options;
        // 提交：买方扣款 + 挂牌置为已成交 + 两封托管邮件同事务；提交成功后由会话安装在线快照并铸造回执。
        // 写库失败照旧向上抛（协议层按既有约定处理），这里不吞异常；条件更新 0 行已被服务层翻成领域拒绝。
        var commit = context.Session.BuyListing(auctions, character, plan);
        if (commit.Buy.Character is null)
        {
            byte code = AuctionBuyErrors.ToClientCode(commit.Buy.Error);
            log.Write("auction_buy_rejected", new
            {
                Reason = commit.Buy.Error.ToString(),
                Detail = commit.Buy.Detail,
                ClientCode = code,
                Stale = true,
                request.ListingId,
                RequestAmount = request.Amount,
                plan.UnitPrice,
                plan.TotalPrice,
                plan.ItemCount,
                CharacterId = character.Id,
                CharacterGold = character.Inventory.Gold
            });
            await context.Reply(CmdPacketType.AUCTION_BIDDING, AuctionPackets.BuyFailure(code), "auction_buy_rejected");
            return CommandResult.Handled;
        }
        log.Write("auction_buy", new
        {
            Buyer = commit.Buy.Character.Id,
            BuyerName = commit.Buy.Character.Name,
            Revision = commit.Buy.Character.Revision,
            Gold = commit.Buy.Character.Inventory.Gold,
            // 提交回执与刚安装的快照同源：带上它就能事后核对「这次成交属于哪一代 owner / 哪一次选角」。
            // 购买**不发布任何背包/金币刷新**，因此回执只用于核对，不喂给投影。
            ReceiptGeneration = commit.Receipt?.Generation ?? 0,
            ReceiptEpoch = commit.Receipt?.SelectionEpoch ?? 0,
            plan.ListingId,
            plan.ItemTemplateId,
            plan.ItemKind,
            plan.ItemCount,
            plan.UnitPrice,
            plan.TotalPrice,
            plan.SellerProceeds,
            SaleTaxPercent = AuctionService.SaleTaxPercent,
            // ★ 金额口径：命中单价 / 总额 / 两者同值（数量 = 1 区分不了）。
            //   第三个样本（数量 > 1 的挂牌）点一次，用这三个字段即可定案。
            AmountBasis = plan.AmountBasis.ToString(),
            RequestedAmount = plan.RequestedAmount,
            Seller = plan.SellerCharacterId,
            plan.SellerName,
            request.Leading,
            RequestExpiresAtUnix = plan.RequestExpiresAtUnix,
            ListingExpiresAtUnix = plan.ExpiresAtUnix,
            ExpiresEchoMatches = plan.ExpiresEchoMatches,
            SettledAtUnix = plan.SettledAtUnix,
            // 两封托管行的结果：投给谁、多少钱、有没有被跳过；跳过的必然带原因（机器人卖家 / 角色行已删）。
            Custody = commit.Buy.Custody.Select(row => new
            {
                row.Key,
                Recipient = row.RecipientCharacterId,
                row.Gold,
                row.Delivered,
                row.Note
            }).ToArray(),
            // 下面两个字段是这次成交的**客户端契约结论**：不发金币/背包刷新，只给买方一次邮件到达提醒。
            GoldRefresh = "none",
            MailAlarm = "buyer"
        });
        await context.Reply(CmdPacketType.AUCTION_BIDDING, AuctionPackets.BuySuccess(), "auction_buy");
        // ★ 不补发背包/金币刷新（理由见方法注释）；只把「新邮件到达」交给邮件模块既有入口，
        //   它自己核对 Ready / 选中角色 / 连接是否仍属于本账号，失败也不会影响已经提交的成交。
        context.Session.NotifyMailbox(commit.Buy.Character.Id);
        return CommandResult.Handled;
    }

    /// <summary>
    /// CMD 334 `AUCTION_BUY_ITEM_APIECE` 的**请求抓取桩**（参考实现里的另一入口，该版本未走）。
    ///
    /// 两个实机样本证实「购买」按钮发的是 **185**（30B：`[26..29]` 才是物品模板 id），
    /// 而 334 连前导字节的精确偏移都只有参考实现自标「无法 100% 静态确定」⇒ 保持桩形态：
    /// 只记日志、不回任何应答、不改任何状态。等实机点出 334 的请求体再按同一套流程接入。
    /// </summary>
    Task<CommandResult> BuyItemApieceCapture(GameConnectionContext context, Packet packet) =>
        CaptureBuyRequest(context, packet, AuctionBuyCommand.BuyItemApiece);

    /// <summary>
    /// CMD 334 的请求抓取桩。**本方法只写一条日志：不改任何状态，也不回任何应答。**
    ///
    /// ① **为什么不回包**：客户端只有在**收到应答**时才可能把这次操作当成成交 —— 协议页与参考实现 §5.4
    ///    都写明「客户端不认识的 errcode 会被当成成功」，而 334 自己的失败码分派表**在本客户端没有实测**。
    ///    返回 <see cref="CommandResult.Unhandled"/>（无包）等于保持这条命令今天的可观测行为，
    ///    只多一条带字段读数的日志：界面不会假装成交，也不会因应答长度与回调预期不符触发 217 读超。
    ///    这同时满足「未实现请求不得返回通用成功」：不发任何应答，也就不占用任何白名单码的语义。
    /// ② **为什么把完整 hex 与逐字段读数都记下来**：D95 的真实请求体是唯一能锁定这条布局的证据；
    ///    字段读数全部是**试探**（<see cref="AuctionBuyProbes"/>），离线比对时以 hex 为准。
    /// ③ 桩**不依赖**选中角色、阶段与 owner：请求在什么状态下到达都记下来 —— 日志本身才是要的产物。
    /// </summary>
    Task<CommandResult> CaptureBuyRequest(GameConnectionContext context, Packet packet, AuctionBuyCommand command)
    {
        var probe = AuctionBuyProbes.Describe(command, packet.Body);
        log.Write("auction_buy_request", new
        {
            Opcode = packet.Opcode,
            Name = ((CmdPacketType)packet.Opcode).ToString(),
            // 参考布局名与出处：日志里直接能看出「这次是按哪一份资料的偏移读的」，换代时不会串。
            Layout = probe.Layout,
            Evidence = probe.Evidence,
            Length = probe.BodySize,
            Body = Convert.ToHexString(packet.Body),
            ReferenceSize = probe.ReferenceSize,
            BodyComplete = probe.BodyComplete,
            // 逐字段试探读数：Complete=false 表示包体不够长（**不补 0、不猜测**）。
            Fields = probe.Fields.Select(field => new
            {
                field.Name, field.Offset, field.Width, field.Complete, field.Value, field.Raw, field.Note
            }).ToArray(),
            // 前 16 个位置上的 i32 读值：与参考实现 off0..15 的探测打印同款，便于离线对齐偏移。
            Windows = probe.Windows.Select(window => new { window.Offset, window.Value }).ToArray(),
            Tail = probe.TailHex,
            Note = probe.Note,
            Connection = context.Id,
            Character = context.Session.Selected?.Id ?? 0,
            Ready = context.Session.Ready,
            Phase = context.Session.Phase.ToString(),
            // 下面三个字段是这次抓包的**结论**：未实现、没有任何状态变更、没有应答。
            Implemented = false,
            StateChange = "none",
            Reply = "none"
        });
        return Task.FromResult(CommandResult.Unhandled);
    }

    /// <summary>
    /// CMD 183 `AUCTION_REGIST_ITEM`（上架）。请求体布局与实测样本见
    /// <see cref="AuctionRegisterRequest"/>；这里是三步编排，业务规则在
    /// <see cref="AuctionService.PlanRegister"/>、事务在 <see cref="AuctionService.Register"/>。
    ///
    /// **每一步的失败都必须回白名单里的错误码**（<see cref="AuctionRegisterErrors"/>）：
    /// 客户端把未知 errcode 当成上架成功 ⇒ 界面假装成功而道具还在背包，比「什么都不回」更糟。
    /// 请求体不足 28 字节、未选中角色、连接被门禁挡住时同样回白名单码而不是静默或通用成功。
    ///
    /// **先应答、后刷新**：183 应答的 `mode=0` 负责清客户端「上架进行中锁」`0x20c`；
    /// 背包列刷新（`0x000D`）必须在应答之后，否则客户端会先收到物品变化、界面仍处于锁定态。
    /// 刷新复用现有背包投影（先按挂牌的 plan 发布差量、再补发整列），由投影自己核对
    /// generation / 选角 epoch / 阶段，不在这里手搓包体。
    /// </summary>
    async Task<CommandResult> RegisterItem(GameConnectionContext context, Packet packet)
    {
        byte[] body = packet.Body;
        AuctionRegisterRequest request;
        try
        {
            request = AuctionRegisterRequest.Parse(body);
        }
        catch (InvalidDataException error)
        {
            // 主体不足 28 字节：无法安全定位源槽与价格，明确失败（不按残缺字段扣道具）。
            log.Write("auction_register_rejected", new
            {
                Reason = error.Message,
                Length = body.Length,
                Body = Convert.ToHexString(body)
            });
            await context.Reply(CmdPacketType.AUCTION_REGIST_ITEM,
                AuctionPackets.RegisterFailure(AuctionRegisterErrors.Generic), "auction_register_rejected");
            return CommandResult.Handled;
        }
        var duration = AuctionDurationResolution.Resolve(request.DurationHours);
        var character = context.Session.Selected;
        if (character is null || !context.Session.Ready || context.Session.Phase != Game.Session.SessionPhase.Town ||
            context.BlocksTownActions)
        {
            // 没有选中角色就无法定位源槽、也无法判定「排除自己」，保持明确失败，不返回成功。
            log.Write("auction_register_unavailable", new
            {
                Connection = context.Id,
                Length = body.Length,
                Body = Convert.ToHexString(body),
                Ready = context.Session.Ready,
                Phase = context.Session.Phase.ToString(),
                Blocked = context.BlocksTownActions
            });
            await context.Reply(CmdPacketType.AUCTION_REGIST_ITEM,
                AuctionPackets.RegisterFailure(AuctionRegisterErrors.Generic), "auction_register_rejected");
            return CommandResult.Handled;
        }
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var decision = auctions.PlanRegister(character, request, now);
        if (decision.Options is null)
        {
            byte code = AuctionRegisterErrors.ToClientCode(decision.Error);
            log.Write("auction_register_rejected", new
            {
                Reason = decision.Error.ToString(),
                ClientCode = code,
                Length = body.Length,
                Body = Convert.ToHexString(body),
                request.Leading,
                request.SourceList,
                request.SourceSlot,
                request.ItemTemplateId,
                request.ItemCount,
                request.StartingPrice,
                request.BuyoutPrice,
                request.BuyoutEcho,
                BuyoutEchoMatches = request.BuyoutEchoMatches,
                request.DurationHours,
                AppliedDurationHours = duration.Applied,
                DurationFallback = duration.Fallback,
                // 价格上限的判定依据就是角色自己的这一列（默认 4 亿）：
                CharacterId = character.Id,
                AuctionGoldLimit = character.Inventory.AuctionGoldLimit
            });
            await context.Reply(CmdPacketType.AUCTION_REGIST_ITEM, AuctionPackets.RegisterFailure(code), "auction_register_rejected");
            return CommandResult.Handled;
        }
        var plan = decision.Options;
        // 提交：背包扣减 + 挂牌插入同事务（事务在 Persistence 里），提交成功后由会话安装在线快照并产出
        // 整列刷新请求（含提交回执）。异常由协议层按既有约定处理（写库失败不返回成功，这里不吞）。
        var commit = context.Session.RegisterListing(auctions, character, plan);
        if (commit.Listing.Character is null)
        {
            byte code = AuctionRegisterErrors.ToClientCode(commit.Listing.Error);
            log.Write("auction_register_rejected", new { Reason = commit.Listing.Error.ToString(), ClientCode = code, Stale = true });
            await context.Reply(CmdPacketType.AUCTION_REGIST_ITEM, AuctionPackets.RegisterFailure(code), "auction_register_rejected");
            return CommandResult.Handled;
        }
        log.Write("auction_register", new
        {
            Seller = commit.Listing.Character.Id,
            SellerName = commit.Listing.Character.Name,
            Revision = commit.Listing.Character.Revision,
            plan.ItemTemplateId,
            plan.ItemCount,
            plan.ItemKind,
            plan.StartingPrice,
            plan.BuyoutPrice,
            plan.DurationHours,
            plan.PostedAtUnix,
            plan.ExpiresAtUnix,
            // 本次挂牌已扣的保证金（用户口径：挂牌阶段扣）：与 PlanRegister 里的扣款同源，只此一处常数。
            DepositGold = AuctionService.ListingDepositGold,
            // 客户端给的槽位线索与实际采信的源槽：不一致时 Reason 说明为什么走了扫描路径。
            ClientList = request.SourceList,
            ClientSlot = request.SourceSlot,
            UsedList = (byte)plan.Source.List,
            UsedSlot = plan.Source.Index,
            request.Leading,
            request.BuyoutEcho,
            BuyoutEchoMatches = request.BuyoutEchoMatches,
            RequestedDurationHours = request.DurationHours,
            DurationFallback = duration.Fallback,
            Fallback = plan.FallbackReason,
            EntryLength = plan.ItemEntry.Length
        });
        await context.Reply(CmdPacketType.AUCTION_REGIST_ITEM, AuctionPackets.RegisterSuccess(), "auction_register");
        // 整列刷新（`WholeListRefresh`）连「当前占用的槽 + 虚拟货币槽」一起重发，而不是只发变化槽的差量；
        // 请求体连同提交回执由会话在安装快照时产出，因此投影不会再因缺少回执而抛异常。
        if (commit.Inventory is { } change)
            await inventory.RefreshAsync(context, change);
        return CommandResult.Handled;
    }

    /// <summary>
    /// CMD 184 `AUCTION_REGIST_CANCEL`（取消上架）。请求体布局与实测样本见
    /// <see cref="AuctionCancelRequest"/>；业务规则在 <see cref="AuctionService.PlanCancel"/>，
    /// 事务在 <see cref="AuctionService.Cancel"/>（道具还回背包 + 挂牌置为已下架 + 审计同一事务）。
    ///
    /// **失败一律回白名单码**（与 183 同一张表，见 <see cref="AuctionCancelErrors"/>）：
    /// 客户端把未知 errcode 当成成功，界面就会假装取消成功而挂牌还在。
    /// 长度不足 9 字节、未选中角色、门禁挡住时同样回白名单码，不静默、不返回通用成功。
    ///
    /// **先应答、后刷新**（与上架同序）：184 应答的 `mode=0` 负责刷新「我的拍卖品」面板；
    /// 背包列刷新在应答之后，且请求体由会话在安装提交快照时产出（带提交回执），
    /// 因此「提交后的刷新」不会成为失败点。
    /// </summary>
    async Task<CommandResult> CancelRegist(GameConnectionContext context, Packet packet)
    {
        byte[] body = packet.Body;
        AuctionCancelRequest request;
        try
        {
            request = AuctionCancelRequest.Parse(body);
        }
        catch (InvalidDataException error)
        {
            // 不足 9 字节：无法安全定位挂牌，明确失败（不按残缺字段去动资产）。
            log.Write("auction_cancel_rejected", new
            {
                Reason = error.Message,
                Length = body.Length,
                Body = Convert.ToHexString(body)
            });
            await context.Reply(CmdPacketType.AUCTION_REGIST_CANCEL,
                AuctionPackets.CancelFailure(AuctionCancelErrors.Generic), "auction_cancel_rejected");
            return CommandResult.Handled;
        }
        var character = context.Session.Selected;
        if (character is null || !context.Session.Ready || context.Session.Phase != Game.Session.SessionPhase.Town ||
            context.BlocksTownActions)
        {
            log.Write("auction_cancel_unavailable", new
            {
                Connection = context.Id,
                Length = body.Length,
                Body = Convert.ToHexString(body),
                Ready = context.Session.Ready,
                Phase = context.Session.Phase.ToString(),
                Blocked = context.BlocksTownActions
            });
            await context.Reply(CmdPacketType.AUCTION_REGIST_CANCEL,
                AuctionPackets.CancelFailure(AuctionCancelErrors.Generic), "auction_cancel_rejected");
            return CommandResult.Handled;
        }
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        // 只读定位（不占 owner）：拿不到这一行就无从判断「谁的、是否在售」。
        var listing = auctions.FindListing(request.ListingId);
        var decision = auctions.PlanCancel(character, listing, request, now);
        if (decision.Options is null)
        {
            byte code = AuctionCancelErrors.ToClientCode(decision.Error);
            log.Write("auction_cancel_rejected", new
            {
                Reason = decision.Error.ToString(),
                Detail = decision.Detail,
                ClientCode = code,
                Length = body.Length,
                Body = Convert.ToHexString(body),
                request.Leading,
                request.ListingId,
                request.ExpiresAtUnix,
                // [5..8] 是辅助字段（推断为到期时间）：与库里那一行是否一致只作证据记录，不参与定位。
                ListingExpiresAtUnix = listing?.ExpiresAtUnix ?? 0,
                ExpiresEchoMatches = listing is not null && listing.ExpiresAtUnix == request.ExpiresAtUnix,
                Seller = listing?.SellerCharacterId,
                State = listing?.State ?? -1,
                CharacterId = character.Id
            });
            await context.Reply(CmdPacketType.AUCTION_REGIST_CANCEL, AuctionPackets.CancelFailure(code), "auction_cancel_rejected");
            return CommandResult.Handled;
        }
        var plan = decision.Options;
        // 提交：道具还回背包 + 挂牌置为已下架 + 背包审计同事务；提交成功后由会话安装快照并给出回执。
        // 写库失败照旧向上抛（协议层按既有约定处理），这里不吞异常。
        var commit = context.Session.CancelListing(auctions, character, plan);
        if (commit.Listing.Character is null)
        {
            byte code = AuctionCancelErrors.ToClientCode(commit.Listing.Error);
            log.Write("auction_cancel_rejected", new { Reason = commit.Listing.Error.ToString(), ClientCode = code, Stale = true });
            await context.Reply(CmdPacketType.AUCTION_REGIST_CANCEL, AuctionPackets.CancelFailure(code), "auction_cancel_rejected");
            return CommandResult.Handled;
        }
        log.Write("auction_cancel", new
        {
            Character = commit.Listing.Character.Id,
            CharacterName = commit.Listing.Character.Name,
            Revision = commit.Listing.Character.Revision,
            plan.ListingId,
            plan.ItemTemplateId,
            plan.ItemKind,
            plan.ListingCount,
            ReturnCount = plan.ReturnCount,
            ListingExpiresAtUnix = plan.ExpiresAtUnix,
            request.Leading,
            // [5..8] 是辅助字段（推断为到期时间）：与库里那一行是否一致只作证据记录，不参与定位。
            RequestExpiresAtUnix = request.ExpiresAtUnix,
            ExpiresEchoMatches = plan.ExpiresAtUnix == request.ExpiresAtUnix,
            // 还回背包的落点：日志里能直接看出「回到哪个槽 / 是不是并进了原来的堆」。
            ReturnedSlots = plan.Inventory.ChangedSlots.Select(slot => new { List = (byte)slot.List, slot.Index }).ToArray(),
            // 保证金**不退**（用户口径：取消上架不退，不然不叫保证金）：钱在挂牌阶段已经扣走，
            // 这条路径不产生任何退款，这里只把结论记进日志，便于与「到期退还」对照。
            Deposit = "forfeited",
            DepositGold = AuctionService.ListingDepositGold
        });
        await context.Reply(CmdPacketType.AUCTION_REGIST_CANCEL, AuctionPackets.CancelSuccess(), "auction_cancel");
        // 取消是「道具回到背包」：常规差量即可覆盖新增/并入的槽位，不需要整列重建（与上架的差别见会话入口注释）。
        if (commit.Inventory is { } change)
            await inventory.RefreshAsync(context, change);
        return CommandResult.Handled;
    }
}
