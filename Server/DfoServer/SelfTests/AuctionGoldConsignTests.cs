using static DfoServer.Testing.TestCheck;
using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Auction;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Network.Builders;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;

namespace DfoServer;

/// <summary>
/// 金币寄售闭环：上架金币券、与普通拍卖隔离、购买结算、有券/无券税率、到期退回。
/// 模板预置，不读 PVF。费率数字的出处在 <see cref="AuctionService.GoldCouponSaleTaxPercent"/>。
/// </summary>
internal static class AuctionGoldConsignTests
{
    const int GoldTicket = 2681725;
    const int OtherTicket = 2683073;
    const int TokenCera = 2681762;
    const int Equipment = 10400;
    const string Sample =
        "00001B00A028000001000000FFFFFFFF684200006842000018000000000000000000000000";
    static readonly Location Room = new(38, 1, 450, 234, 0);

    public static void Run()
    {
        int checks = Count;
        Catalog();
        RegisterAndIsolate();
        BuyAndTax();
        Expire();
        Console.WriteLine($"PASS {Count - checks} auction gold consign assertions");
    }

    static void Catalog()
    {
        That(AuctionGoldTickets.All.Length == 17 &&
            AuctionGoldTickets.All.Select(ticket => ticket.ItemId).Distinct().Count() == 17,
            "PVF chn_Trade_Gold 的 gold_*.stk 是 17 张互不相同的金币券");
        That(AuctionGoldTickets.IsCategory(40101) && AuctionGoldTickets.IsCategory(40117) &&
            !AuctionGoldTickets.IsCategory(40100) && !AuctionGoldTickets.IsCategory(40118) &&
            !AuctionGoldTickets.IsCategory(40119),
            "金币分类只含 40101–40117，不含「全部」40119");
        That(AuctionGoldTickets.IsTicket(GoldTicket) && AuctionGoldTickets.IsTicket(OtherTicket) &&
            !AuctionGoldTickets.IsTicket(TokenCera) && !AuctionGoldTickets.IsTicket(Equipment) &&
            !AuctionGoldTickets.IsTicket(0),
            "代币券和普通装备不是金币券；item 0 也不当作金币券");
        That(AuctionService.SaleTaxPercent == 5 && AuctionService.CouponSaleTaxPercent == 3 &&
            AuctionService.GoldCouponSaleTaxPercent == 3 &&
            AuctionService.TaxPercent(false, true) == 5 && AuctionService.TaxPercent(true, true) == 3 &&
            AuctionService.SellerProceeds(800_000, 3) == 776_000 && AuctionService.SellerProceeds(800_000) == 760_000,
            "无券 5%；持券无论普通拍卖还是金币寄售都是 3%（PVF guide/41 写明减至 3%，不是 1%）");
        Reject<InvalidDataException>(() => AuctionService.SellerProceeds(800_000, 1), "不采用未经 PVF 证实的 1%");
    }

