using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Auction;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

/// <summary>
/// 拍卖行挂牌表的 SQL。只负责插入、按条件分页查询与计数：分页大小、排除自己、价格展示等业务规则在
/// <see cref="AuctionService"/>；本类不解释 item_entry 的字段，也不构造包体。
/// 复用 <see cref="SqliteGameDatabase"/> 建立的连接设置，不另建连接字符串或数据库文件。
///
/// <see cref="IAuctionListingStore"/> 的实现方是 <see cref="CharacterRepository"/>：上架提交要与背包扣减
/// 共用同一个写事务，而写背包的唯一实现入口在角色仓储。本类因此只提供 SQL 与单行插入入口，
/// 不自己实现那份合同（否则两个仓储会互相构造）。
/// </summary>
internal sealed class AuctionListingRepository(string path)
{
    // state 放在最后：只读路径的字段位置因此与之前完全一致，取消上架（CMD 184）需要它来判「仍在售」。
    const string Columns = "listing_id,seller_character_id,seller_name,item_template_id,item_kind,item_count,item_entry," +
        "starting_price,buyout_price,current_bid,posted_at_unix,expires_at_unix,state,deposit_gold";

    readonly SqliteGameDatabase database = new(path);

    public AuctionSearchPage Search(AuctionSearchFilter filter, long now, int offset, int limit)
    {
        // 计数与分页放在同一读事务里，避免两批挂牌在两次查询之间到期导致「总数」与「本批」互相对不上。
        using var db = database.Open();
        using var tx = db.BeginRead();
        int total;
        using (var count = db.CreateCommand())
        {
            count.Transaction = tx;
            count.CommandText = $"SELECT count(*) FROM auction_listings WHERE {Filter(filter, count, now)}";
            total = checked((int)(long)count.ExecuteScalar()!);
        }
        var rows = ImmutableArray.CreateBuilder<AuctionListing>();
        using (var query = db.CreateCommand())
        {
            query.Transaction = tx;
            query.CommandText = $"SELECT {Columns} FROM auction_listings WHERE {Filter(filter, query, now)} " +
                "ORDER BY listing_id LIMIT $limit OFFSET $offset";
            query.Parameters.AddWithValue("$limit", limit >= 0 ? limit : 0);
            query.Parameters.AddWithValue("$offset", offset >= 0 ? offset : 0);
            using var reader = query.ExecuteReader();
            while (reader.Read())
                rows.Add(Read(reader));
        }
        tx.Commit();
        return new(rows.ToImmutable(), total);
    }

    /// <summary>
    /// 同一套过滤条件下的全部匹配挂牌。与 <see cref="Search"/> 的区别只有「不分页」：
    /// 带装备部位的分类要先在内存里过滤再分页，因此必须先拿到全量（见 <see cref="AuctionService.Search"/>）。
    /// `item_entry` 也一并读回：内存过滤可能丢掉部分行，提前截断页会在过滤后留下空页。
    /// </summary>
    public ImmutableArray<AuctionListing> LoadMatches(AuctionSearchFilter filter, long now)
    {
        using var db = database.Open();
        using var query = db.CreateCommand();
        query.CommandText = $"SELECT {Columns} FROM auction_listings WHERE {Filter(filter, query, now)} ORDER BY listing_id";
        var rows = ImmutableArray.CreateBuilder<AuctionListing>();
        using var reader = query.ExecuteReader();
        while (reader.Read())
            rows.Add(Read(reader));
        return rows.ToImmutable();
    }

    public int CountListings()
    {
        using var db = database.Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT count(*) FROM auction_listings";
        return checked((int)(long)command.ExecuteScalar()!);
    }

