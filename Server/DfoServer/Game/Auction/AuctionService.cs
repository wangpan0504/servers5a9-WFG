using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Auction;

/// <summary>
/// 拍卖行挂牌的领域存储合同。与 IMailboxStore、IClientSettingsStore 同形：合同声明在本层、
/// 实现归 Persistence，由应用入口显式装配。Game 不允许引用 Persistence 实现（S5A9001），
/// 因此这里只声明实际用到的读写，不为每个类包一层转发接口。
/// 搜索是跨账号只读查询、不触碰角色资产，所以不接收账号 owner。
/// </summary>
internal interface IAuctionListingStore
{
    /// <summary>在售且未过期的挂牌：返回本页记录与同一事务内的匹配总数。</summary>
    AuctionSearchPage Search(AuctionSearchFilter filter, long now, int offset, int limit);

    /// <summary>
    /// 在售且未过期的**全部**匹配挂牌（按 listing_id 稳定排序），不分页。
    /// 供带装备部位维度的分类搜索在内存里过滤后再分页：部位无法下推到 SQL（表里没有部位列），
    /// 而匹配总数必须是过滤后的值，所以必须取全量再过滤。
    /// </summary>
    ImmutableArray<AuctionListing> LoadMatches(AuctionSearchFilter filter, long now);

    /// <summary>表内挂牌总数（含已成交、已过期），用于判断是否需要开发种子。</summary>
    int CountListings();

    /// <summary>
    /// 某个卖家**自己**的挂牌（CMD 188「我的上架」/ 190「我的拍卖历史」），按 <c>listing_id</c> 稳定排序、不分页。
    ///
    /// 与 <see cref="Search"/> 的区别有两点，都不能省：
    ///   ① 卖家是**本人**（不是「排除本人」）：两个面板显示的就是自己的东西；
    ///   ② 不过滤分页：客户端这两个面板不翻页，本角色在售上限是
    ///      <see cref="AuctionService.SellerListingCap"/>（100），一次全发即可，条数上限由应答记录的计数宽度兜住。
    /// <paramref name="includeInactive"/> = false 只取**在售且未过期**（188）；true 取该卖家的**全部状态**
    /// （190 的历史：已成交/已下架/已过期/已结算）。本仓库尚未接入这些状态流转，因此两条查询今天返回
    /// 相同的行集合；条件仍分开写，「历史」不会被实现成「在售」。
    /// </summary>
    ImmutableArray<AuctionListing> LoadSellerListings(long sellerCharacterId, long now, bool includeInactive);

    /// <summary>一次事务写入一批挂牌。</summary>
    void InsertListings(ImmutableArray<AuctionListingDraft> listings);

    /// <summary>
    /// 某卖家当前**在售且未过期**的挂牌数，用于上架数量上限。按 <c>auction_listings_seller</c> 索引走纯 SELECT
    /// （不申请 writer、不建行、不补默认值）；失败按「查不到」处理并由上层放行，见
    /// <see cref="AuctionService.SellerListingCap"/>。
    /// </summary>
    int CountSellerListings(long sellerCharacterId, long now);

    /// <summary>
    /// 上架提交：**背包扣减与新挂牌在同一个外层事务内写入**，事务由本方法拥有。
    /// 挂牌插入失败时整个事务回滚，背包（含审计与 revision）一并回退，不留半成品。
    /// 与 <c>IMailboxStore.SendMail</c> 同形：接收 owner、规划时的角色快照与完整计划，
    /// 提交前重新核对角色 revision / 背包快照 / owner generation。
    /// </summary>
    AuctionListingRegisterResult RegisterListing(AccountOwners.Lease owner, Character expected, AuctionRegisterPlan plan);

    /// <summary>
    /// 按挂牌 id 读**一行**（含 <see cref="AuctionListing.State"/>），供取消上架（CMD 184）定位与校验。
    /// 只读查询、不接收 owner（和搜索一样不触碰角色资产）；没有这一行时返回 null。
    /// </summary>
    AuctionListing? LoadListing(int listingId);

    /// <summary>
    /// 取消上架提交：**挂牌置为已下架 + 道具还回背包 + 背包审计在同一个外层事务内**，事务由本方法拥有。
    /// 与 <see cref="RegisterListing"/> 同形，但方向相反：先写背包（发放实例）再更新挂牌状态；
    /// 任一失败都抛异常 ⇒ 事务不提交 ⇒ 挂牌仍是 state=0、道具没有凭空多出来，**不会出现「挂牌没了但道具也没回来」**。
    /// 在事务内还要复核「这行仍是当前角色的、且 state=0」，避免并发取消把一件道具还两次。
    /// </summary>
    AuctionListingCancelResult CancelListing(AccountOwners.Lease owner, Character expected, AuctionCancelPlan plan);

    /// <summary>
    /// 一口价购买提交：**买方扣款 + 挂牌置为已成交 + 买方道具托管邮件 + 卖方金币托管邮件在同一个外层事务内**，
    /// 事务由本方法拥有（只取买方一把租约，卖方交付走托管行，见
    /// [并发、事务与响应延迟](../../../../Docs/规范/并发事务与响应延迟.md#跨账号转移拍卖行一口价购买)）。
    ///
    /// 两条不能省的幂等/一致性命中点：
    ///   ① 事务内重读挂牌行，复核**内容**（一口价、数量、模板、卖家）与过期时间仍与规划一致；
    ///   ② 挂牌状态用条件更新 `state=0 → 1`，**影响行数必须为 1**：为 0 抛
    ///      <see cref="AuctionBuyRaceLostException"/> 让整个事务回滚 —— 这是「同一挂牌只能成交一次」的
    ///      唯一判据，重复点击、重放、以及「提交成功但应答丢失后的重试」都不会第二次扣款或第二次发货。
    /// </summary>
    AuctionListingBuyResult BuyListing(AccountOwners.Lease owner, Character expected, AuctionBuyPlan plan);

    /// <summary>
    /// 某卖家**已到期但仍在售**的挂牌（`state=0 AND expires_at_unix&lt;=now`），到期结算的输入。
    /// 机器人挂牌（`seller_character_id IS NULL`）没有可退的对象，不在返回集合里。
    /// </summary>
    ImmutableArray<AuctionListing> LoadExpiredListings(long sellerCharacterId, long now);

    /// <summary>
    /// 到期结算提交（**到期未售**）：**挂牌置为已过期 + 一封系统退回邮件（道具 + 退还的保证金）
    /// 在同一个外层事务内**，事务由本方法拥有。
    ///
    /// 与 <see cref="BuyListing"/> 同形：只写挂牌行与邮件行、**不改卖家角色行**（钱进托管邮件，
    /// 卖家在自己的邮箱里领取），因此这里既没有 revision 依赖、也不需要第二把租约。
    /// 幂等判据是**状态条件更新** `state=0 → 3`：影响 0 行说明这一行已被下架/成交/并发结算，
    /// 跳过它并记原因，而不是再退一次保证金、再退一次道具。
    /// </summary>
    AuctionExpiryResult SettleExpiredListings(AccountOwners.Lease owner, Character expected, AuctionExpiryPlan plan);
}

/// <summary>
/// 上架的结果：`Error` 是领域错误（None 表示成功），`Character` 是提交后的角色快照
/// （Repository 重新分配 UID 后的权威值），写库失败则整体抛出、不返回成功。
/// </summary>
internal sealed record AuctionListingRegisterResult(AuctionRegisterError Error, Character? Character)
{
    public static AuctionListingRegisterResult Reject(AuctionRegisterError error) => new(error, null);
}

/// <summary>
/// 上架被拒绝的原因。**每一条都要能映射到客户端认识的白名单错误码**（见 <see cref="AuctionRegisterErrors"/>）：
/// 客户端把未知 errcode 当成功，因此枚举里不允许出现没有映射的取值。
/// </summary>
internal enum AuctionRegisterError
{
    None = 0,