    static void RegisterAndIsolate()
    {
        using var f = new Fixture();
        var listed = f.Register(GoldTicket, 121, 800_000);
        That(listed.Error == AuctionRegisterError.None && listed.Character is not null, "金币券可以上架");
        That(listed.Character!.Inventory.Gold == 1_000_000 - AuctionService.ListingDepositGold, "上架扣 10,000 保证金");
        That(listed.Character.Inventory.Items.Single(item => item.Core.ItemId == GoldTicket).Core.Count == 1,
            "两张金币券上架一张后，背包还剩一张");
        int listingId = checked((int)f.QueryLong($"SELECT listing_id FROM auction_listings WHERE item_template_id={GoldTicket}"));
        That(f.QueryLong($"SELECT deposit_gold FROM auction_listings WHERE listing_id={listingId}") == AuctionService.ListingDepositGold,
            "挂牌记下金币券模板和实收保证金");

        var token = f.Decide(TokenCera, 122, 800_000);
        That(token.Error == AuctionRegisterError.ItemNotTradable && token.Options is null,
            "同目录的代币券仍按虚拟资产拒绝，不能借金币寄售上架");

        f.Repository.InsertListings([new(null, "拍卖机器人乙", OtherTicket, (byte)ItemKind.Material, 1,
            AuctionPackets.ItemEntrySnapshot(new ItemCore(OtherTicket, ItemKind.Material, 1)),
            1_000_000, 1_500_000, f.Now, f.Now + 86_400)]);

        var equipment = f.Service.Search(new(0, null, AuctionCategoryFilter.All), f.Seller.Id, f.Now);
        That(equipment.TotalCount == 1 && equipment.Listings.Single().ItemTemplateId == Equipment,
            "普通「全部」只看到装备，两张金币券都不在里面");
        var gold = f.Service.Search(new(0, null, 40101), f.Seller.Id, f.Now, goldWindow: false);
        var goldWindow = f.Service.Search(new(0, null, 0), f.Seller.Id, f.Now, goldWindow: true);
        That(gold.TotalCount == 1 && gold.Listings.Single().ItemTemplateId == OtherTicket &&
            goldWindow.TotalCount == 1 && goldWindow.Listings.Single().ItemTemplateId == OtherTicket &&
            gold.Plan!.Category.Status == AuctionCategoryStatus.Gold,
            "40101 与 mode=1 都只返回别人的金币券，且 40101 与 40117 同一市场（面额细分没有抓包）");
        That(f.Service.Search(new(0, null, 40117), f.Seller.Id, f.Now).Listings.Single().ItemTemplateId == OtherTicket,
            "40117 与 40101 目前返回同一批金币券");
        That(f.Service.SellerListings(f.Seller.Id, f.Now, false, goldMarket: true).Single().ListingId == listingId &&
            f.Service.SellerListings(f.Seller.Id, f.Now, false, goldMarket: false).IsEmpty,
            "我的拍卖品：金币侧只有自己的金币券，普通侧没有这张券");
        That(f.Service.Search(new(0, GoldTicket, 40101), f.Seller.Id, f.Now).TotalCount == 0,
            "按另一张面额的模板去搜 1百万券，自己的挂牌被排除后没有别的 1百万券");
    }

    static void BuyAndTax()
    {
        using var plain = new Fixture();
        plain.Register(GoldTicket, 121, 800_000);
        int listingId = checked((int)plain.QueryLong("SELECT max(listing_id) FROM auction_listings"));
        long expires = plain.QueryLong($"SELECT expires_at_unix FROM auction_listings WHERE listing_id={listingId}");
        var plan = plain.Service.PlanBuy(plain.Buyer, plain.Service.FindListing(listingId),
            AuctionBuyRequest.Parse(BuyBody(800_000, listingId, expires, GoldTicket)), plain.Now);
        That(plan.Error == AuctionBuyError.None && plan.Options!.SellerProceeds == 760_000,
            "无券购买金币券：买方按 800000 结算，卖方实收 floor(800000×95%)=760000");
        var bought = plain.Service.Buy(plain.BuyerOwner, plain.Buyer, plan.Options!);
        That(bought.Error == AuctionBuyError.None && plain.Gold(plain.Buyer.Id) == plain.Buyer.Inventory.Gold - 800_000 &&
            plain.Gold(plain.Seller.Id) == 1_000_000 - AuctionService.ListingDepositGold,
            "买方金币减少成交价；卖方钱包先不动，所得进托管邮件");
        That(plain.QueryLong("SELECT state FROM auction_listings WHERE listing_id=" + listingId) == AuctionListingState.Sold &&
            plain.QueryLong("SELECT gold FROM mailbox_messages WHERE delivery_key='auction:gold:" + listingId + "'") == 760_000 &&
            plain.QueryLong("SELECT count(*) FROM mailbox_attachments a JOIN mailbox_messages m ON m.mail_id=a.mail_id WHERE m.delivery_key='auction:item:" + listingId + "'") == 1,
            "成交后挂牌 state=1，卖方邮件 760000，买方邮件带着金币券");

        using var coupon = new Fixture();
        coupon.Register(GoldTicket, 121, 800_000);
        int couponListing = checked((int)coupon.QueryLong("SELECT max(listing_id) FROM auction_listings"));
        coupon.Execute($"INSERT INTO account_wallets(account,premium_contracts) VALUES('gold-seller','{{\"29\":{coupon.Now + 3600}}}')");
        long couponExpires = coupon.QueryLong($"SELECT expires_at_unix FROM auction_listings WHERE listing_id={couponListing}");
        var discounted = coupon.Service.PlanBuy(coupon.Buyer, coupon.Service.FindListing(couponListing),
            AuctionBuyRequest.Parse(BuyBody(800_000, couponListing, couponExpires, GoldTicket)), coupon.Now);
        That(discounted.Options!.SellerProceeds == 776_000 &&
            discounted.Options.Custody.Single(mail => mail.Gold > 0).Text.Contains("3%", StringComparison.Ordinal),
            "持有类型 29 时金币寄售手续费降到 3%，实收 776000，不是 1% 的 792000");
        var committed = coupon.Service.Buy(coupon.BuyerOwner, coupon.Buyer, discounted.Options);
        That(committed.Error == AuctionBuyError.None &&
            coupon.QueryLong("SELECT gold FROM mailbox_messages WHERE delivery_key='auction:gold:" + couponListing + "'") == 776_000,
            "提交后的卖方邮件按 3% 入账");
        That(coupon.QueryLong("SELECT state FROM auction_listings WHERE item_template_id=" + Equipment) == 0,
            "同库的普通装备挂牌没有被这笔金币成交改掉");
    }