    /// <summary>
    /// 某个卖家自己的挂牌（CMD 188 / 190），按 <c>listing_id</c> 稳定排序、不分页。
    /// 走 <c>auction_listings_seller</c> 索引；`includeInactive` 为假时只取在售未过期（与搜索的在售口径一致），
    /// 为真时取该卖家的全部状态（190 的历史）。**不读 state 列**：两个面板的客户端行结构里没有它，
    /// 行的取舍完全由这里的 WHERE 决定。
    /// </summary>
    public ImmutableArray<AuctionListing> LoadSellerListings(long sellerCharacterId, long now, bool includeInactive)
    {
        using var db = database.Open();
        using var query = db.CreateCommand();
        query.CommandText = includeInactive
            ? $"SELECT {Columns} FROM auction_listings WHERE seller_character_id=$seller ORDER BY listing_id"
            : $"SELECT {Columns} FROM auction_listings WHERE seller_character_id=$seller AND state=0 AND expires_at_unix>$now ORDER BY listing_id";
        query.Parameters.AddWithValue("$seller", sellerCharacterId);
        if (!includeInactive)
            query.Parameters.AddWithValue("$now", now);
        var rows = ImmutableArray.CreateBuilder<AuctionListing>();
        using var reader = query.ExecuteReader();
        while (reader.Read())
            rows.Add(Read(reader));
        return rows.ToImmutable();
    }

    /// <summary>
    /// 某卖家在售且未过期的挂牌数（上架数量上限）。条件与 <see cref="Search"/> 的在售口径一致，
    /// 走 <c>auction_listings_seller</c> 索引，只发一条纯 SELECT：不申请 writer、不建行、不补默认值。
    /// </summary>
    public int CountSellerListings(long sellerCharacterId, long now)
    {
        using var db = database.Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT count(*) FROM auction_listings WHERE seller_character_id=$seller AND state=0 AND expires_at_unix>$now";
        command.Parameters.AddWithValue("$seller", sellerCharacterId);
        command.Parameters.AddWithValue("$now", now);
        return checked((int)(long)command.ExecuteScalar()!);
    }