    /// <summary>请求字段非法：主体不足 28 字节、数量非正、一口价为负、模板 id 非正。</summary>
    InvalidRequest,

    /// <summary>源道具定位失败：槽位内容与 itemId 不符，且按 itemId 扫描得不到唯一候选。</summary>
    ItemNotFound,

    /// <summary>道具不可上架：已绑定 / 有交易限制 / 已锁定 / 已到期 / 是计时或租赁实例。</summary>
    ItemNotTradable,

    /// <summary>价格非法：超出 <c>characters.auction_gold_limit</c>、起拍价高于一口价。</summary>
    PriceOutOfRange,

    /// <summary>本人在售挂牌已达上限。</summary>
    ListingLimit,

    /// <summary>本人金币不足 <see cref="AuctionService.ListingDepositGold"/>：挂牌阶段要先收保证金。</summary>
    InsufficientGold,

    /// <summary>提交时角色或背包快照已变化（换角、接管、revision 冲突）。</summary>
    StalePlan
}

/// <summary>
/// ★ **客户端认识的上架（CMD 183）错误码白名单**。客户端回调 `0x1126cd0` 只在失败分支（`flag == 0`）
/// 读到 errcode 后按下面的表分派；**白名单以外的取值会被直接当成成功路径** ⇒ 界面假装上架成功，
/// 而道具还在背包里（状态不一致且无任何提示）。因此服务端**只能从这几个码里选**。
///
/// | errcode | 客户端行为 |
/// | ---: | --- |
/// | `0xd2` | 弹通用提示框（文本是客户端本地化的「安全模式」文案）后收尾 —— 最安全的兜底 |
/// | `0x69` | 弹本地化提示框（文本内嵌客户端本地查询到的数字）后收尾 |
/// | `0x72` | 弹提示框，**且继续 fall-through 读第 2 字节** ⇒ 应答必须是 2 字节 |
/// | `0x7e` / `0xce` / `0x7b` / `0x89` | **静默收尾**（只刷新窗口，不弹框） |
/// | 其它 | **被当成上架成功** —— 禁止下发 |
///
/// 本仓库的取值策略：**除「源道具定位失败」外一律 `0xd2`**（有提示、不会假装成功）。
/// 「道具已经不在背包里」对玩家而言是过期的操作，弹框只会困惑，因此映射到静默码 `0x7e`。
/// 各码对应的客户端**文本**尚未逐条实测，因此不做细分猜测（例如不把「已绑定」单独映射到 `0x69`）。
/// </summary>
internal static class AuctionRegisterErrors
{
    /// <summary>通用提示框（客户端给 `0xd2` 配的是「安全模式」文案）。所有没有更精确映射的失败都用它。</summary>
    public const byte Generic = 0xd2;

    /// <summary>静默收尾：只刷新窗口、不弹框。用于「道具已经不在」这类过期操作。</summary>
    public const byte Silent = 0x7e;

    /// <summary>白名单全表（含各自含义），供自测锁定「不可能下发未知码」。</summary>
    public static readonly ImmutableArray<byte> Whitelist = [Generic, 0x69, 0x72, 0x7e, 0xce, 0x7b, 0x89];

    /// <summary>
    /// 领域错误 → 客户端白名单码。**返回值一定在白名单内**（未知领域错误不会漏成「客户端当成成功」）。
    /// </summary>
    public static byte ToClientCode(AuctionRegisterError error) => error switch
    {
        AuctionRegisterError.ItemNotFound => Silent,
        AuctionRegisterError.None => throw new InvalidDataException("成功没有错误码"),
        _ => Generic
    };
}