    static void Expire()
    {
        using var f = new Fixture();
        f.Register(GoldTicket, 121, 800_000);
        int listingId = checked((int)f.QueryLong($"SELECT listing_id FROM auction_listings WHERE item_template_id={GoldTicket}"));
        f.Execute($"UPDATE auction_listings SET expires_at_unix={f.Now - 60}, posted_at_unix={f.Now - 120} WHERE listing_id={listingId}");
        var plan = f.Service.PlanExpire(f.Seller, f.Now);
        That(plan.Rows.Length == 1 && plan.Rows[0].Mail.Gold == AuctionService.ListingDepositGold &&
            plan.Rows[0].Mail.Items.Single().Core.ItemId == GoldTicket,
            "到期退回邮件里是这张金币券，加上挂牌时实收的 10000 保证金");
        var settled = f.Service.SettleExpired(f.SellerOwner, f.Seller, plan);
        That(settled.SettledCount == 1 && f.QueryLong($"SELECT state FROM auction_listings WHERE listing_id={listingId}") == AuctionListingState.Expired &&
            f.QueryLong("SELECT count(*) FROM mailbox_attachments") == 1 &&
            f.QueryLong("SELECT gold FROM mailbox_messages") == AuctionService.ListingDepositGold,
            "到期结算把金币券和保证金退进同一封邮件，挂牌变为已过期");
        That(f.Service.PlanExpire(f.Seller, f.Now).IsEmpty, "同一张券不会被退第二次");
    }

    static byte[] BuyBody(int amount, int listingId, long expires, int itemId)
    {
        var body = new byte[30];
        BitConverter.GetBytes(amount).CopyTo(body, 1);
        BitConverter.GetBytes(listingId).CopyTo(body, 5);
        BitConverter.GetBytes(unchecked((uint)expires)).CopyTo(body, 9);
        BitConverter.GetBytes(itemId).CopyTo(body, 26);
        return body;
    }