    /// <summary>
    /// **本类内部**的单行插入入口：<see cref="InsertListings"/>（开发种子，整批一次提交）与
    /// <see cref="CharacterRepository.RegisterListing"/>（背包扣减 + 挂牌同事务）共用同一条语句与同一份校验，
    /// 避免「种子能过、上架过不了」这类只在实机上暴露的不一致。
    /// </summary>
    internal static void InsertListing(SqliteConnection db, SqliteTransaction transaction, AuctionListingDraft listing)
    {
        Validate(listing);
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO auction_listings(seller_character_id,seller_name,item_template_id,item_kind,item_count,item_entry,
              starting_price,buyout_price,current_bid,state,posted_at_unix,expires_at_unix,deposit_gold)
            VALUES($seller,$name,$template,$kind,$count,$entry,$starting,$buyout,0,0,$posted,$expires,$deposit)
            """;
        command.Parameters.AddWithValue("$seller", (object?)listing.SellerCharacterId ?? DBNull.Value);
        command.Parameters.AddWithValue("$name", listing.SellerName);
        command.Parameters.AddWithValue("$template", listing.ItemTemplateId);
        command.Parameters.AddWithValue("$kind", listing.ItemKind);
        command.Parameters.AddWithValue("$count", listing.ItemCount);
        command.Parameters.AddWithValue("$entry", listing.ItemEntry);
        command.Parameters.AddWithValue("$starting", listing.StartingPrice);
        command.Parameters.AddWithValue("$buyout", listing.BuyoutPrice);
        command.Parameters.AddWithValue("$posted", listing.PostedAtUnix);
        command.Parameters.AddWithValue("$expires", listing.ExpiresAtUnix);
        // 保证金逐行落库（历史行为 0）：到期结算按这一列退，而不是按当前政策常数退。
        command.Parameters.AddWithValue("$deposit", listing.DepositGold);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// 一行挂牌的合法性（表约束与业务约束的交集）。批量插入与单笔上架提交**共用同一份判定**：
    /// 上架路径若自己写一套，就会出现「种子能过、上架过不了」这类只在实机上暴露的不一致。
    /// </summary>
    internal static void Validate(AuctionListingDraft listing)
    {
        if (string.IsNullOrWhiteSpace(listing.SellerName) || listing.ItemEntry.Length == 0 ||
            listing.ItemCount < 1 || listing.StartingPrice < 0 || listing.BuyoutPrice < -1 ||
            listing.ExpiresAtUnix < listing.PostedAtUnix)
            throw new InvalidDataException("拍卖行挂牌数据无效");
    }

    public void InsertListings(ImmutableArray<AuctionListingDraft> listings)
    {
        if (listings.IsDefaultOrEmpty)
            return;
        using var db = database.Open();
        using var write = db.BeginWrite("AuctionListingRepository.InsertListings");
        using var tx = write.Transaction;
        // 一条挂牌一行，整批一次提交：种子要么全在要么全不在，不留半批数据。
        // 任一行非法 → 抛异常 → 事务未提交 → 已插入的行全部回滚。
        foreach (var listing in listings)
            InsertListing(db, tx, listing);
        tx.Commit();
    }

    /// <summary>
    /// 上架提交：**背包扣减与新挂牌在同一个外层事务里**，由角色仓储调用
    /// （<see cref="CharacterRepository.RegisterListing"/>，那是 <see cref="IAuctionListingStore"/> 的实现方）。
    ///
    /// 为什么必须同事务（而不是先提交背包、再单独插一行）：SQLite 同一个文件只有一个 writer，
    /// 分两次提交会出现「背包扣了、挂牌没写上」的半成品，而拍卖行上架是**会改背包资产**的操作，
    /// 半成品只能靠人工补偿。这里沿用参考实现同一口径（扣道具 + 写挂牌 + 审计一次提交）：
    /// 事务由调用方拥有，背包写入只参与、不提交；挂牌插入失败时抛异常往上走，事务未提交
    /// ⇒ 背包、审计、revision 与挂牌表一起回滚。
    /// </summary>
    internal static void InsertListing(SqliteConnection db, SqliteTransaction transaction, AuctionRegisterPlan plan, Character character)
    {
        InsertListing(db, transaction, new AuctionListingDraft(
            SellerCharacterId: character.Id,
            SellerName: character.Name,
            ItemTemplateId: plan.ItemTemplateId,
            ItemKind: plan.ItemKind,
            ItemCount: plan.ItemCount,
            ItemEntry: plan.ItemEntry,
            StartingPrice: plan.StartingPrice,
            BuyoutPrice: plan.BuyoutPrice,
            PostedAtUnix: plan.PostedAtUnix,
            ExpiresAtUnix: plan.ExpiresAtUnix)
        {
            // 保证金随挂牌一起落库：到期结算按这一行的金额退，而不是按那时的政策常数。
            DepositGold = plan.DepositGold
        });
    }

    /// <summary>
    /// 在售且未过期的过滤条件，外加物品大类。条件文本只按当前过滤条件拼装一次，
    /// **计数、分页与本类全量查询共用同一个方法**：客户端按「匹配总数」算总页数，两处条件不一致会让页数与列表对不上。
    /// 装备部位不在这里：表里没有部位列，由上层在内存里补做。
    /// 未确认的量不参与过滤：分类 id 表还没有逐节点确认，未识别的 id 在上层已被翻成「没有维度」。
    /// 排除自己时机器人挂牌（seller 为 NULL）保留。
    /// 命令文本按条件拼装而不是用 NULL 参数比较，保证 <c>auction_listings_template</c> 索引用得上。
    /// </summary>
    static string Filter(AuctionSearchFilter filter, SqliteCommand command, long now)
    {
        var conditions = new List<string> { "state=0", "expires_at_unix>$now" };
        command.Parameters.AddWithValue("$now", now);
        if (filter.ItemTemplateId is { } template)
        {
            conditions.Add("item_template_id=$template");
            command.Parameters.AddWithValue("$template", template);
        }
        if (filter.ExcludeSellerCharacterId is { } seller)
        {
            conditions.Add("(seller_character_id IS NULL OR seller_character_id<>$seller)");
            command.Parameters.AddWithValue("$seller", seller);
        }
        if (filter.Kinds is { IsDefaultOrEmpty: false } kinds)
        {
            var names = new string[kinds.Length];
            for (int index = 0; index < kinds.Length; index++)
            {
                names[index] = "$kind" + index;
                command.Parameters.AddWithValue(names[index], kinds[index]);
            }
            conditions.Add($"item_kind IN ({string.Join(',', names)})");
        }
        if (filter.Gold is bool gold)
        {
            var names = new string[AuctionGoldTickets.All.Length];
            for (int index = 0; index < AuctionGoldTickets.All.Length; index++)
            {
                names[index] = "$gold" + index;
                command.Parameters.AddWithValue(names[index], AuctionGoldTickets.All[index].ItemId);
            }
            conditions.Add(gold
                ? $"item_template_id IN ({string.Join(',', names)})"
                : $"item_template_id NOT IN ({string.Join(',', names)})");
        }
        return string.Join(" AND ", conditions);
    }

    static AuctionListing Read(SqliteDataReader reader) => new(
        checked((int)reader.GetInt64(0)),
        reader.IsDBNull(1) ? null : reader.GetInt64(1),
        reader.GetString(2),
        checked((int)reader.GetInt64(3)),
        checked((byte)reader.GetInt32(4)),
        checked((int)reader.GetInt64(5)),
        (byte[])reader.GetValue(6),
        checked((int)reader.GetInt64(7)),
        checked((int)reader.GetInt64(8)),
        checked((int)reader.GetInt64(9)),
        reader.GetInt64(10),
        reader.GetInt64(11))
    {
        State = reader.GetInt32(12),
        DepositGold = reader.GetInt32(13)
    };

    /// <summary>
    /// 按挂牌 id 读一行（不含任何在售/过期条件）：取消上架（CMD 184）要靠它区分
    /// 「不存在」/「不是自己的」/「已经不在售」，这三种拒绝的客户端码与提示都不同。
    /// </summary>
    public AuctionListing? LoadListing(int listingId)
    {
        using var db = database.Open();
        return LoadListing(db, null, listingId);
    }

    /// <summary>
    /// **在调用方的事务内**按挂牌 id 读一行（购买提交要在同一事务里复核价格/数量/模板/卖家）。
    /// 与只读入口共用同一条 SELECT 与同一份投影，避免两处读法漂移。
    /// </summary>
    internal static AuctionListing? LoadListing(SqliteConnection db, SqliteTransaction? transaction, int listingId)
    {
        using var query = db.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = $"SELECT {Columns} FROM auction_listings WHERE listing_id=$id";
        query.Parameters.AddWithValue("$id", listingId);
        using var reader = query.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    /// <summary>
    /// 一口价购买的挂牌状态写入：**在调用方的事务内**把一行置为已成交并盖上结算时间。
    ///
    /// 条件是「这一行仍是 `state=0`」—— **影响行数必须为 1**，这是「同一挂牌只能成交一次」的唯一判据
    /// （重复点击、重放、以及「提交成功但应答丢失后的重试」都必须命中这里）。为 0 行时抛
    /// <see cref="AuctionBuyRaceLostException"/> 让**整个事务回滚**：钱、挂牌状态与两封托管邮件
    /// 是跨账号资产，任何「钱扣了、挂牌还是别人的」都只能人工补偿。
    /// 这里**不做「先查后改」**：查与改之间的窗口正是并发购买出问题的地方。
    /// </summary>
    internal static void MarkSold(SqliteConnection db, SqliteTransaction transaction, int listingId, long settledAtUnix)
    {
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE auction_listings SET state=$sold,settled_at_unix=$settled WHERE listing_id=$id AND state=$active";
        command.Parameters.AddWithValue("$sold", AuctionListingState.Sold);
        command.Parameters.AddWithValue("$settled", settledAtUnix);
        command.Parameters.AddWithValue("$id", listingId);
        command.Parameters.AddWithValue("$active", AuctionListingState.Active);
        if (command.ExecuteNonQuery() != 1)
            throw new AuctionBuyRaceLostException($"挂牌 {listingId} 的条件更新（state=0→1）影响 0 行：已被买走、已下架或已过期");
    }

    /// <summary>
    /// 单件/拆分购买（CMD 334 `AUCTION_BUY_ITEM_APIECE`）未买空整堆时的挂牌更新：
    /// 在调用方的事务内把 `item_count` 与 `item_entry` 条件更新为剩余数量，保持 `state=0` 在售。
    /// 条件是 `state=0 AND item_count=$expectedCount`：并发购买或重复点击导致数量已变时影响 0 行，
    /// 抛出 <see cref="AuctionBuyRaceLostException"/> 让整个事务回滚。
    /// </summary>
    internal static void DeductListingCount(SqliteConnection db, SqliteTransaction transaction, int listingId,
        int expectedCount, int remainingCount, byte[] remainingItemEntry)
    {
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE auction_listings SET item_count=$remainingCount,item_entry=$remainingEntry " +
            "WHERE listing_id=$id AND state=$active AND item_count=$expectedCount";
        command.Parameters.AddWithValue("$remainingCount", remainingCount);
        command.Parameters.AddWithValue("$remainingEntry", remainingItemEntry);
        command.Parameters.AddWithValue("$id", listingId);
        command.Parameters.AddWithValue("$active", AuctionListingState.Active);
        command.Parameters.AddWithValue("$expectedCount", expectedCount);
        if (command.ExecuteNonQuery() != 1)
            throw new AuctionBuyRaceLostException($"挂牌 {listingId} 的拆分购买条件更新（item_count={expectedCount}→{remainingCount}）影响 0 行：已被买走、下架或数量已变");
    }

    /// <summary>
    /// 取消上架（CMD 184）的挂牌状态写入：**在调用方的事务内**把一行置为已下架。
    ///
    /// 条件是「这一行仍是该角色的、且 state=0」：并发取消（或客户端重复点）时第二条 UPDATE 影响 0 行，
    /// 这里直接抛异常让**整个事务回滚**（道具不会第二次回到背包）；若只按 listing_id 更新，
    /// 就会出现「同一件道具被还两次」的资产复制。
    /// </summary>
    internal static void MarkCancelled(SqliteConnection db, SqliteTransaction transaction, int listingId, long sellerCharacterId)
    {
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE auction_listings SET state=$cancelled WHERE listing_id=$id AND seller_character_id=$seller AND state=$active";
        command.Parameters.AddWithValue("$cancelled", AuctionListingState.Cancelled);
        command.Parameters.AddWithValue("$active", AuctionListingState.Active);
        command.Parameters.AddWithValue("$id", listingId);
        command.Parameters.AddWithValue("$seller", sellerCharacterId);
        if (command.ExecuteNonQuery() != 1)
            throw new InvalidOperationException("挂牌已被取消、成交或不属于当前角色");
    }

    /// <summary>
    /// 某卖家**已到期但仍在售**的挂牌（`state=0 AND expires_at_unix&lt;=now`），到期结算的输入。
    /// 按 <c>listing_id</c> 稳定排序，与两个「我的拍卖品」读路径同一口径；
    /// 机器人挂牌（`seller_character_id IS NULL`）不会被任何卖家查到，因此没有可退的对象。
    /// </summary>
    public ImmutableArray<AuctionListing> LoadExpiredListings(long sellerCharacterId, long now)
    {
        using var db = database.Open();
        using var query = db.CreateCommand();
        query.CommandText = $"SELECT {Columns} FROM auction_listings WHERE seller_character_id=$seller AND state=0 AND expires_at_unix<=$now ORDER BY listing_id";
        query.Parameters.AddWithValue("$seller", sellerCharacterId);
        query.Parameters.AddWithValue("$now", now);
        var rows = ImmutableArray.CreateBuilder<AuctionListing>();
        using var reader = query.ExecuteReader();
        while (reader.Read())
            rows.Add(Read(reader));
        return rows.ToImmutable();
    }

    /// <summary>
    /// 到期结算的挂牌状态写入：**在调用方的事务内**把一行置为已过期，条件是「这一行仍是该角色的、
    /// 且 `state=0`」，影响 0 行时返回 false（这一行已被下架/成交/并发结算）而**不抛异常**。
    ///
    /// 为什么与 <see cref="MarkCancelled"/>（抛异常）不同：到期结算是**批量、时间驱动**的清扫，
    /// 一行被卖走不该让整批都结算不了；调用方对 false 的处理是「跳过这一行并记原因」——
    /// 既不重复退保证金，也不重复退道具（退回邮件另有 `auction:return:{id}` 唯一键兜底）。
    /// </summary>
    internal static bool TryMarkExpired(SqliteConnection db, SqliteTransaction transaction, int listingId, long sellerCharacterId)
    {
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE auction_listings SET state=$expired WHERE listing_id=$id AND seller_character_id=$seller AND state=$active";
        command.Parameters.AddWithValue("$expired", AuctionListingState.Expired);
        command.Parameters.AddWithValue("$active", AuctionListingState.Active);
        command.Parameters.AddWithValue("$id", listingId);
        command.Parameters.AddWithValue("$seller", sellerCharacterId);
        return command.ExecuteNonQuery() == 1;
    }
}
