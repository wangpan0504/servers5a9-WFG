using static DfoServer.Testing.TestCheck;
using System.Buffers.Binary;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Persistence;
using PvfLib;

namespace DfoServer.Testing.Inventory;

// 交易窗口（ShopId<0）由 etc/tradingsystem.etc 驱动：购买、材料兑换、名册收购价与[sale limit]账号每日上限。
internal static class NpcTradingTests
{
    const int Stone = 10158700, Refined = 10158964, Pentagram = 10306130;
    const int Jonathan = 476, Pumpkin = 609;

    public static void Run(TownCatalog towns, ItemResources itemResources)
    {
        int checks = Count;
        void Reject(Action action, string name)
        {
            bool rejected = false;
            try { action(); }
            catch (InvalidDataException) { rejected = true; }
            That(rejected, name);
        }
        var catalog = itemResources.Items;
        var trading = itemResources.Shops;

        That(NpcShopCatalog.ParseTrading(null).Count == 0 && NpcShopCatalog.ParseTrading("  ").Count == 0,
            "missing trading roster is an empty catalog");
        var parsed = NpcShopCatalog.ParseTrading("""
            [trade item list]
            77
            [shop title]
            `test`
            [item list]
            1001 6666 1.25 1
            [/item list]

            [shop item]
            1002 1001
            [/shop item]
            [/trade item list]

            [particular trade item list]
            77 6 1003 -1 1004 -1
            [/particular trade item list]
            """);
        That(parsed[77][1001] == new NpcTradingProduct(8333, 6666), "roster price rounds 6666x1.25 away from zero to 8333");
        That(parsed[77][1002] == new NpcTradingProduct(0, null) && parsed[77][1003] == new NpcTradingProduct(0, null),
            "exchange-only and particular entries carry no gold price and no resale price");
        foreach (string text in new[]
        {
            "[trade item list]\n[/trade item list]",
            "[trade item list]\n0\n[/trade item list]",
            "[trade item list]\n77\n[item list]\n1001 6666\n[/item list]\n[/trade item list]",
            "[trade item list]\n77\n[item list]\n1001 6666 0 1\n[/item list]\n[/trade item list]",
            "[trade item list]\n77\n[item list]\n1001 -1 1.25 1\n[/item list]\n[/trade item list]",
            "[trade item list]\n77\n[item list]\n0 6666 1.25 1\n[/item list]\n[/trade item list]",
            "[particular trade item list]\n77 6 1003 -2\n[/particular trade item list]",
            "[particular trade item list]\n77 6 1003\n[/particular trade item list]",
            "[particular trade item list]\n0 0\n[/particular trade item list]"
        })
            Reject(() => NpcShopCatalog.ParseTrading(text), "malformed trading roster cannot silently open a merchant");

        // [sale limit] 本版资源只出现过 `account` `daily`；其他范围必须让装配失败，不能放宽收购口径。
        That(ItemShopPrice.Parse(new PvfDocument("[price]\n100\n[sale limit]\n`account` `daily` 80"), false, 0).SaleLimit is
            { AccountWide: true, Count: 80 }, "account daily sale limit is parsed from the item template");
        Reject(() => ItemShopPrice.Parse(new PvfDocument("[price]\n100\n[sale limit]\n`character` `daily` 5"), false, 0),
            "an unconfirmed sale limit scope cannot silently allow selling");

        That(trading.Trading[Pumpkin][Stone] == new NpcTradingProduct(8333, 6666) &&
            trading.Trading[Jonathan][Stone] == new NpcTradingProduct(8333, 6666),
            "matched roster price is 8333 to buy and 6666 to sell");
        That(trading.IsTradingNpc(Pumpkin) && trading.Allows(Pumpkin, -1, Stone) && trading.Allows(Pumpkin, -1, Refined) &&
            trading.Allows(Pumpkin, -1, Pentagram), "matched PVF opens the pumpkin trading window");
        That(trading.Allows(Jonathan, -1, Stone) && !trading.Allows(Jonathan, -1, Pentagram) &&
            !trading.Allows(Pumpkin, -1, 10160299) && trading.Allows(Jonathan, -1, 10160299),
            "trading roster is per merchant: pentagram coin and particular goods do not leak");
        That(!trading.Allows(Pumpkin, -1, 3175) && !trading.Allows(1, -1, Stone) && trading.Allows(1, 2, 3175),
            "unknown item or non-trading merchant stays rejected while normal shops are unchanged");
        That(trading.PurchasePrice(Pumpkin, -1, Stone, catalog.Get(Stone).ShopPrice) is { Gold: 8333 } &&
            trading.PurchasePrice(Pumpkin, 2, 3175, catalog.Get(3175).ShopPrice) == catalog.Get(3175).ShopPrice,
            "trading window overlays the roster price while normal shops keep the template price");
        That(trading.PurchasePrice(Jonathan, -1, Pentagram, catalog.Get(Pentagram).ShopPrice) is null,
            "item outside this merchant roster has no price at all");

        // 购买限购按调用方传入的 utcNow 计日，出售按注入时钟计日：两者都固定到假时钟才能做跨日断言。
        var clock = new Clock(new DateTimeOffset(2026, 10, 3, 21, 59, 59, TimeSpan.Zero));
        var shop = new InventoryShopService(catalog, trading, itemResources.GoldLimits, itemResources.EpicPieces, clock);
        int today = ShopPurchaseCount.Today(clock.GetUtcNow());
        var current = new Character(1, 0, "Trading", 0, towns.Room, towns.InitialReturn) { Job = 15 };

        current = current with { Inventory = new([], Gold: 100_000) };
        var bought = shop.Buy(current, new(Stone, 1, -1, Pumpkin), out var receipt, clock.GetUtcNow());
        var ack = ShopPackets.Bought(bought, receipt);
        That(bought.Accepted && bought.Plan!.After.Gold == 100_000 - 8333 &&
            bought.Plan.After.Items is [{ Core.ItemId: Stone, Core.Count: 1 }] &&
            receipt is { Count: 1, Core.ItemId: Stone },
            "pumpkin sells one time stone for the roster price");
        That(ack.Length == 141 && I32(ack, 1) == 91_667 && I32(ack, 19) == Stone && I32(ack, 23) == 1 && ack[140] == 0,
            "trading purchase ACK reuses the native shop reply shape");
        bought = shop.Buy(current, new(Stone, 3, -1, Pumpkin), out receipt, clock.GetUtcNow());
        That(bought.Accepted && bought.Plan!.After.Gold == current.Inventory.Gold - 3 * 8333 && receipt!.Count == 3,
            "bulk purchase multiplies the roster price");

        current = current with { Inventory = new([new(0, Bag(121), Core(Stone, 12))], Gold: 55_555) };
        bought = shop.Buy(current, new(Refined, 1, -1, Pumpkin), out receipt, clock.GetUtcNow());
        ack = ShopPackets.Bought(bought, receipt);
        That(bought.Accepted && bought.Plan!.After.Gold == 55_555 &&
            bought.Plan.After.Find(Bag(121)) is { Core.ItemId: Stone, Core.Count: 2 } &&
            bought.Plan.After.Items.Any(item => item.Core.ItemId == Refined && item.Core.Count == 1) &&
            receipt!.Materials.SequenceEqual(new ShopMaterialBalance[] { new(Stone, 2) }),
            "ten time stones exchange for one refined stone without charging gold");
        That(ack.Length == 149 && ack[140] == 1 && I32(ack, 141) == Stone && I32(ack, 145) == 2,
            "exchange ACK carries the paid stone balance");

        current = current with { Inventory = new([new(0, Bag(121), Core(Stone, 5))], Gold: 0) };
        bought = shop.Buy(current, new(Pentagram, 1, -1, Pumpkin), out receipt, clock.GetUtcNow());
        // 整叠材料被消耗后源槽释放，新物品会复用该槽位，因此只能断言「只剩这一件产物」。
        That(bought.Accepted && bought.Plan!.After.Items is [{ Core.ItemId: Pentagram, Core.Count: 1 }],
            "five time stones exchange for one pentagram coin and consume the whole stack");
        current = current with { Inventory = new([new(0, Bag(121), Core(Stone, 9))], Gold: 1_000) };
        That(shop.Buy(current, new(Refined, 1, -1, Pumpkin), out _, clock.GetUtcNow()) is { Accepted: false, Error: 10 } &&
            current.Inventory.Find(Bag(121))!.Core.Count == 9, "short exchange never consumes part of the material");
        That(shop.Buy(current, new(Pentagram, 1, -1, Jonathan), out _, clock.GetUtcNow()) is { Accepted: false, Error: 17 },
            "merchant without the item in its roster cannot sell it");

        current = current with { Inventory = new([new(0, Bag(121), Core(Refined, 9))], Gold: 0) };
        bought = shop.Buy(current, new(10160299, 1, -1, Jonathan), out receipt, clock.GetUtcNow());
        That(bought.Accepted && bought.Plan!.After.Items is [{ Core.ItemId: 10160299, Core.Count: 1 }] &&
            bought.Plan.After.Find(Bag(121)) is null, "particular trade item exchanges for refined stones");
        current = current with { Inventory = new([new(0, Bag(121), Core(Refined, 9))], Gold: 0) };
        That(shop.Buy(current, new(10160299, 1, -1, Pumpkin), out _, clock.GetUtcNow()) is { Accepted: false, Error: 17 },
            "particular goods stay scoped to their own merchant");
        current = current with
        {
            Inventory = new InventorySnapshot([new(0, Bag(121), Core(Refined, 20))], Gold: 0)
            {
                ShopPurchases = [new(Jonathan, 10160299, true, true, today, 30)]
            }
        };
        That(shop.Buy(current, new(10160299, 1, -1, Jonathan), out _, clock.GetUtcNow()) is { Accepted: false, Error: 0x85 },
            "particular daily purchase limit is enforced per merchant");

        current = current with
        {
            Inventory = new InventorySnapshot([], Gold: 1_000_000)
            {
                ShopPurchases = [new(Pumpkin, Stone, true, true, today, 2799)]
            }
        };
        That(shop.Buy(current, new(Stone, 2, -1, Pumpkin), out _, clock.GetUtcNow()) is { Accepted: false, Error: 0x85 },
            "per-merchant daily purchase limit rejects the over-cap buy");
        bought = shop.Buy(current, new(Stone, 1, -1, Pumpkin), out _, clock.GetUtcNow());
        That(bought.Accepted && bought.Plan!.After.ShopPurchases.Single() is { NpcId: Pumpkin, Count: 2800 },
            "the last allowed unit fills the merchant quota");
        var counts = shop.PurchaseCounts(current, Pumpkin, clock.GetUtcNow());
        That(counts is [{ ItemId: Stone, Count: 2799 }] && ShopPackets.PurchaseCounts(counts).Length == 25,
            "trading merchant answers the count query instead of an error");
        That(shop.PurchaseCounts(current, int.MaxValue) is null, "non-trading merchant keeps the rejection path");
        // 只在交易名册里的商人（不在 itemshop/*.shp）也必须答计数；只测南瓜球会被它自己的普通商店掩盖。
        var tradingOnly = trading.Trading.Keys.Where(id => !trading.Shops.Values.Any(entry => entry.NpcId == id)).ToArray();
        That(tradingOnly.Length > 0, "at least one trading merchant has no itemshop entry so the roster guard is observable");
        foreach (int npcId in tradingOnly)
            That(shop.PurchaseCounts(current, npcId, clock.GetUtcNow()) is not null, "trading-only merchant answers the count query: " + npcId);

        current = current with { Inventory = new([new(0, Bag(121), Core(Stone, 3))], Gold: 1_000) };
        var request = new SellItemRequest(Bag(121), 3, Pumpkin, 2, 0);
        var sold = shop.Sell(current, request);
        ack = ShopPackets.Sold(sold, request);
        That(sold.Accepted && sold.Plan!.After.Gold == 1_000 + 3 * 6666 && sold.Plan.After.Items.IsEmpty &&
            ack.Length == 12 && I32(ack, 1) == 20_998 && I32(ack, 8) == 3,
            "selling time stones pays the roster price and credits the sold count");
        current = current with { Inventory = new([new(0, Bag(121), Core(Stone, 1))], Gold: 1_000) };
        var elsewhere = shop.Sell(current, new(Bag(121), 1, 1, 2, 0));
        That(elsewhere.Accepted && elsewhere.Plan!.After.Gold == 1_000,
            "selling to a non-trading NPC keeps the template price instead of the roster price");

        current = current with
        {
            Inventory = new InventorySnapshot([new(0, Bag(121), Core(Stone, 2))], Gold: 0)
            {
                ShopPurchases = [new(0, Stone, true, true, today, 79)]
            }
        };
        That(shop.Sell(current, new(Bag(121), 2, Pumpkin, 2, 0)) is { Accepted: false, Error: 0x85, Plan: null } &&
            current.Inventory.Find(Bag(121))!.Core.Count == 2,
            "daily sale limit rejects the whole sale without deleting items");
        sold = shop.Sell(current, new(Bag(121), 1, Pumpkin, 2, 0));
        That(sold.Accepted && sold.Plan!.After.ShopPurchases.Single() is { NpcId: 0, ItemId: Stone, AccountWide: true, Count: 80 } &&
            sold.Plan.After.Gold == 6666, "the last allowed stone fills the account daily sale limit");

        string root = Path.Combine(Path.GetTempPath(), "s5a9-npc-trading-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var repo = new CharacterRepository(Path.Combine(root, "game.db"));
            var owners = new AccountOwners();
            using var owner = owners.Acquire("trading", () => { });
            // 1 级角色见不到这类交易商人；且一次卖 80 个时空石得 533280 金币，超过 1 级角色 100000 的携带上限。
            // 夹具按 85 级建角，才能同时覆盖真实入口与足额金币。
            var seller = repo.Create(owner, new("TradingStone", towns.Room, towns.InitialReturn)
            {
                Level = 85,
                Inventory = new([new(0, Bag(121), Core(Stone, 80))], Gold: 0)
            });
            var bulk = shop.Sell(seller, new(Bag(121), 80, Pumpkin, 2, 0));
            That(bulk.Accepted && bulk.Plan!.After.Gold == 80 * 6666, "eighty stones sell for the full roster price");
            long reset = clock.GetUtcNow().AddSeconds(1).ToUnixTimeSeconds();
            That(bulk.Plan!.CharacterPrecondition is { } daily && daily.Matches(seller, reset - 1) && !daily.Matches(seller, reset),
                "每日收购资格在重置等号拒绝上一日的旧计划");
            var committed = InventoryService.Commit(repo, owner, seller, bulk);
            That(committed.Inventory.ShopPurchases is [{ NpcId: 0, ItemId: Stone, Count: 80 }] && committed.Inventory.Items.IsEmpty,
                "sale limit counter commits together with the sale");
            var sibling = repo.Create(owner, new("TradingPeer", towns.Room, towns.InitialReturn)
            {
                Level = 85,
                Inventory = new([new(0, Bag(121), Core(Stone, 1))], Gold: 0)
            });
            That(shop.Sell(sibling, new(Bag(121), 1, Pumpkin, 2, 0)) is { Accepted: false, Error: 0x85 },
                "account daily sale limit is shared by every character");
            clock.Advance(TimeSpan.FromSeconds(1));
            var nextDay = shop.Sell(sibling, new(Bag(121), 1, Pumpkin, 2, 0));
            That(nextDay.Accepted && nextDay.Plan!.After.Gold == 6666 && nextDay.Plan.After.Items.IsEmpty,
                "the sale limit resets at the 06:00 UTC+8 boundary");
        }
        finally { TestFiles.DeleteDirectory(root, true); }
        Console.WriteLine($"PASS {Count - checks} NPC trading assertions");
        ItemCore Core(int id, int count = 1) => InventoryRewardGrantService.Create(catalog.Get(id), count);
    }
    sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; private set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
        public void Advance(TimeSpan amount) => Now += amount;
    }
    static InventorySlot Bag(short index) => new(InventoryListType.Main, index);
    static int I32(byte[] bytes, int offset) => BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset));
}