    sealed class Fixture : IDisposable
    {
        readonly string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "S5A9-auction-gold-" + Guid.NewGuid().ToString("N"));
        public string Path { get; }
        public AccountOwners Owners { get; } = new();
        public AccountOwners.Lease SellerOwner { get; }
        public AccountOwners.Lease BuyerOwner { get; }
        public CharacterRepository Repository { get; }
        public AuctionService Service { get; }
        public Character Seller { get; set; }
        public Character Buyer { get; }
        public long Now { get; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        public Fixture()
        {
            Directory.CreateDirectory(root);
            Path = System.IO.Path.Combine(root, "game.db");
            SellerOwner = Owners.Acquire("gold-seller", () => { });
            BuyerOwner = Owners.Acquire("gold-buyer", () => { });
            Repository = new(Path);
            var catalog = new ItemCatalog(null!, null!, null!, 0);
            catalog.PrefillTemplates(Templates);
            Service = new AuctionService(Repository, new AuctionCategoryResolver(catalog), catalog,
                new EpicPieceCatalog("[piece list]\n60000 70001\n[/piece list]"),
                AuctionPackets.ItemEntrySnapshot, AuctionPackets.ItemCoreFromEntry);
            Seller = Repository.Create(SellerOwner, new("GoldSeller", Room, null)
            {
                Level = 95,
                Inventory = new(ImmutableArray.Create(
                    new InventoryItem(0, new(InventoryListType.Main, 121), new ItemCore(GoldTicket, ItemKind.Material, 2)),
                    new InventoryItem(0, new(InventoryListType.Main, 122), new ItemCore(TokenCera, ItemKind.Material, 1)),
                    new InventoryItem(0, new(InventoryListType.Main, 27), new ItemCore(Equipment, ItemKind.Equipment, 999_999_998) { Attr = 2, Durability = 45 })
                ), Gold: 1_000_000)
            });
            Buyer = Repository.Create(BuyerOwner, new("GoldBuyer", Room, null) { Level = 95, Inventory = new([], Gold: 5_000_000) });
            Repository.InsertListings([new(null, "装备机器人", Equipment, (byte)ItemKind.Equipment, 1,
                AuctionPackets.ItemEntrySnapshot(new ItemCore(Equipment, ItemKind.Equipment, 999_999_998) { Attr = 2, Durability = 45 }),
                16_000, 17_000, Now, Now + 86_400)]);
        }

        public AuctionRegisterDecision Decide(int itemId, short slot, int buyout)
        {
            var body = Convert.FromHexString(Sample);
            body[2] = (byte)slot;
            body[3] = (byte)(slot >> 8);
            BitConverter.GetBytes(itemId).CopyTo(body, 4);
            BitConverter.GetBytes(buyout).CopyTo(body, 16);
            BitConverter.GetBytes(buyout).CopyTo(body, 20);
            return Service.PlanRegister(Seller, AuctionRegisterRequest.Parse(body), Now);
        }

        public AuctionListingRegisterResult Register(int itemId, short slot, int buyout)
        {
            var decision = Decide(itemId, slot, buyout);
            if (decision.Options is null)
                return new(decision.Error, null);
            var result = Service.Register(SellerOwner, Seller, decision.Options);
            if (result.Character is not null)
                Seller = result.Character;
            return result;
        }

        public long Gold(long characterId) => QueryLong($"SELECT gold FROM characters WHERE id={characterId}");
        public long QueryLong(string sql) => Convert.ToInt64(Query(sql), System.Globalization.CultureInfo.InvariantCulture);
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
        SqliteConnection Open()
        {
            var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path, Pooling = false, ForeignKeys = true }.ToString());
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

    static readonly Dictionary<int, ItemTemplate> Templates = new()
    {
        [GoldTicket] = new(GoldTicket, ItemKind.Material, 0, 1, 0, 1000, 1, 0, 1, "[free]", 65535, 0, "1百万"),
        [OtherTicket] = new(OtherTicket, ItemKind.Material, 0, 1, 0, 1000, 1, 0, 1, "[free]", 65535, 0, "2亿"),
        [TokenCera] = new(TokenCera, ItemKind.Material, 0, 1, 0, 1000, 1, 0, 1, "[free]", 65535, 0, "代币券"),
        [Equipment] = new(Equipment, ItemKind.Equipment, 15, 1, 45, 1, 1, 0, 1, "[free]", 65535, 0, "残缺的牛皮背心")
    };
}