/// <summary>
/// 拍卖行读用例：分页搜索（匹配总数 + 排除自己 + 分类过滤）与开发种子。
/// 只依赖存储合同：条目字节的排列由物品模块的编码入口产生，在装配处传进来（见 <see cref="ResolveSeedItems"/>），
/// 分类 id 的翻译由 <see cref="AuctionCategoryResolver"/> 负责，因此本层不引用 Network，也不需要 PVF 就能自测。
///
/// 上架（CMD 183）与取消上架（CMD 184）也归这里：`PlanRegister` / `PlanCancel` 都是**纯只读规划**
/// （不写库、不占 owner），`Register` / `Cancel` 才拿 owner 提交，且背包变更与挂牌写入共用同一个外层事务
/// （见 <see cref="IAuctionListingStore.RegisterListing"/> 与 <see cref="IAuctionListingStore.CancelListing"/>）。
///
/// 两个方向的物品快照编解码都在装配处注入（`itemEntrySnapshot` / `itemCoreSnapshot`）：
/// 上架把物品核心编码成挂牌保存的 123B 条目（不存核心 JSON、不改表结构），
/// 取消则把同一份条目**逐字段读回**核心，再交给既有的发放入口还回背包。
/// 解码入口要额外接收**物品种类**：条目字节里没有 Kind 字段（挂牌表单独一列存它），因此由调用方传入。
/// </summary>
internal sealed class AuctionService(IAuctionListingStore store, AuctionCategoryResolver categories,
    ItemCatalog items, EpicPieceCatalog pieces, Func<ItemCore, byte[]> itemEntrySnapshot,
    Func<byte[], ItemKind, ItemCore> itemCoreSnapshot)
{
    /// <summary>
    /// 客户端按每页固定 10 条计算总页数与翻页偏移，本批条数必须与它一致，
    /// 否则「下一页」的偏移会跳过或重复记录。
    /// </summary>
    public const int PageSize = 10;

    /// <summary>
    /// 一个角色的在售挂牌上限。参考实现里官方无拍卖券角色上限为 5，且**触顶时客户端只会弹 `0xd2`
    /// 那个看起来像「安全模式」的通用提示**。本仓库按参考实现放宽后的口径取 100：
    /// 私服的单人测试场景很容易同时挂几件（金币寄售 + 道具混挂），5 件太容易触顶，
    /// 而触顶的提示文本会误导玩家，因此不放 5。
    /// </summary>
    public const int SellerListingCap = 100;

    // 开发种子的候选物品（本机 PVF 中确认存在的普通商品）：四件低阶武器（训练用刀/东方棍/魔杖、长矛）、
    // 五件低阶防具（粗布衬衫、牛皮背心/短裤/腰带/拖鞋）、两件常见材料（无色小晶块、金色小晶块）
    // 与粗布腰带随机礼盒，共 12 件。
    // **不使用 3033–3037 这类账号钱包货币**：它们是 Main 容器 354–365 的货币虚拟槽，不是可上架实体。
    // 每件都必须能在 ItemCatalog 里解析成功，否则启动日志的 UnresolvedItemIds 会列出它。
    static readonly int[] SeedItemIds = [101010912, 102040571, 106020596, 106000064, 10001, 10400, 12400, 16400, 18400, 3037, 3262, 10014751];
    // 机器人卖家：seller_character_id 为 NULL，任何账号都不会把机器人挂牌当成「自己上架的」而排除。
    static readonly string[] SeedSellers = ["拍卖机器人甲", "拍卖机器人乙", "拍卖机器人丙"];
    const int SeedStartingPrice = 12_000;
    const int SeedPriceStep = 1_000;
    const int SeedStackCount = 5;
    const long SeedDurationSeconds = 86_400;

    /// <summary>种子候选物品池，供自测核对覆盖面与排除钱包物品；顺序即挂牌的轮换顺序。</summary>
    public static IReadOnlyList<int> SeedPool => SeedItemIds;

    /// <summary>
    /// 分页搜索：解析分类 id，物品大类下推到存储，装备部位在内存里补做，最后按同一份匹配集合分页。
    ///
    /// **为什么部位要在内存里过滤**：`auction_listings` 没有装备部位列，而武器/防具/首饰的顶层大类正是按部位区分的
    /// （三者 item_kind 都是 Equipment）。把部位展开成模板 id 再 `IN (…)` 已被实测否决（武器约 23000 个 id，
    /// SQLite 每次查询约 12 秒，另有约 11 秒的一次性目录遍历）。内存过滤的代价只在**本次匹配到的模板**上，
    /// 且部位按模板缓存一次。
    ///
    /// **为什么匹配总数必须是过滤后的值**：客户端按每页 10 条算总页数并直接显示「搜索出 N 个」，
    /// 用过滤前的条数会让页数虚高、翻到后面全是空页。因此内存过滤在分页之前完成，总数取过滤后的条数。
    /// </summary>
    public AuctionSearchPage Search(AuctionSearchRequest request, long? excludeSellerCharacterId, long now)
    {
        if (request.Offset < 0)
            throw new InvalidDataException("拍卖行翻页偏移不能为负");
        var plan = categories.Build(request, excludeSellerCharacterId);
        if (plan.SlotDimensions is not { } dimensions)
            return store.Search(plan.Filter, now, request.Offset, PageSize) with { Plan = plan };
        // 有内存维度（装备部位 / 武器族）：先取回 SQL 侧的全部匹配挂牌（已按 listing_id 稳定排序），
        // 再按维度过滤，最后分页——顺序不能反，反了总数就不是过滤后的值。
        var matches = store.LoadMatches(plan.Filter, now);
        var kept = ImmutableArray.CreateBuilder<AuctionListing>(matches.Length);
        foreach (var listing in matches)
        {
            if (categories.InCategory(dimensions, listing.ItemTemplateId))
                kept.Add(listing);
        }
        var filtered = kept.ToImmutable();
        return new(filtered.Skip(request.Offset).Take(PageSize).ToImmutableArray(), filtered.Length) { Plan = plan };
    }

    /// <summary>
    /// 某个物品模板当前在售挂牌的均价（整件，向下取整）。**不编造定价策略**：只对真实在售挂牌取统计量，
    /// 没有任何在售时返回 <see cref="AuctionAveragePrice.None"/>（均价 0、件数 0），不写常数表也不加随机扰动。
    ///
    /// 计入的「价」是与列表「当前竞拍」列同源的 <see cref="AuctionListing.DisplayPrice"/>（有出价取最高出价、
    /// 无出价取起拍价）：这是客户端界面上真实显示的价格，均价必须与它同源，否则「均价」与「列表」会对不上。
    /// 求和用 <c>long</c> 再除：单价上界是 int，几千件挂牌的 <c>int</c> 累加会静默溢出。
    ///
    /// 不分页、不做内存维度过滤：均价是「这个模板的在售行情」，与客户端当前点的分类节点无关，
    /// 客户端点任何节点拖动同一件物品都应得到同一个均价。
    /// </summary>
    public AuctionAveragePrice AveragePrice(int itemTemplateId, long now)
    {
        if (itemTemplateId <= 0)
            return AuctionAveragePrice.None;
        // 仓储没有聚合入口，这里复用同一套在售/未过期/模板过滤条件取全量匹配（见 IAuctionListingStore）。
        // 单价查询的条数天然受「同一模板在售件数」限制，与搜索一页 10 条无关。
        var matches = store.LoadMatches(new(itemTemplateId, null), now);
        if (matches.IsDefaultOrEmpty)
            return AuctionAveragePrice.None;
        long sum = 0;
        foreach (var listing in matches)
            sum += listing.DisplayPrice;
        return new(checked((int)(sum / matches.Length)), matches.Length);
    }

    /// <summary>
    /// CMD 182 应答要下发的 <c>key</c>（参考单价）：
    ///   - **有在售** → 在售均价（<see cref="AveragePrice"/>，与列表「当前竞拍」列同源）；
    ///   - **无在售** → 该模板的**商店售价**（PVF 的 `[price]`，无 `[price]` 时取 `[value]`，
    ///     见 <c>NpcShopCatalog.ItemShopPrice.Parse</c> 的 `gold` 归并），因为客户端拿这个值预填一口价输入框，
    ///     并在提交时校验「不能低于商店销售的价格」——回 0 会同时踩中「没有价格数据」与「低于商店价」
    ///     两条客户端判定（这就是「一直停在『正在统计拍卖行价格……』、上架必失败」的原因）；
    ///   - **商店价也取不到**（模板缺失 / 商店价为 0）→ 兜底 <see cref="AuctionKeyPrice.MinimumKey"/>（1）。
    ///
    /// 依据：`_review/06-拍卖行协议参考-S4A21.md` §4.1 与参考实现 `Data/auction_avg_reply.txt:441`
    /// （key 兼作非零门，实现里夹到至少 1）、`:307-312`（`keyminprice`：无在售时回落到 `keyprice`）。
    /// **不写死价格表、不加随机扰动**：无在售时的价来自该模板自己的 PVF 字段。
    /// </summary>
    public AuctionKeyPrice ResolveAverageKey(int itemTemplateId, long now)
    {
        var average = AveragePrice(itemTemplateId, now);
        var (floor, note) = ShopPriceFloor(itemTemplateId);
        // 有在售就如实回均价（不改写成商店价：列表显示的就是行情，均价必须与它同源）；
        // 无在售才用下限，并让日志能分辨三种来源。
        // ★ 两档都把 key 夹到 ≥ 1：客户端拿 key 当「有价格数据」的非零门，**0 永远是错的取值**
        //   （均价算出来是 0 只可能来自 0 价挂牌这种退化数据，此时按参考实现的做法夹到 1）。
        if (average.Count > 0)
            return new(average.Average, average.Count, floor, Math.Max(average.Average, AuctionKeyPrice.MinimumKey),
                AuctionKeySource.Listings, null);
        return new(0, 0, floor, floor, floor > AuctionKeyPrice.MinimumKey ? AuctionKeySource.ShopPrice : AuctionKeySource.Minimum, note);
    }

    /// <summary>
    /// 该模板的商店售价下限：取 PVF 的商店价（<c>ItemShopPrice.Gold</c>，即 NPC 卖给玩家的价）；
    /// 模板缺失或商店价为 0 时返回兜底 1，并把原因带回日志（<see cref="AuctionKeyPrice.Note"/>）。
    /// 这里只读进程内已加载的模板目录，不写库、不发包。
    ///
    /// **刻意捕获全部异常**：这是一次纯只读的「尽力而为」查价，取不到就退回 1 ——
    /// 让异常冒出去会让 182 整条应答失败（界面重新卡回「正在统计拍卖行价格……」），
    /// 那正是本次要修的症状。失败类型记进 <c>Note</c>，日志里仍能看出是哪一类问题。
    /// </summary>
    (int Floor, string? Note) ShopPriceFloor(int itemTemplateId)
    {
        if (itemTemplateId <= 0)
            return (AuctionKeyPrice.MinimumKey, "itemId 非正，无法查商店价");
        try
        {
            int gold = items.Get(itemTemplateId).ShopPrice.Gold;
            // 商店价为 0 的模板（PVF 里既没有 [price] 也没有 [value]）仍然不能回 0：非零门要求 ≥ 1。
            return gold > 0 ? (gold, null) : (AuctionKeyPrice.MinimumKey, "该模板的商店售价为 0（PVF 无 [price]/[value]）");
        }
        catch (Exception error)
        {
            return (AuctionKeyPrice.MinimumKey, $"模板解析失败（{error.GetType().Name}）：{error.Message}");
        }
    }

    // —— CMD 188 / 189 / 190「我的拍卖品」的读路径 ——
    /// <summary>
    /// 「我的上架」（188）与「拍卖历史」（190）的数据来源：**该角色自己的挂牌**，走同一份存储合同，
    /// 不在 Handler 或这里写 SQL（与搜索一样只声明「要哪些行」，条件归
    /// <see cref="IAuctionListingStore.LoadSellerListings"/>）。
    ///
    /// 没有选中角色时调用方不会走到这里；角色 id 非正（不可能出现在真实会话里）时返回空列表，
    /// 宁可回空也不做一次「卖家为 NULL」的查询把机器人挂牌当成玩家的东西发出去。
    /// </summary>
    public ImmutableArray<AuctionListing> SellerListings(long sellerCharacterId, long now, bool includeInactive) =>
        sellerCharacterId <= 0 ? [] : store.LoadSellerListings(sellerCharacterId, now, includeInactive);

    // —— CMD 183 上架（AUCTION_REGIST_ITEM）——

    /// <summary>`[trade limit]` 归一化后的裸词：这类道具按「剩余交易次数」判定，不是一概禁止。</summary>
    const string TradeLimit = "trade limit";

    /// <summary>
    /// **上架保证金**（每笔固定，金币）：挂牌阶段从卖方扣（用户口径，界面那一行「保证金 10,000金币」）。
    /// 退还口径同样是用户口径：**取消上架不退**（保证金的意义就在于此）、**到期未售退还**。
    ///
    /// 与 5% 成交税并列为拍卖行第二个政策常数，**只在这里写一份**：上架规划按它扣款，
    /// 到期结算按它退到退回邮件里，两侧共用同一个数，不会出现「扣 10000、退 5000」。
    /// </summary>
    public const int ListingDepositGold = 10_000;

    /// <summary>
    /// **只读规划**上架：定位源道具、判定可上架、算价与时长、生成扣减计划。
    ///
    /// 锁内不做任何资源解析：这里读的是模板目录（进程内已加载的只读缓存）与传入的角色快照，
    /// 不写库、不发包、不等待网络；提交在 <see cref="Register"/>。
    ///
    /// **源槽定位**（`[1]`/`[2..3]` 只是参考实现的**推断**，不能直接扣道具）：
    ///   ① 先按客户端给的列表类型 + 槽位取实例；只有当该槽内的模板 id 与 `[4..7]` 一致时才采信；
    ///   ② 对不上（或列表类型不是 Main）就**按 itemId 扫描来源列表**：候选必须唯一，否则拒绝
    ///      —— 宁可让玩家重试，也绝不猜一件扣掉。回退时把原因写进计划的
    ///      <see cref="AuctionRegisterPlan.FallbackReason"/>，Handler 记日志。
    ///
    /// **价格**：起拍价 `-1` 表示客户端界面上「起拍价」留空，落库前归一为合法值；一口价 `-1` 是纯竞拍
    /// （表约束允许 `buyout_price = -1`）。两个价格上限都取
    /// <c>characters.auction_gold_limit</c>（默认 4 亿），与[数据库](../开发/数据库.md)的列默认值同源。
    ///
    /// **一次扣减**：整笔上架只从**一个槽**扣数量（与仓库/邮件的「单实例扣减」一致），
    /// 不跨堆补扣；扣完仍有余量的槽保留原 UID 与余量（拆堆上架）。
    ///
    /// **保证金**与道具扣减写在**同一份 <see cref="InventoryPlan"/>** 里（金币与背包槽同一次提交）：
    /// 余额不足直接拒绝（<see cref="AuctionRegisterError.InsufficientGold"/>，客户端可见码），
    /// 绝不会出现「挂牌成功但没收到保证金」或「钱扣了但挂牌没写上」。
    /// </summary>
    public AuctionRegisterDecision PlanRegister(Character character, AuctionRegisterRequest request, long now)
    {
        if (request.ItemTemplateId <= 0 || request.ItemCount <= 0 || request.BuyoutPrice < -1)
            return AuctionRegisterDecision.Reject(AuctionRegisterError.InvalidRequest);

        // ① 源槽定位（含按 itemId 扫描的回退路径）。定位不到或候选不唯一都拒绝。
        var source = Locate(character.Inventory, request, out var fallbackReason);
        if (source is null)
            return AuctionRegisterDecision.Reject(AuctionRegisterError.ItemNotFound);
        var item = source.Value.Item;
        if (item.Core.ItemId != request.ItemTemplateId)
            return AuctionRegisterDecision.Reject(AuctionRegisterError.ItemNotFound);

        // ② 数量：整件类只能是 1；堆叠类必须为正且不超过当前堆。
        int count = item.Core.IsStackable ? request.ItemCount : 1;
        if (count <= 0 || count > item.Core.Count)
            return AuctionRegisterDecision.Reject(AuctionRegisterError.InvalidRequest);

        // ③ 物品可上架判定。
        if (!TryResolveTemplate(item.Core, now, out _, out var blocked))
            return AuctionRegisterDecision.Reject(blocked);

        // ④ 价格：起拍价留空（-1）归一为「有一口价就用一口价，否则 1 金币起拍」。
        int buyout = request.BuyoutPrice;
        int starting = request.StartingPrice < 0 ? buyout > 0 ? buyout : 1 : request.StartingPrice;
        int limit = character.Inventory.AuctionGoldLimit;
        if (starting > limit || buyout > limit || buyout > 0 && starting > buyout)
            return AuctionRegisterDecision.Reject(AuctionRegisterError.PriceOutOfRange);

        // ⑤ 时长白名单 6/12/24/48；白名单外回落默认值（回落本身由调用方记日志）。
        var duration = AuctionDurationResolution.Resolve(request.DurationHours);

        // ⑥ 在售数量上限：纯 SELECT，查询失败按「查不到」放行（详述见 SellerListingCap）。
        if (store.CountSellerListings(character.Id, now) >= SellerListingCap)
            return AuctionRegisterDecision.Reject(AuctionRegisterError.ListingLimit);

        // ⑦ 扣减计划：复用背包模块的单实例数量规则，不另写一套扣减。
        if (!InventoryDeleteService.TryDelete(item, item.Core.Value, count, out var remaining))
            return AuctionRegisterDecision.Reject(AuctionRegisterError.ItemNotFound);
        var before = character.Inventory;
        var after = before with { Items = remaining is null ? before.Items.Remove(item) : before.Items.Replace(item, remaining) };
        // ⑧ 保证金：与道具扣减共用同一份 InventoryPlan ⇒ 同一个事务提交（用户口径：挂牌阶段扣）。
        //    余额不足在这里就拒绝：客户端拿到的是可见码（Generic = 0xd2），不会出现「界面显示上架成功、
        //    金币却没被收」这种半真状态。
        if (before.Gold < ListingDepositGold)
            return AuctionRegisterDecision.Reject(AuctionRegisterError.InsufficientGold);
        after = after with { Gold = before.Gold - (uint)ListingDepositGold };
        // 挂牌副本的数量：整件类沿用核心，堆叠类只取本次上架的数量（余量留在背包）。
        var core = item.Core.IsStackable ? item.Core with { Value = count } : item.Core;

        return AuctionRegisterDecision.Ok(new(
            Source: item.Position,
            SourceList: request.SourceList,
            SourceSlot: request.SourceSlot,
            ItemTemplateId: item.Core.ItemId,
            ItemKind: (byte)item.Core.Kind,
            ItemCount: count,
            ItemEntry: itemEntrySnapshot(core),
            StartingPrice: starting,
            BuyoutPrice: buyout,
            DurationHours: duration.Applied,
            PostedAtUnix: now,
            ExpiresAtUnix: checked(now + (long)duration.Applied * 3600),
            Inventory: new InventoryPlan(before, after, "auction_register", new(character)),
            FallbackReason: fallbackReason)
        {
            DepositGold = ListingDepositGold
        });
    }

    /// <summary>
    /// 提交上架：背包扣减 + 挂牌插入在**同一个外层事务**里（由 <see cref="IAuctionListingStore.RegisterListing"/> 拥有），
    /// 任一步失败则整体回滚，不留半成品。返回的 <c>Character</c> 是提交后的权威快照（含新 UID 与 revision）。
    /// </summary>
    public AuctionListingRegisterResult Register(AccountOwners.Lease owner, Character character, AuctionRegisterPlan plan) =>
        store.RegisterListing(owner, character, plan);

    // —— CMD 184 AUCTION_REGIST_CANCEL（取消上架） ——

    /// <summary>按挂牌 id 读一行（只读）：没有这一行时返回 null，由 <see cref="PlanCancel"/> 决定怎么拒绝。</summary>
    public AuctionListing? FindListing(int listingId) => listingId <= 0 ? null : store.LoadListing(listingId);

    /// <summary>
    /// **只读规划**取消上架：校验挂牌属于当前角色且在售，把挂牌保存的物品快照读回物品核心，
    /// 交给既有的发放入口（<see cref="InventoryRewardGrantService.TryGrantCore"/>）产出「道具还回背包」的库存计划。
    ///
    /// 三条不能省的判定：
    ///   ① **属于当前角色**（`seller_character_id`）—— 否则任何人都能取消别人的挂牌并把道具发给别人；
    ///   ② **仍在售**（`state = 0`）—— 已取消/已成交/已下架的行不能再取消一次，否则一件道具会被还两次；
    ///   ③ **背包放得下** —— 由现有发放入口判定（`Reject(17)`），失败时**不动挂牌状态**：
    ///      道具留在挂牌上（还能再取消），绝不出现「挂牌没了、道具也没回来」。
    ///
    /// 锁内不做资源解析之外的事：这里读模板目录、读一行挂牌（只读 SQL）与角色快照，
    /// 不写库、不发包、不等待网络；提交在 <see cref="Cancel"/>，事务由仓储拥有。
    /// </summary>
    public AuctionCancelDecision PlanCancel(Character character, AuctionListing? listing, AuctionCancelRequest request, long now)
    {
        if (request.ListingId <= 0)
            return AuctionCancelDecision.Reject(AuctionCancelError.InvalidRequest, "挂牌 id 非正");
        if (listing is null)
            return AuctionCancelDecision.Reject(AuctionCancelError.NotFound, $"挂牌 {request.ListingId} 不存在");
        if (listing.SellerCharacterId != character.Id)
            return AuctionCancelDecision.Reject(AuctionCancelError.NotOwner,
                $"挂牌 {listing.ListingId} 的卖家是 {(listing.SellerCharacterId?.ToString() ?? "NULL（机器人）")}，当前角色 {character.Id}");
        if (listing.State != AuctionListingState.Active)
            return AuctionCancelDecision.Reject(AuctionCancelError.AlreadyInactive,
                $"挂牌 {listing.ListingId} 的 state={listing.State}（0 = 在售）");
        ItemTemplate template;
        try
        {
            template = items.Get(listing.ItemTemplateId);
        }
        catch (Exception error)
        {
            // 解不出模板就**不能**把挂牌置为已下架（否则道具真的没了）：不论失败类型一律明确拒绝，
            // 失败类型记进 Detail 便于分辨是 PVF 缺失、坏条目还是目录未装配。
            return AuctionCancelDecision.Reject(AuctionCancelError.Unavailable,
                $"模板解析失败（{error.GetType().Name}）：{error.Message}");
        }
        // 挂牌表把物品种类单独存了一列（条目字节里没有它）：与模板对不上说明这行是坏的，
        // 此时不能动状态（道具会真的消失），明确按「数据不可用」拒绝。
        if ((byte)template.Kind != listing.ItemKind)
            return AuctionCancelDecision.Reject(AuctionCancelError.Unavailable,
                $"挂牌记录的物品种类 {listing.ItemKind} 与模板 {listing.ItemTemplateId} 的 {template.Kind} 不符");
        ItemCore core;
        try
        {
            core = itemCoreSnapshot(listing.ItemEntry, template.Kind);
        }
        catch (Exception error) when (error is InvalidDataException or ArgumentException or IndexOutOfRangeException or EndOfStreamException)
        {
            // 快照读不回来就**不能**把挂牌置为已下架：否则道具真的没了。
            return AuctionCancelDecision.Reject(AuctionCancelError.Unavailable, "挂牌物品快照不可用：" + error.Message);
        }
        var grant = InventoryRewardGrantService.TryGrantCore(character, core, template, pieces, DateTimeOffset.FromUnixTimeSeconds(now));
        if (grant.Plan is null)
        {
            // 复用背包模块自己的判定（背包满、格子不可用、实例不可回存…），不在这里另写一套放置规则。
            return AuctionCancelDecision.Reject(AuctionCancelError.InventoryRejected,
                $"背包拒绝放回（错误码 {grant.Error}）：模板 {listing.ItemTemplateId} × {core.Count}");
        }
        // 审计动作名与上架对应（inventory_audit_log.action_name），并带上规划资格：写事务内还会再核一次。
        var inventory = new InventoryPlan(grant.Plan.Before, grant.Plan.After, "auction_cancel", new(character));
        return AuctionCancelDecision.Ok(new(listing.ListingId, listing.ItemTemplateId, listing.ItemKind,
            listing.ItemCount, core.Count, listing.ExpiresAtUnix, inventory));
    }

    /// <summary>
    /// 提交取消上架：挂牌置为已下架 + 道具还回背包 + 背包审计在**同一个外层事务**里
    /// （由 <see cref="IAuctionListingStore.CancelListing"/> 拥有），任一步失败整体回滚，
    /// 返回的 <c>Character</c> 是提交后的权威快照。
    /// </summary>
    public AuctionListingCancelResult Cancel(AccountOwners.Lease owner, Character character, AuctionCancelPlan plan) =>
        store.CancelListing(owner, character, plan);

    // —— CMD 185 AUCTION_BIDDING（一口价购买，跨账号钱货转移）——

    /// <summary>
    /// 一口价成交的**卖方侧**税率（百分比）。界面上的 5% 取自实机截图，参考实现（另一客户端版本）里
    /// 也是 `SaleTaxRate = 0.05` 且**从卖家所得里抽**；本仓库因此按「卖方实收 = floor(成交价 × 95%)、
    /// 买方只付成交价」实现。
    /// ⚠ **仍是待确认项**：界面那 5% 到底记在卖方头上还是买方服务费，只有让另一个账号买走自己小号的
    /// 一件挂牌、比对买方扣款与卖方到账才能分辨（见文档的「待确认项的确认方法」）。
    /// 这里不写死第二份费率、也不加列：费率是本模块唯一的政策常数，成交价与实收都进日志，便于事后核对。
    /// </summary>
    public const int SaleTaxPercent = 5;

    /// <summary>托管邮件的系统发件人名（卖方所得那封由拍卖行自己发，不是卖家发给卖家）。</summary>
    public const string SystemSender = "拍卖行";

    /// <summary>卖方实收 = `floor(成交价 × (100 - 税率) / 100)`。整数运算（先乘后除）等于向下取整，不引入浮点。</summary>
    public static long SellerProceeds(long totalPrice) => totalPrice * (100 - SaleTaxPercent) / 100;

    /// <summary>
    /// 买方道具托管邮件的幂等键。按挂牌 id 命名：它与挂牌状态在同一事务提交，正常路径不可能重复；
    /// 真出现重复投递时由 <c>mailbox_messages.delivery_key</c> 的 UNIQUE 约束直接拒绝，而不是投两份。
    /// </summary>
    public static string CustodyItemKey(int listingId) => $"auction:item:{listingId}";

    /// <summary>卖方所得托管邮件的幂等键（同 <see cref="CustodyItemKey"/> 的口径，金币一封、道具一封）。</summary>
    public static string CustodyGoldKey(int listingId) => $"auction:gold:{listingId}";

    /// <summary>
    /// **只读规划**一口价购买：把成交所需的一切算出来并留下证据。
    ///
    /// 成交价**一律取库里的 `buyout_price × item_count`**（`long` 计算），绝不按请求值结算；
    /// 请求金额只是**校验输入**，且校验用保守口径：`[1..4]` 必须等于库里的**单价**或**总额**之一
    /// （命中哪一个写进 <see cref="AuctionBuyPlan.AmountBasis"/>：两个实机样本的数量都是 1，
    /// 单价与总额同值 ⇒ 现有样本区分不了，第三个样本（数量 &gt; 1 的挂牌）一次判定）。
    ///
    /// 锁内不做资源解析之外的事：这里读模板目录（进程内只读缓存）与传入的角色快照，
    /// 不写库、不发包、不等待网络；提交在 <see cref="Buy"/>，事务由仓储拥有。
    ///
    /// **购买阶段不检查买方背包容量**：道具进托管邮件，容量判定发生在**领取**时（邮件模块既有规则）。
    /// 理由是硬性的 —— 185 回调会本地乐观扣一次金币，若购买时直接入包就必须补发含金币槽的背包差量，
    /// 那会被客户端叠加成「再扣一次」（参考实现实测：余额不足时界面显示「余额清空」）。
    /// </summary>
    public AuctionBuyDecision PlanBuy(Character buyer, AuctionListing? listing, AuctionBuyRequest request, long now)
    {
        if (request.ListingId <= 0 || request.Amount <= 0)
            return AuctionBuyDecision.Reject(AuctionBuyError.InvalidRequest, $"挂牌 id {request.ListingId}、金额 {request.Amount}");
        if (listing is null)
            return AuctionBuyDecision.Reject(AuctionBuyError.NotFound, $"挂牌 {request.ListingId} 不存在");
        if (listing.State != AuctionListingState.Active)
            return AuctionBuyDecision.Reject(AuctionBuyError.NotActive, $"挂牌 {listing.ListingId} 的 state={listing.State}（0 = 在售）");
        if (listing.ExpiresAtUnix <= now)
            return AuctionBuyDecision.Reject(AuctionBuyError.Expired, $"挂牌 {listing.ListingId} 已于 {listing.ExpiresAtUnix} 过期（now={now}）");
        // 一口价 -1 = 纯竞拍：竞价不实现，绝不能按一口价把纯竞拍的挂牌卖掉。
        if (listing.BuyoutPrice < 0)
            return AuctionBuyDecision.Reject(AuctionBuyError.AuctionOnly, $"挂牌 {listing.ListingId} 的一口价是 {listing.BuyoutPrice}（-1 = 纯竞拍）");
        // 买自己的挂牌：对应白名单里的 0x69（参考实现里该码就是这条语义，D95 未实测其文本）。
        if (listing.SellerCharacterId == buyer.Id)
            return AuctionBuyDecision.Reject(AuctionBuyError.SelfBuy, $"挂牌 {listing.ListingId} 的卖家就是当前角色 {buyer.Id}");
        // [26..29] 与库内的物品模板 id 交叉校验：不一致说明客户端窗口里那行与库里不是同一件东西。
        if (listing.ItemTemplateId != request.ItemTemplateId)
            return AuctionBuyDecision.Reject(AuctionBuyError.TemplateMismatch,
                $"请求里的模板 id {request.ItemTemplateId} 与挂牌 {listing.ListingId} 的 {listing.ItemTemplateId} 不符");

        // 模板 + 条目快照：读不回来就拒绝（拿不到要托管的物品核心，成交就落不了地）。
        ItemTemplate template;
        try
        {
            template = items.Get(listing.ItemTemplateId);
        }
        catch (Exception error)
        {
            return AuctionBuyDecision.Reject(AuctionBuyError.Unavailable, $"模板解析失败（{error.GetType().Name}）：{error.Message}");
        }
        if ((byte)template.Kind != listing.ItemKind)
            return AuctionBuyDecision.Reject(AuctionBuyError.Unavailable,
                $"挂牌记录的物品种类 {listing.ItemKind} 与模板 {listing.ItemTemplateId} 的 {template.Kind} 不符");
        ItemCore core;
        try
        {
            core = itemCoreSnapshot(listing.ItemEntry, template.Kind);
        }
        catch (Exception error) when (error is InvalidDataException or ArgumentException or IndexOutOfRangeException or EndOfStreamException)
        {
            return AuctionBuyDecision.Reject(AuctionBuyError.Unavailable, "挂牌物品快照不可用：" + error.Message);
        }
        // 托管邮件只能装普通实体：钱包货币与史诗碎片走账号钱包，不能借邮件托管（与邮箱投递同一口径）。
        if (core.ItemId != listing.ItemTemplateId || core.Kind != template.Kind || core.Count <= 0 ||
            core.Count != listing.ItemCount || core.Count > template.StackLimit || core.IsExpired(now) ||
            CurrencyWallet.RewardKind(core.ItemId) is not null || pieces.ByPiece.ContainsKey(core.ItemId))
        {
            return AuctionBuyDecision.Reject(AuctionBuyError.Unavailable,
                $"挂牌物品核心不可托管：itemId={core.ItemId} kind={core.Kind} count={core.Count}（挂牌数量 {listing.ItemCount}、上限 {template.StackLimit}）");
        }

        // 成交价：库里的单价 × 数量，long 计算；客户端搜索记录里该字段已被夹到 int.MaxValue，
        // 因此结果超出它视为数据异常 —— 不能拿一个客户端根本显示不出的数成交。
        long unitPrice = listing.BuyoutPrice;
        long totalPrice = unitPrice * listing.ItemCount;
        if (totalPrice <= 0 || totalPrice > int.MaxValue)
            return AuctionBuyDecision.Reject(AuctionBuyError.Unavailable, $"成交价溢出或非法：{unitPrice} × {listing.ItemCount} = {totalPrice}");

        // 金额校验（保守口径）：命中单价或总额之一即接受，命中的是哪一个写进日志；两个都不等 ⇒ 拒绝。
        bool unitHit = request.Amount == unitPrice;
        bool totalHit = request.Amount == totalPrice;
        if (!unitHit && !totalHit)
            return AuctionBuyDecision.Reject(AuctionBuyError.AmountMismatch,
                $"请求金额 {request.Amount} 既不是单价 {unitPrice} 也不是总额 {totalPrice}（数量 {listing.ItemCount}）");
        var basis = unitHit && totalHit ? AuctionBuyAmountBasis.UnitEqualsTotal
            : unitHit ? AuctionBuyAmountBasis.UnitPrice : AuctionBuyAmountBasis.TotalPrice;

        // 买方扣款：复用背包提交器（revision 复核与审计同一路径），绝不写裸 SQL 改金币列。
        if ((long)buyer.Inventory.Gold < totalPrice)
            return AuctionBuyDecision.Reject(AuctionBuyError.InsufficientGold,
                $"买方 {buyer.Id} 金币 {buyer.Inventory.Gold} < 成交价 {totalPrice}");

        // 托管邮件：买方道具一封（必须投出去）+ 卖方所得一封（收件人可能不存在 ⇒ 可跳过）。
        var custody = ImmutableArray.CreateBuilder<AuctionCustodyMail>();
        var itemText = $"拍卖行成交（挂牌 {listing.ListingId}）：您拍得的道具";
        var overlong = AuctionCustodyMail.TextTooLong(listing.SellerName, itemText);
        if (overlong is not null)
            return AuctionBuyDecision.Reject(AuctionBuyError.Unavailable, overlong);
        custody.Add(new(CustodyItemKey(listing.ListingId), buyer.Id, Required: true, listing.SellerName,
            itemText, 0, [InventoryRewardGrantService.CreateItem(template, new(InventoryListType.Main, 0), core)], now, null));
        long proceeds = SellerProceeds(totalPrice);
        if (listing.SellerCharacterId is { } sellerId)
        {
            var goldText = $"拍卖行成交（挂牌 {listing.ListingId}）：卖方所得（已扣 {SaleTaxPercent}% 手续费）";
            overlong = AuctionCustodyMail.TextTooLong(SystemSender, goldText);
            if (overlong is not null)
                return AuctionBuyDecision.Reject(AuctionBuyError.Unavailable, overlong);
            custody.Add(new(CustodyGoldKey(listing.ListingId), sellerId, Required: false, SystemSender,
                goldText, checked((uint)proceeds), [], now, null));
        }

        var before = buyer.Inventory;
        var after = before with { Gold = checked(before.Gold - (uint)totalPrice) };
        return AuctionBuyDecision.Ok(new(
            ListingId: listing.ListingId,
            ItemTemplateId: listing.ItemTemplateId,
            ItemKind: listing.ItemKind,
            ItemCount: listing.ItemCount,
            SellerCharacterId: listing.SellerCharacterId,
            SellerName: listing.SellerName,
            UnitPrice: checked((int)unitPrice),
            TotalPrice: totalPrice,
            SellerProceeds: proceeds,
            AmountBasis: basis,
            RequestedAmount: request.Amount,
            RequestExpiresAtUnix: request.ExpiresAtUnix,
            ExpiresEchoMatches: listing.ExpiresAtUnix == request.ExpiresAtUnix,
            Custody: custody.ToImmutable(),
            Inventory: new InventoryPlan(before, after, "auction_buy", new(buyer)),
            SettledAtUnix: now,
            ExpiresAtUnix: listing.ExpiresAtUnix));
    }

    /// <summary>
    /// 提交一口价购买：买方扣款 + 挂牌置为已成交 + 两封托管邮件在**同一个外层事务**里
    /// （由 <see cref="IAuctionListingStore.BuyListing"/> 拥有），任一步失败整体回滚，不留半成品。
    ///
    /// **只在这里把条件更新 0 行翻成静默码**：<see cref="AuctionBuyRaceLostException"/> 意味着挂牌
    /// 已被别人买走或本次是重复点击，事务已经回滚（零副作用），因此可以直接回 `0x7e`；
    /// 其它异常（SQL 失败、revision 冲突、托管投递失败）照旧向上抛，由协议层按既有约定处理
    /// —— 写库失败绝不能编一个可能被客户端当成成功的码。
    /// </summary>
    public AuctionListingBuyResult Buy(AccountOwners.Lease owner, Character character, AuctionBuyPlan plan)
    {
        try
        {
            return store.BuyListing(owner, character, plan);
        }
        catch (AuctionBuyRaceLostException error)
        {
            return AuctionListingBuyResult.Reject(AuctionBuyError.RaceLost, error.Message);
        }
    }

    // —— 到期结算（到期未售：道具退回 + 保证金退还）——

    /// <summary>
    /// 到期退回邮件的幂等键。与 <see cref="CustodyItemKey"/> / <see cref="CustodyGoldKey"/> 同一口径：
    /// 一封邮件同时装道具与退还的保证金，因此只需要一个键；重复结算会被 `mailbox_messages.delivery_key`
    /// 的唯一约束挡下，而不是退两份。
    /// </summary>
    public static string ReturnKey(int listingId) => $"auction:return:{listingId}";

    /// <summary>
    /// **只读规划**到期结算：把该卖家所有「已到期且仍在售」的挂牌算成「一次状态流转 + 一封退回邮件」。
    ///
    /// 三条口径：
    ///   ① **只结算本人的行**（`seller_character_id = 本人`，由仓库侧过滤）：机器人挂牌没有可退的对象；
    ///   ② **解不出模板就不结算这一行**（沿用取消上架的规则）：状态不流转、道具不丢，留在 `state=0`
    ///      等下次访问重试，并记进 <see cref="AuctionExpiryPlan.Skipped"/>；
    ///   ③ **保证金按这一行实际收过的金额退**（`auction_listings.deposit_gold`，用户口径：到期未售退还；
    ///      取消上架不退 —— 那条路径不产生任何退款）。历史行（本列出现之前的挂牌）记 0，
    ///      因此它们只退道具、不退钱：**绝不用当前常数去退一笔没交过的保证金**。
    ///
    /// 锁内不做资源解析之外的事：只读挂牌行与模板目录，不写库、不发包、不等待网络；提交在
    /// <see cref="SettleExpired"/>。
    /// </summary>
    public AuctionExpiryPlan PlanExpire(Character seller, long now)
    {
        if (seller.Id <= 0)
            return AuctionExpiryPlan.None;
        var expired = store.LoadExpiredListings(seller.Id, now);
        if (expired.IsDefaultOrEmpty)
            return AuctionExpiryPlan.None;
        var rows = ImmutableArray.CreateBuilder<AuctionExpiryRow>(expired.Length);
        var skipped = ImmutableArray.CreateBuilder<string>();
        foreach (var listing in expired)
        {
            ItemTemplate template;
            try
            {
                template = items.Get(listing.ItemTemplateId);
            }
            catch (Exception error)
            {
                skipped.Add($"挂牌 {listing.ListingId}：模板解析失败（{error.GetType().Name}）");
                continue;
            }
            if ((byte)template.Kind != listing.ItemKind)
            {
                skipped.Add($"挂牌 {listing.ListingId}：物品种类 {listing.ItemKind} 与模板 {listing.ItemTemplateId} 的 {template.Kind} 不符");
                continue;
            }
            ItemCore core;
            try
            {
                core = itemCoreSnapshot(listing.ItemEntry, template.Kind);
            }
            catch (Exception error) when (error is InvalidDataException or ArgumentException or IndexOutOfRangeException or EndOfStreamException)
            {
                skipped.Add($"挂牌 {listing.ListingId}：条目快照解不出物品核心（{error.GetType().Name}）");
                continue;
            }
            string text = listing.DepositGold > 0
                ? $"拍卖行到期未售（挂牌 {listing.ListingId}）：道具已退回，保证金 {listing.DepositGold} 金币已退还"
                : $"拍卖行到期未售（挂牌 {listing.ListingId}）：道具已退回";
            if (AuctionCustodyMail.TextTooLong(SystemSender, text) is { } overlong)
            {
                skipped.Add($"挂牌 {listing.ListingId}：{overlong}");
                continue;
            }
            rows.Add(new AuctionExpiryRow(listing.ListingId, listing.ItemTemplateId,
                new AuctionCustodyMail(ReturnKey(listing.ListingId), seller.Id, Required: true, SystemSender, text,
                    checked((uint)listing.DepositGold),
                    [InventoryRewardGrantService.CreateItem(template, new(InventoryListType.Main, 0), core)],
                    now, null)));
        }
        return new AuctionExpiryPlan(seller.Id, rows.ToImmutable(), skipped.ToImmutable(), now);
    }

    /// <summary>
    /// 提交到期结算：挂牌置为已过期 + 退回邮件在**同一个外层事务**里（由
    /// <see cref="IAuctionListingStore.SettleExpiredListings"/> 拥有），任一步失败整体回滚。
    /// </summary>
    public AuctionExpiryResult SettleExpired(AccountOwners.Lease owner, Character character, AuctionExpiryPlan plan) =>
        store.SettleExpiredListings(owner, character, plan);

    /// <summary>
    /// 源槽定位。返回的 `Fallback` 表示「客户端给的列表类型/槽位没采信，走了按 itemId 扫描」。
    /// **只有 Main 容器参与上架**：上架窗口只从上架页取道具，仓库/穿戴/时装容器需要先取回主背包
    /// （与「账号仓库物品需先取出」等既有约定一致）。
    /// </summary>
    (InventoryItem Item, bool Fallback)? Locate(InventorySnapshot inventory, AuctionRegisterRequest request, out string? fallbackReason)
    {
        fallbackReason = null;
        if (request.SourceList == (byte)InventoryListType.Main)
        {
            var slot = new InventorySlot(InventoryListType.Main, unchecked((short)request.SourceSlot));
            if (inventory.Find(slot) is { } direct && direct.Core.ItemId == request.ItemTemplateId)
                return (direct, false);
            // 推断的槽位不可信：不按它扣道具，落到下面的扫描路径并说明原因。
            fallbackReason = inventory.Find(slot) is { } occupant
                ? $"客户端槽位线索指向槽 {request.SourceSlot}，但该槽是 itemId {occupant.Core.ItemId}，与请求的 {request.ItemTemplateId} 不符"
                : $"客户端槽位线索指向空槽 {request.SourceSlot}（列表类型 {request.SourceList}）";
        }
        else
        {
            fallbackReason = $"客户端源列表类型 {request.SourceList} 不是主背包（推断字段，未采信）";
        }

        InventoryItem? found = null;
        int matches = 0;
        foreach (var candidate in inventory.Items)
        {
            if (candidate.Position.List != InventoryListType.Main || candidate.Core.ItemId != request.ItemTemplateId)
                continue;
            matches++;
            found ??= candidate;
        }
        if (matches == 0)
            return null;
        if (matches > 1)
        {
            // 同模板多堆：无法判断玩家拖的是哪一堆，宁可拒绝也不能猜（扣错道具不可逆）。
            fallbackReason += $"；主背包里同模板有 {matches} 处，无法唯一确定来源，拒绝（请把要上架的那一堆单独放置后重试）";
            return null;
        }
        return (found!, true);
    }

    /// <summary>
    /// 模板解析 + 可上架判定。判定口径与邮箱/仓库共用同一套物品模块字段（`TradeRestriction`、`SealFlag`、
    /// `Attr` 的高 3 位交易次数、`AttachType`、租赁标记、`ExpireTime`），**不新建第二套可交易判定**：
    ///   - 已锁定（排序锁 / 装备安全锁）拒绝 —— 锁定的实例不允许离开背包；
    ///   - 已到期或**带期限**（租赁 / `ExpireTime > 0`）拒绝 —— 计时道具不能挂到拍卖行；
    ///   - `TradeRestriction != 0` 拒绝 —— 包含任务道具与各类绑定实例（PVF 的绑定标记落到这个字段）；
    ///   - `AttachType` 非 `free`：`trade limit` 要求还有剩余交易次数、`sealing`/`seal` 要求已解封、
    ///     `account` 等账号绑定类**一律拒绝**（拍卖行必然跨账号）。
    /// 判定失败返回具体原因，供调用方映射成客户端白名单错误码。
    /// </summary>
    bool TryResolveTemplate(ItemCore core, long now, out ItemTemplate template, out AuctionRegisterError error)
    {
        template = null!;
        error = AuctionRegisterError.None;
        try
        {
            template = items.Get(core.ItemId);
        }
        catch (Exception failure) when (failure is InvalidDataException or FileNotFoundException or KeyNotFoundException)
        {
            // 目录里没有这个模板：不能凭空判断它可交易，明确拒绝并让上层记日志。
            error = AuctionRegisterError.ItemNotFound;
            return false;
        }
        // 账号钱包货币与史诗碎片是虚拟资产（不是可上架实体），与搜索路径排除它们的口径一致。
        if (CurrencyWallet.RewardKind(core.ItemId) is not null || pieces.ByPiece.ContainsKey(core.ItemId))
        {
            error = AuctionRegisterError.ItemNotTradable;
            return false;
        }
        if (template.Id != core.ItemId || template.Kind != core.Kind)
        {
            error = AuctionRegisterError.ItemNotTradable;
            return false;
        }
        if (core.SortLockFlag != 0 || core.EquipmentLockId != 0 ||
            core.IsExpired(now) || core.IsRental || core.ExpireTime > 0 || core.TradeRestriction != 0)
        {
            error = AuctionRegisterError.ItemNotTradable;
            return false;
        }
        if (TransferBlocked(core, template) is not null)
        {
            error = AuctionRegisterError.ItemNotTradable;
            return false;
        }
        return true;
    }

    /// <summary>
    /// `AttachType` 声明的交易限制：可交易返回 null，否则返回拒绝原因。
    /// 与邮箱寄送的判定同源（`[trade limit]` 按剩余交易次数、`[sealing]` 按封装标志），
    /// 唯一差别是账号绑定类在这里**一律拒绝**（拍卖行不可能是同账号之间的转移）。
    /// </summary>
    public static string? TransferBlocked(ItemCore core, ItemTemplate template)
    {
        string attach = NormalizeAttachType(template.AttachType);
        return attach switch
        {
            "free" => null,
            TradeLimit => core.Attr >> 5 != 0 ? null : "交易次数已用尽",
            "sealing" or "seal" => core.SealFlag != 0 ? null : "未解封（封装状态不可交易）",
            _ when attach.Contains("account", StringComparison.Ordinal) => "账号绑定道具不能上架",
            _ => "该道具的交易类型不允许上架"
        };
    }

    /// <summary>把 PVF 的 `[free]` / `[trade limit]` 之类的书写归一成小写裸词（与邮箱模块同一套写法）。</summary>
    static string NormalizeAttachType(string value) => value.Replace("`", "").Trim().Trim('[', ']').Trim().ToLowerInvariant();

    /// <summary>
    /// 按本机 PVF 解析种子候选物品，并调用物品模块的编码入口生成 123B 条目字节快照。
    /// 不同 PVF 版本可能没有同名物品：缺失的跳过并通过 <paramref name="unresolvedItemIds"/> 回报给启动日志，
    /// 不用默认值伪造一件物品；全部缺失时由 <see cref="Seed"/> 明确失败。
    /// </summary>
    public static ImmutableArray<AuctionSeedItem> ResolveSeedItems(ItemCatalog items, Func<ItemCore, byte[]> itemEntrySnapshot,
        out ImmutableArray<int> unresolvedItemIds)
    {
        var resolved = ImmutableArray.CreateBuilder<AuctionSeedItem>();
        var unresolved = ImmutableArray.CreateBuilder<int>();
        foreach (int itemId in SeedItemIds)
        {
            try
            {
                var template = items.Get(itemId);
                // 装备/时装/宠物/公会勋章只能整件：Create 对它们要求数量为 1。
                int stack = template.Kind is ItemKind.Equipment or ItemKind.Avatar or ItemKind.Creature or
                    ItemKind.CreatureEquipment or ItemKind.GuildMedal ? 1 : Math.Min(SeedStackCount, template.StackLimit);
                var core = InventoryRewardGrantService.Create(template, stack);
                resolved.Add(new(itemId, (byte)core.Kind, core.Count, itemEntrySnapshot(core)));
            }
            catch (Exception error) when (error is InvalidDataException or FileNotFoundException or KeyNotFoundException)
            {
                unresolved.Add(itemId);
            }
        }
        unresolvedItemIds = unresolved.ToImmutable();
        return resolved.ToImmutable();
    }

    /// <summary>
    /// 开发用种子（<c>--auction-seed</c>）：表内已有挂牌时不写入，避免每次启动重复堆数据。
    ///
    /// 取值固定，便于实机核对：第 n 行（从 0 起）起拍价 12000 + 1000×n、每第三行一口价为 -1（纯竞拍）、
    /// 其余一口价为起拍价两倍、到期时间统一为当前时间 + 24 小时，卖家与物品按上面的顺序轮换。
    /// </summary>
    public AuctionSeedResult Seed(int count, long now, IReadOnlyList<AuctionSeedItem> items)
    {
        if (count <= 0 || store.CountListings() != 0)
            return new(0, []);
        if (items.Count == 0)
            throw new InvalidDataException("--auction-seed 在本机 PVF 里解析不到任何候选物品");
        var drafts = ImmutableArray.CreateBuilder<AuctionListingDraft>(count);
        for (int index = 0; index < count; index++)
        {
            var item = items[index % items.Count];
            int starting = SeedStartingPrice + index * SeedPriceStep;
            drafts.Add(new(null, SeedSellers[index % SeedSellers.Length], item.ItemTemplateId, item.ItemKind, item.Count,
                item.Entry, starting, index % 3 == 2 ? -1 : starting * 2, now, now + SeedDurationSeconds));
        }
        store.InsertListings(drafts.ToImmutable());
        return new(drafts.Count, drafts.Select(draft => draft.ItemTemplateId).ToImmutableArray());
    }
}
