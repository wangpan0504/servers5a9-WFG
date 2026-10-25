using static DfoServer.Testing.TestCheck;
using DfoServer.Infrastructure;
using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;

namespace DfoServer.Testing.Inventory;

internal static class ShopTests
{
    public static void Run(TownCatalog towns, CharacterCatalog characters, ItemResources itemResources)
    {
        int checks = Count;
        void Reject(Action action, string name)
        {
            bool rejected = false;
            try { action(); }
            catch (Exception e) when (e is SqliteException or InvalidDataException or InvalidOperationException) { rejected = true; }
            That(rejected, name);
        }
        var catalog = itemResources.Items;
        var shop = new InventoryShopService(catalog, itemResources.Shops, itemResources.GoldLimits, itemResources.EpicPieces);
        var items = new InventoryService(catalog, itemResources.GoldLimits, itemResources.EpicPieces);
        That(NpcShopCatalog.ParseSaleRestrictions("[undeletable item]\n17 23 17\n[/undeletable item]").SetEquals(new[] { 17, 23 }),
            "sale restriction resource uses declared item IDs and tolerates repeated entries");
        That(NpcShopCatalog.ParseSaleRestrictions("[undeletable item]\n[/undeletable item]").Count == 0,
            "explicit empty restriction block is a valid empty resource list");
        foreach (string? text in new string?[] { null, "", "[undeletable item]\n17", "[undeletable item]\n0\n[/undeletable item]",
            "[undeletable item]\n-1\n[/undeletable item]", "[undeletable item]\n2147483648\n[/undeletable item]",
            "[undeletable item]\n17 bad\n[/undeletable item]", "[undeletable item]\n17\n[/undeletable item]\n[undeletable item]\n23\n[/undeletable item]" })
            Reject(() => NpcShopCatalog.ParseSaleRestrictions(text), "missing or malformed sale restrictions cannot silently allow sales");
        var now = new DateTimeOffset(2026, 10, 3, 21, 59, 59, TimeSpan.Zero);
        string root = Path.Combine(Path.GetTempPath(), "s5a9-shop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "game.db");
            var repo = new CharacterRepository(path);
            var owners = new AccountOwners();
            using var owner = owners.Acquire("shop", () => { });
            var current = repo.Create(owner, new("ShopOne", towns.Room, towns.InitialReturn)
            {
                Level = 60, AcceptedStoryLevel = 1, Skills = characters.Get(0).Skills,
                Inventory = InventoryRewardGrantService.CreateStarter(characters.Get(0)) with { Gold = 1_000_000 }
            });
            That(itemResources.Shops.Allows(1, 2, 3175) && !itemResources.Shops.Allows(3, 2, 3175) && !itemResources.Shops.Allows(1, 2, 51), "catalog checks shop, NPC and offered item");
            That(itemResources.Shops.Shops.ContainsKey(15), "region-prefixed shop file is resolved");
            That(catalog.Get(3175).ShopPrice is { Gold: 10, SellGold: 1 } && catalog.Get(10400).ShopPrice.SellGold == 64, "matching PVF stack and Client equipment sell rates");
            foreach (int id in new[] { 16400, 18400 })
            {
                var template = catalog.Get(id);
                foreach (var (durability, expected) in new (ushort, int)[] { (20, 25), (10, 12), (0, 0), (30, 25) })
                {
                    var item = new InventoryItem(0, Bag(36), InventoryRewardGrantService.Create(template) with { Durability = durability });
                    var source = current with { Inventory = current.Inventory with { Items = [item] } };
                    var sale = shop.Sell(source, new(Bag(36), 1, 1, 2, 75));
                    That(sale.Accepted && sale.Plan!.After.Gold == current.Inventory.Gold + expected && sale.Plan.After.Items.IsEmpty,
                        "native equipment sale gold and deletion " + id + "/" + durability);
                }
            }
            That(catalog.Get(10400).ShopPrice.GetSellGold(16, 33) == 30, "fractional durability truncates before the 90-percent adjustment");
            foreach (int id in new[] { 4237, 4238, 4240, 4277, 4275 })
            {
                var forbidden = current with { Inventory = new([new(1001, Bag(177), InventoryRewardGrantService.Create(catalog.Get(id), 2))], Gold: 1000) };
                That(!itemResources.Shops.CanSell(id) && shop.Sell(forbidden, new(Bag(177), 1, 1, 2, 0)) is { Accepted: false, Error: 1, Plan: null },
                    "resource restriction rejects a real quest material without deleting it " + id);
            }
            foreach (var (id, kind, slot, count, remaining, price) in new (int, ItemKind, short, int, int, int)[]
            {
                (3175, ItemKind.Material, 121, 2, 1, 1), (1106, ItemKind.Consumable, 65, 2, 1, 20),
                (7542, ItemKind.Consumable, 65, 2, 1, 0), (2058, ItemKind.Consumable, 65, 2, 1, 0),
                (10400, ItemKind.Equipment, 36, 1, 0, 64)
            })
            {
                var item = new InventoryItem(1001, Bag(slot), InventoryRewardGrantService.Create(catalog.Get(id), count));
                var source = current with { Inventory = new([item], Gold: 1000) };
                var request = new SellItemRequest(item.Position, 1, 1, 2, 0);
                var sale = shop.Sell(source, request);
                That(item.Core.Kind == kind && itemResources.Shops.CanSell(id) && sale is { Accepted: true, MoveCount: 1 } &&
                    sale.Plan!.After.Gold == 1000 + price && (sale.Plan.After.Find(item.Position)?.Core.Count ?? 0) == remaining &&
                    sale.Plan.After.Items.All(saved => saved.Uid == item.Uid && saved.Core.ItemId == id),
                    "generic entity sale preserves surviving UID and does not open sold packages " + id);
                That(shop.Sell(source with { Inventory = source.Inventory with { Items = [item with { Core = item.Core with { EquipmentLockId = 2 } }] } }, request)
                    is { Accepted: false, Error: 0xd6, Plan: null }, "generic sale keeps the real safety lock " + id);
                That(shop.Sell(source with { Inventory = source.Inventory with { Items = [item with { Core = item.Core with { ExpireTime = 1 } }] } }, request)
                    is { Accepted: false, Plan: null }, "generic sale rejects expired source instances " + id);
            }
            That(catalog.Get(16400).ShopPrice.GetSellGold(0, 0) == 25, "equipment without maximum durability keeps its base sale price");
            foreach (int value in new[] { 0, 1 })
                That(ItemShopPrice.Parse(new("[value]\n" + value), true, 200).GetSellGold(1, 1) == 0, "zero sale does not invent a one-gold minimum " + value);
            That(ItemShopPrice.Parse(new("[value]\n2147483647"), true, int.MaxValue).GetSellGold(1, 1) == int.MaxValue,
                "large equipment price cannot wrap into a cheap sale");
            var bought = shop.Buy(current, new(3175, 10, 2, 1), out var receipt);
            var ack = ShopPackets.Bought(bought, receipt);
            That(ack.Length == 141 && ack[0] == 1 && I32(ack, 1) == 999900 && I32(ack, 19) == 3175 && I32(ack, 23) == 10 && ack[140] == 0,
                "buy ACK four scalars, complete 123-byte entry and empty material list");
            current = Commit(bought);
            var material = current.Inventory.Items.Single(item => item.Core.ItemId == 3175);
            long uid = material.Uid;
            var stale = current;
            bought = shop.Buy(current, new(3175, 5, 2, 1), out receipt);
            That(receipt!.Core!.Value == 15 && I32(ShopPackets.Bought(bought, receipt), 23) == 5 && bought.Plan!.After.Find(material.Position)!.Core.Value == 15,
                "domain receipt keeps complete core while builder projects only the purchase increment");
            current = Commit(bought);
            That(current.Inventory.Find(material.Position)!.Uid == uid, "repeated valid buys retain target UID and each charge once");
            Reject(() => repo.CommitInventory(owner, stale, shop.Buy(stale, new(3175, 1, 2, 1), out _).Plan!), "stale revision cannot buy");
            That(shop.Buy(current, new(3175, int.MaxValue, 2, 1), out _) is { Accepted: false, Error: 0x85 }, "huge count rejected before costs");
            That(!shop.Buy(current, new(3175, 1, 2, 3), out _).Accepted, "forged NPC rejected");
            foreach (int count in new[] { int.MinValue, -1, 1_000_001, int.MaxValue })
                foreach (int id in new[] { 3175, 10402 })
                    That(shop.Buy(current, new(id, count, 2, 1), out receipt) is { Accepted: false, Error: 0x85, Plan: null } && receipt is null,
                        "invalid purchase count does not become a one-item charge " + id + "/" + count);
            That(shop.Buy(current, new(3175, 0, 2, 1), out receipt) is { Accepted: false, Error: 0x85, Plan: null } && receipt is null,
                "zero stackable purchase remains rejected without a plan or receipt");
            That(shop.Buy(current with { Job = 15, Level = 1 }, new(10402, 99, 2, 1), out receipt).Accepted && receipt!.Count == 1,
                "equipment buys one without inventing profession or wear-level purchase constraints");
            var talismanTemplate = catalog.Get(10402) with
            {
                Kind = ItemKind.Talisman, EquipmentSlot = 34,
                ShopPrice = new(100, 0, [], new(false, 10, false))
            };
            var talismanCatalog = new ItemCatalog(null!, new Dictionary<int, string>(), new Dictionary<int, string>(), 0);
            talismanCatalog.PrefillTemplates(new Dictionary<int, ItemTemplate> { [10402] = talismanTemplate });
            talismanCatalog.PrefillTalismans([new(talismanTemplate, [1], 9)]);
            var talismanShop = new InventoryShopService(talismanCatalog, itemResources.Shops, itemResources.GoldLimits, itemResources.EpicPieces);
            foreach (int count in new[] { 0, 1, 2 })
            {
                var talismanBuyer = current with { Inventory = new([], Gold: 1000) };
                var purchase = talismanShop.Buy(talismanBuyer, new(10402, count, 2, 1), out var talismanReceipt);
                That(purchase.Accepted && talismanReceipt is { Count: 1, Position.List: InventoryListType.Talisman } &&
                    purchase.Plan!.After.Gold == 900 && purchase.Plan.After.Items is [{ Core.Kind: ItemKind.Talisman }] &&
                    purchase.Plan.After.ShopPurchases is [{ Count: 1 }],
                    "护石购买保留装备单件合同，零数量与复数输入均只收费、发放、累计限购一次 " + count);
            }
            bought = shop.Buy(current, new(27800, 0, 2, 1), out receipt);
            That(bought.Accepted && receipt is { Count: 1, Core.ItemId: 27800 } && receipt.Paid.Gold == current.Inventory.Gold - 3500 &&
                bought.Plan!.After.Items.Length == current.Inventory.Items.Length + 1,
                "captured zero-count equipment request charges and grants exactly one item");
            current = Commit(bought);
            That(current.Inventory.Items.Single(item => item.Core.ItemId == 27800) is { Uid: > 0, Core.Count: 1 },
                "zero-count equipment purchase persists one complete instance");
            var potion = catalog.Get(1106);
            That(potion.StackLimit == 1000 && potion.ShopPrice.Gold == 100 && itemResources.Shops.Allows(2, 15, potion.Id), "matched potion stack bound and offer");
            var splitSource = current with { Inventory = new([
                new(1001, Bag(3), InventoryRewardGrantService.Create(potion, 998)),
                new(1002, Bag(65), InventoryRewardGrantService.Create(potion, 997))], Gold: 2000) };
            var split = shop.Buy(splitSource, new(potion.Id, 10, 15, 2), out receipt);
            That(split is { Accepted: false, Error: 4, Plan: null } && receipt is null && splitSource.Inventory.Gold == 2000,
                "purchase cannot spill into later stacks and rejects without charging");
            split = shop.Buy(splitSource, new(potion.Id, 2, 15, 2), out receipt);
            That(split.Accepted && receipt!.Position == Bag(3) && receipt.Core!.Value == 1000 && receipt.Count == 2 &&
                I32(ShopPackets.Bought(split, receipt), 23) == 2, "purchase ACK carries this complete insertion count");
            That(split.Plan!.After.Gold == 1800 && split.Plan.After.Find(Bag(3)) is { Uid: 1001, Core.Value: 1000 } &&
                split.Plan.After.Find(Bag(65)) is { Uid: 1002, Core.Value: 997 } && split.Plan.After.Find(Bag(66)) is null &&
                splitSource.Inventory.Find(Bag(3))!.Core.Count == 998, "purchase fills first compatible quickbar stack and preserves all other slots");

            current = Commit(items.Move(current, new(material.Position, 3175, 0, Bag(3), 0, 0, -1, 0, 0)));
            uint gold = current.Inventory.Gold;
            var sell = new SellItemRequest(Bag(3), 3, 1, 2, 13);
            var sold = shop.Sell(current, sell);
            ack = ShopPackets.Sold(sold, sell);
            That(ack.Length == 12 && I32(ack, 1) == gold + 3 && I32(ack, 8) == 3, "sell ACK uses i32 sold count and committed gold");
            current = Commit(sold);
            That(current.Inventory.Find(Bag(3)) is { Uid: var savedUid, Core.Value: 12 } && savedUid == uid, "quickbar sale retains unsold instance");
            var locked = current.Inventory.Find(Bag(3))!;
            var lockedCharacter = current with { Inventory = current.Inventory with { Items = current.Inventory.Items.Replace(locked, locked with { Core = locked.Core with { EquipmentLockId = 2 } }) } };
            That(shop.Sell(lockedCharacter, sell) is { Accepted: false, Error: 0xd6 }, "safety locked source rejected");
            var sorted = current with { Inventory = current.Inventory with { Items = current.Inventory.Items.Replace(locked, locked with { Core = locked.Core with { SortLockFlag = 1 } }) } };
            That(shop.Sell(sorted, sell).Accepted, "sorting lock does not invent sell restriction");
            foreach (int count in new[] { -1, 0, 99 })
                That(shop.Sell(current, sell with { Count = count }) is { Accepted: true, MoveCount: 12 }, "whole stack sale convention " + count);
            That(shop.Sell(current, sell with { Position = new(InventoryListType.Equipment, 12) }) is { Accepted: true, MoveCount: 1 }, "worn equipment can be sold");
            That(!shop.Sell(current with { Inventory = current.Inventory with { Gold = int.MaxValue } }, sell).Accepted, "gold cap rejects entire sale");

            foreach (var (id, kind, slot, price) in new (int, CurrencyKind, short, int)[]
            {
                (3033, CurrencyKind.BlackCube, 354, 8), (3034, CurrencyKind.WhiteCube, 355, 8),
                (3035, CurrencyKind.RedCube, 356, 8), (3036, CurrencyKind.BlueCube, 357, 8),
                (3037, CurrencyKind.ClearCube, 358, 20), (3262, CurrencyKind.GoldCube, 359, 80)
            })
            {
                var walletOnly = current with { Inventory = InventorySnapshot.Empty with
                {
                    Gold = 1000, AccountWallet = AccountWallet.Empty.Set(kind, 2)
                } };
                var request = new SellItemRequest(Bag(slot), 1, 1, 2, 719);
                var sale = shop.Sell(walletOnly, request);
                That(catalog.Get(id).ShopPrice.SellGold == price && sale is { Accepted: true, MoveCount: 1 } &&
                    sale.Plan!.After.Gold == 1000 + price && sale.Plan.After.AccountWallet.Get(kind) == 1 && sale.Plan.After.Items.IsEmpty,
                    "wallet-only cube sale uses matching PVF price without creating an instance " + id);
                That(shop.Sell(walletOnly, request with { Count = 2 }) is { Accepted: true, MoveCount: 2, Plan: { } all } &&
                    all.After.Gold == 1000 + price * 2 && all.After.AccountWallet.Get(kind) == 0, "entire wallet cube balance can be sold " + id);
                foreach (int count in new[] { int.MinValue, -1, 0, 3, int.MaxValue })
                    That(shop.Sell(walletOnly, request with { Count = count }) is { Accepted: false, Plan: null },
                        "invalid wallet sale count cannot silently become the whole balance " + id + "/" + count);
                foreach (var list in new[] { InventoryListType.Equipment, InventoryListType.PersonalCargo, InventoryListType.AccountCargo, InventoryListType.Pet })
                    That(shop.Sell(walletOnly, request with { Position = new(list, slot) }) is { Accepted: false, Plan: null },
                        "forged cube container cannot debit wallet " + id + "/" + list);
                That(shop.Sell(walletOnly with { Inventory = walletOnly.Inventory with { AccountWallet = AccountWallet.Empty } }, request) is { Accepted: false, Plan: null },
                    "empty wallet rejects sale " + id);
                int cap = new CurrencyService(itemResources.GoldLimits).CarryLimit(walletOnly);
                var capped = walletOnly with { Inventory = walletOnly.Inventory with { Gold = checked((uint)(cap - price + 1)) } };
                That(shop.Sell(capped, request) is { Accepted: false, Error: 22, Plan: null } && capped.Inventory.AccountWallet.Get(kind) == 2,
                    "gold cap rejects the whole wallet sale without debiting cubes " + id);
                That(shop.Sell(capped with { Inventory = capped.Inventory with { Gold = checked((uint)(cap - price)) } }, request) is
                    { Accepted: true, Plan: { } exact } && exact.After.Gold == cap, "sale may reach exact carry limit " + id);
                That(walletOnly.Inventory.Gold == 1000 && walletOnly.Inventory.AccountWallet.Get(kind) == 2 && walletOnly.Inventory.Items.IsEmpty,
                    "sale planning and rejections leave the original wallet untouched " + id);
            }
            foreach (short slot in new short[] { 0, 1, 2, 353, 360, 365, 366 })
                That(shop.Sell(current with { Inventory = InventorySnapshot.Empty with
                {
                    AccountWallet = AccountWallet.Empty.Set(CurrencyKind.CommonSoul, 2).Set(CurrencyKind.Cera, 2).Set(CurrencyKind.TokenCera, 2)
                } }, new(Bag(slot), 1, 1, 2, 719)) is { Accepted: false, Plan: null }, "other virtual assets are not sellable cubes " + slot);
            That(shop.Sell(current with { Inventory = InventorySnapshot.Empty with
            {
                AccountWallet = AccountWallet.Empty.Set(CurrencyKind.ClearCube, int.MaxValue)
            } }, new(Bag(358), int.MaxValue, 1, 2, 719)) is { Accepted: false, Error: 22, Plan: null },
                "large wallet sale price multiplication cannot wrap into a small gold credit");

            current = Commit(items.Grant(current, [new(3037, 100), new(3030, 3)]));
            bought = shop.Buy(current, Offer(3033, 2), out receipt);
            That(bought.Accepted && receipt!.Materials.Single().Remaining == 90 && receipt.Paid.Gold == current.Inventory.Gold, "cube exchange charges wallet and no inferred gold from value");
            current = Commit(bought);
            That(current.Inventory.AccountWallet.Get(CurrencyKind.BlackCube) == 2 && current.Inventory.AccountWallet.Get(CurrencyKind.ClearCube) == 90,
                "virtual reward and virtual cost share account-wallet transaction");
            bought = shop.Buy(current, Offer(3137, 3), out receipt);
            That(bought.Accepted && receipt!.Materials.Single().Remaining == 0, "ordinary material exchange consumes whole source");
            current = Commit(bought);
            That(current.Inventory.Items.All(item => item.Core.ItemId != 3030) && current.Inventory.Items.Any(item => item.Core.ItemId == 3137 && item.Core.Count == 3),
                "material slot released then reused by complete reward");
            var full = current with { Inventory = current.Inventory with
            {
                Items = Enumerable.Range(9, 56).Select(slot => new InventoryItem(slot, Bag((short)slot), InventoryRewardGrantService.Create(catalog.Get(10400)))).ToImmutableArray()
            } };
            That(shop.Buy(full, new(10402, 1, 2, 1), out _) is { Accepted: false, Error: 4 }, "full equipment page rejects without charging");
            var exchangeFull = current with { Inventory = current.Inventory with
            {
                Items = Enumerable.Range(121, 56).Select(slot => new InventoryItem(slot, Bag((short)slot),
                    InventoryRewardGrantService.Create(catalog.Get(slot == 121 ? 3030 : 3299), slot == 121 ? 3 : 1))).ToImmutableArray()
            } };
            var released = shop.Buy(exchangeFull, Offer(3137, 3), out receipt);
            That(released.Accepted && released.Plan!.After.Items.Length == 56 && released.Plan.After.Find(Bag(121)) is { Uid: 0, Core.ItemId: 3137, Core.Value: 3 },
                "full material page can use space freed by its exchange cost");
            var expiringCost = exchangeFull.Inventory.Find(Bag(121))!;
            var expiredCost = exchangeFull with { Inventory = exchangeFull.Inventory with
            {
                Items = exchangeFull.Inventory.Items.Replace(expiringCost, expiringCost with { Core = expiringCost.Core with { ExpireTime = 1 } })
            } };
            That(!shop.Buy(expiredCost, Offer(3137, 3), out _).Accepted, "expired cost cannot buy a fresh reward");

            var limited = Offer(10099885, 3);
            That(catalog.Get(limited.ItemId).ShopPrice.Limit is { AccountWide: true, Count: 3, ResetDaily: true }, "account daily definition");
            current = Commit(shop.Buy(current, limited, out _, now));
            That(shop.Buy(current, limited with { Count = 1 }, out _, now) is { Accepted: false, Error: 0x85 }, "limit rejects over-cap buy");
            var sibling = repo.Create(owner, new("ShopSibling", towns.Room, towns.InitialReturn) { Level = 60, Inventory = InventorySnapshot.Empty with { Gold = 1_000_000 } });
            That(!shop.Buy(sibling, limited with { Count = 1 }, out _, now).Accepted, "new sibling sees already-consumed account quota");
            using (var stranger = owners.Acquire("shop-other", () => { }))
            {
                var foreign = repo.Create(stranger, new("ShopOther", towns.Room, towns.InitialReturn) { Level = 60, Inventory = InventorySnapshot.Empty with { Gold = 1_000_000 } });
                That(shop.Buy(foreign, limited, out _, now).Accepted, "other account has independent quota");
            }
            var nextDay = now.AddSeconds(1);
            That(ShopPurchaseCount.Today(nextDay) == ShopPurchaseCount.Today(now) + 1 && shop.PurchaseCounts(current, limited.NpcId, nextDay)!.Length == 0,
                "06:00 UTC+8 resets effective daily quota without erasing durable history");
            current = Commit(shop.Buy(current, limited with { Count = 1 }, out _, nextDay));
            That(current.Inventory.ShopPurchases.Single().Count == 1, "new day count and costs commit together");
            var charLimited = Offer(100060533, 1);
            That(catalog.Get(charLimited.ItemId).ShopPrice.Limit is { AccountWide: false, Count: 1, ResetDaily: false }, "charac alias and permanent cap");
            current = Commit(shop.Buy(current, charLimited, out _, now));
            That(!shop.Buy(current, charLimited, out _, now.AddDays(1)).Accepted, "nonreset character quota persists next day");
            sibling = repo.List(owner).Single(role => role.Id == sibling.Id);
            That(shop.Buy(sibling, charLimited, out _, now).Accepted, "character quota does not leak to sibling");
            var rows = shop.PurchaseCounts(current, charLimited.NpcId, now)!;
            That(rows.Any(row => row.ItemId == charLimited.ItemId && row.Count == 1) && ShopPackets.PurchaseCounts(rows).Length == 17 + 8 * rows.Length,
                "Client count reply includes all four required groups");
            That(shop.PurchaseCounts(current, int.MaxValue) is null && ShopPackets.PurchaseCounts([]).Length == 17, "invalid and valid empty count queries remain distinct");

            var session = new GameSession(repo, towns, owner);
            session.Select(0);
            session.MarkReady();
            session.MutateInventory(role => items.Grant(role, [new(3037, 2)]));
            current = session.Selected!;
            string snapshot = Snapshot();
            var walletSell = new SellItemRequest(Bag(358), 1, 1, 2, 719);
            Sql("CREATE TRIGGER fail_cube_sale BEFORE INSERT ON currency_audit_log WHEN NEW.action_name='npc_shop_sell' AND NEW.currency_kind=7 BEGIN SELECT RAISE(ABORT,'cube sale rollback'); END");
            Reject(() => session.MutateInventory(role => shop.Sell(role, walletSell)), "late wallet audit failure rolls back cube debit and gold credit");
            That(Snapshot() == snapshot && session.Selected == current, "failed cube sale publishes no new snapshot, revision or audit");
            Sql("DROP TRIGGER fail_cube_sale");
            var cubeBefore = current;
            session.MutateInventory(role => shop.Sell(role, walletSell));
            current = session.Selected!;
            That(current.Inventory.Gold == cubeBefore.Inventory.Gold + 20 &&
                current.Inventory.AccountWallet.Get(CurrencyKind.ClearCube) == cubeBefore.Inventory.AccountWallet.Get(CurrencyKind.ClearCube) - 1 &&
                current.Inventory.AccountWallet.Revision == cubeBefore.Inventory.AccountWallet.Revision + 1,
                "successful cube sale commits gold and wallet exactly once after rollback");
            Reject(() => repo.CommitInventory(owner, cubeBefore, shop.Sell(cubeBefore, walletSell).Plan!), "stale revision cannot repeat wallet sale");
            snapshot = Snapshot();
            Sql("CREATE TRIGGER fail_shop BEFORE INSERT ON shop_purchase_counts BEGIN SELECT RAISE(ABORT,'shop rollback'); END");
            Reject(() => session.MutateInventory(role => shop.Buy(role, Offer(10099865, 1), out _, now)), "counter failure rolls back fee, grant and quota");
            That(Snapshot() == snapshot && session.Selected == current, "failed transaction publishes no new session state or audit");
            Sql("DROP TRIGGER fail_shop");
            var restored = new CharacterRepository(path).List(owner).First();
            That(ShopPurchaseCount.Same(restored.Inventory.ShopPurchases, current.Inventory.ShopPurchases) && restored.Inventory.Items.SequenceEqual(current.Inventory.Items),
                "restart restores quotas, UID and complete items");
            That(restored.Inventory.Gold == current.Inventory.Gold && restored.Inventory.AccountWallet.Same(current.Inventory.AccountWallet),
                "reopening repository restores the committed cube sale and wallet revision");
            Sql("UPDATE shop_purchase_counts SET count=count+1 WHERE item_id=10099885");
            Reject(() => repo.CommitInventory(owner, current, shop.Buy(current, new(3175, 1, 2, 1), out _).Plan!),
                "external shared quota mutation without character revision cannot be overwritten");
            using var replacement = owners.Acquire(owner.Account, () => { });
            Reject(() => session.MutateInventory(role => shop.Buy(role, new(3175, 1, 2, 1), out _)), "old generation cannot buy");
            That(Query("PRAGMA integrity_check") is "ok", "shop persistence integrity");

            That(ItemShopPrice.Parse(new("[value]\n900\n[need material]\n3037 2\n"), false, 200).Gold == 0, "exchange value never becomes a gold fee");
            That(ItemShopPrice.Parse(new("[price]\n100\n[add price]\n-20\n[need material]\n3037 2\n"), false, 200).Gold == 80, "signed exchange price correction");
            var expires = ItemExpiration.Parse(new("[expiration date]\n`2026-10-04 06:00:00`\n"));
            That(expires.AbsoluteTime == nextDay.ToUnixTimeSeconds(), "absolute PVF expiry is UTC+8");
            var timed = catalog.Get(3175) with { Expiration = new(expires.AbsoluteTime, 2) };
            That(InventoryRewardGrantService.Create(timed, 1, now).ExpireTime == now.AddDays(2).ToUnixTimeSeconds(), "usable days takes creation-relative expiry");
            LegacyExpiryFixture.Restore(catalog, (10005759, "2015-07-30 06:00:00"));
            That(!InventoryRewardGrantService.TryGrantBatch(current, [new(10005759, 1)], catalog, itemResources.GoldLimits, itemResources.EpicPieces).Accepted,
                "matched expired event reward is not turned into a permanent item");
            That(ShopRequests.Buy(new byte[16]) == new BuyItemRequest(0, 0, 0, 0), "purchase request width");
            That(ShopRequests.Sell(new byte[19]).Check == 0 && ShopRequests.PurchaseCounts(new byte[4]) == 0, "sell and quota request widths");
            Reject(() => ShopRequests.Buy(new byte[15]), "short purchase rejected");
            Reject(() => ShopRequests.Sell(new byte[18]), "short sale rejected");
            Reject(() => ShopRequests.PurchaseCounts(new byte[5]), "quota request trailing bytes rejected");

            BuyItemRequest Offer(int id, int count)
            {
                var pair = itemResources.Shops.Shops.First(pair => pair.Value.Items.Contains(id));
                return new(id, count, pair.Key, pair.Value.NpcId);
            }
            Character Commit(InventoryResult result)
            {
                That(result.Accepted, "accepted planned operation");
                return InventoryService.Commit(repo, owner, current, result);
            }
            object? Query(string sql)
            {
                using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False;Foreign Keys=True");
                db.Open();
                using var command = db.CreateCommand();
                command.CommandText = sql;
                return command.ExecuteScalar();
            }
            void Sql(string sql) => Query(sql);
            string Snapshot() => JsonSerializer.Serialize(repo.List(owner)) + Query("SELECT count(*) FROM inventory_audit_log") + ":" + Query("SELECT count(*) FROM currency_audit_log");
        }
        finally { Directory.Delete(root, true); }
        Console.WriteLine($"PASS {Count - checks} NPC shop assertions");
    }
    static InventorySlot Bag(short index) => new(InventoryListType.Main, index);
    static int I32(byte[] bytes, int offset) => BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset));
}
