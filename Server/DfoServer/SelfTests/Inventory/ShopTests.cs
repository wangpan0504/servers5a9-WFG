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
        int checks = 0;
        void Check(bool value, string name)
        {
            checks++;
            if (!value)
                throw new InvalidOperationException("FAIL shop: " + name);
        }
        void Reject(Action action, string name)
        {
            bool rejected = false;
            try { action(); }
            catch (Exception e) when (e is SqliteException or InvalidDataException or InvalidOperationException) { rejected = true; }
            Check(rejected, name);
        }
        var catalog = itemResources.Items;
        var shop = new InventoryShopService(catalog, itemResources.Shops, itemResources.GoldLimits, itemResources.EpicPieces);
        var items = new InventoryService(catalog, itemResources.GoldLimits, itemResources.EpicPieces);
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
            Check(itemResources.Shops.Allows(1, 2, 3175) && !itemResources.Shops.Allows(3, 2, 3175) && !itemResources.Shops.Allows(1, 2, 51), "catalog checks shop, NPC and offered item");
            Check(itemResources.Shops.Shops.ContainsKey(15), "region-prefixed shop file is resolved");
            Check(catalog.Get(3175).ShopPrice is { Gold: 10, SellGold: 1 } && catalog.Get(10400).ShopPrice.SellGold == 64, "matching PVF stack and Client equipment sell rates");
            foreach (int id in new[] { 16400, 18400 })
            {
                var template = catalog.Get(id);
                foreach (var (durability, expected) in new (ushort, int)[] { (20, 25), (10, 12), (0, 0), (30, 25) })
                {
                    var item = new InventoryItem(0, Bag(36), InventoryRewardGrantService.Create(template) with { Durability = durability });
                    var source = current with { Inventory = current.Inventory with { Items = [item] } };
                    var sale = shop.Sell(source, new(Bag(36), 1, 1, 2, 75));
                    Check(sale.Accepted && sale.Plan!.After.Gold == current.Inventory.Gold + expected && sale.Plan.After.Items.IsEmpty,
                        "native equipment sale gold and deletion " + id + "/" + durability);
                }
            }
            Check(catalog.Get(10400).ShopPrice.GetSellGold(16, 33) == 30, "fractional durability truncates before the 90-percent adjustment");
            Check(catalog.Get(16400).ShopPrice.GetSellGold(0, 0) == 25, "equipment without maximum durability keeps its base sale price");
            foreach (int value in new[] { 0, 1 })
                Check(ItemShopPrice.Parse(new("[value]\n" + value), true, 200).GetSellGold(1, 1) == 0, "zero sale does not invent a one-gold minimum " + value);
            Check(ItemShopPrice.Parse(new("[value]\n2147483647"), true, int.MaxValue).GetSellGold(1, 1) == int.MaxValue,
                "large equipment price cannot wrap into a cheap sale");
            var bought = shop.Buy(current, new(3175, 10, 2, 1), out var receipt);
            var ack = ShopPackets.Bought(bought, receipt);
            Check(ack.Length == 141 && ack[0] == 1 && I32(ack, 1) == 999900 && I32(ack, 19) == 3175 && I32(ack, 23) == 10 && ack[140] == 0,
                "buy ACK four scalars, complete 123-byte entry and empty material list");
            current = Commit(bought);
            var material = current.Inventory.Items.Single(item => item.Core.ItemId == 3175);
            long uid = material.Uid;
            var stale = current;
            bought = shop.Buy(current, new(3175, 5, 2, 1), out receipt);
            Check(receipt!.Core!.Value == 15 && I32(ShopPackets.Bought(bought, receipt), 23) == 5 && bought.Plan!.After.Find(material.Position)!.Core.Value == 15,
                "domain receipt keeps complete core while builder projects only the purchase increment");
            current = Commit(bought);
            Check(current.Inventory.Find(material.Position)!.Uid == uid, "repeated valid buys retain target UID and each charge once");
            Reject(() => repo.CommitInventory(owner, stale, shop.Buy(stale, new(3175, 1, 2, 1), out _).Plan!), "stale revision cannot buy");
            Check(shop.Buy(current, new(3175, int.MaxValue, 2, 1), out _) is { Accepted: false, Error: 0x85 }, "huge count rejected before costs");
            Check(!shop.Buy(current, new(3175, 1, 2, 3), out _).Accepted, "forged NPC rejected");
            foreach (int count in new[] { int.MinValue, -1, 0, 1_000_001, int.MaxValue })
                foreach (int id in new[] { 3175, 10402 })
                    Check(shop.Buy(current, new(id, count, 2, 1), out receipt) is { Accepted: false, Error: 0x85, Plan: null } && receipt is null,
                        "invalid purchase count does not become a one-item charge " + id + "/" + count);
            Check(shop.Buy(current with { Job = 15, Level = 1 }, new(10402, 99, 2, 1), out receipt).Accepted && receipt!.Count == 1,
                "equipment buys one without inventing profession or wear-level purchase constraints");
            var potion = catalog.Get(1106);
            Check(potion.StackLimit == 1000 && potion.ShopPrice.Gold == 100 && itemResources.Shops.Allows(2, 15, potion.Id), "matched potion stack bound and offer");
            var splitSource = current with { Inventory = new([
                new(1001, Bag(3), InventoryRewardGrantService.Create(potion, 998)),
                new(1002, Bag(65), InventoryRewardGrantService.Create(potion, 997))], Gold: 2000) };
            var split = shop.Buy(splitSource, new(potion.Id, 10, 15, 2), out receipt);
            Check(split is { Accepted: false, Error: 4, Plan: null } && receipt is null && splitSource.Inventory.Gold == 2000,
                "purchase cannot spill into later stacks and rejects without charging");
            split = shop.Buy(splitSource, new(potion.Id, 2, 15, 2), out receipt);
            Check(split.Accepted && receipt!.Position == Bag(3) && receipt.Core!.Value == 1000 && receipt.Count == 2 &&
                I32(ShopPackets.Bought(split, receipt), 23) == 2, "purchase ACK carries this complete insertion count");
            Check(split.Plan!.After.Gold == 1800 && split.Plan.After.Find(Bag(3)) is { Uid: 1001, Core.Value: 1000 } &&
                split.Plan.After.Find(Bag(65)) is { Uid: 1002, Core.Value: 997 } && split.Plan.After.Find(Bag(66)) is null &&
                splitSource.Inventory.Find(Bag(3))!.Core.Count == 998, "purchase fills first compatible quickbar stack and preserves all other slots");

            current = Commit(items.Move(current, new(material.Position, 3175, 0, Bag(3), 0, 0, -1, 0, 0)));
            uint gold = current.Inventory.Gold;
            var sell = new SellItemRequest(Bag(3), 3, 1, 2, 13);
            var sold = shop.Sell(current, sell);
            ack = ShopPackets.Sold(sold, sell);
            Check(ack.Length == 12 && I32(ack, 1) == gold + 3 && I32(ack, 8) == 3, "sell ACK uses i32 sold count and committed gold");
            current = Commit(sold);
            Check(current.Inventory.Find(Bag(3)) is { Uid: var savedUid, Core.Value: 12 } && savedUid == uid, "quickbar sale retains unsold instance");
            var locked = current.Inventory.Find(Bag(3))!;
            var lockedCharacter = current with { Inventory = current.Inventory with { Items = current.Inventory.Items.Replace(locked, locked with { Core = locked.Core with { EquipmentLockId = 2 } }) } };
            Check(shop.Sell(lockedCharacter, sell) is { Accepted: false, Error: 0xd6 }, "safety locked source rejected");
            var sorted = current with { Inventory = current.Inventory with { Items = current.Inventory.Items.Replace(locked, locked with { Core = locked.Core with { SortLockFlag = 1 } }) } };
            Check(shop.Sell(sorted, sell).Accepted, "sorting lock does not invent sell restriction");
            foreach (int count in new[] { -1, 0, 99 })
                Check(shop.Sell(current, sell with { Count = count }) is { Accepted: true, MoveCount: 12 }, "whole stack sale convention " + count);
            Check(shop.Sell(current, sell with { Position = new(InventoryListType.Equipment, 12) }) is { Accepted: true, MoveCount: 1 }, "worn equipment can be sold");
            Check(!shop.Sell(current with { Inventory = current.Inventory with { Gold = int.MaxValue } }, sell).Accepted, "gold cap rejects entire sale");

            current = Commit(items.Grant(current, [new(3037, 100), new(3030, 3)]));
            bought = shop.Buy(current, Offer(3033, 2), out receipt);
            Check(bought.Accepted && receipt!.Materials.Single().Remaining == 90 && receipt.Paid.Gold == current.Inventory.Gold, "cube exchange charges wallet and no inferred gold from value");
            current = Commit(bought);
            Check(current.Inventory.AccountWallet.Get(CurrencyKind.BlackCube) == 2 && current.Inventory.AccountWallet.Get(CurrencyKind.ClearCube) == 90,
                "virtual reward and virtual cost share account-wallet transaction");
            bought = shop.Buy(current, Offer(3137, 3), out receipt);
            Check(bought.Accepted && receipt!.Materials.Single().Remaining == 0, "ordinary material exchange consumes whole source");
            current = Commit(bought);
            Check(current.Inventory.Items.All(item => item.Core.ItemId != 3030) && current.Inventory.Items.Any(item => item.Core.ItemId == 3137 && item.Core.Count == 3),
                "material slot released then reused by complete reward");
            var full = current with { Inventory = current.Inventory with
            {
                Items = Enumerable.Range(9, 56).Select(slot => new InventoryItem(slot, Bag((short)slot), InventoryRewardGrantService.Create(catalog.Get(10400)))).ToImmutableArray()
            } };
            Check(shop.Buy(full, new(10402, 1, 2, 1), out _) is { Accepted: false, Error: 4 }, "full equipment page rejects without charging");
            var exchangeFull = current with { Inventory = current.Inventory with
            {
                Items = Enumerable.Range(121, 56).Select(slot => new InventoryItem(slot, Bag((short)slot),
                    InventoryRewardGrantService.Create(catalog.Get(slot == 121 ? 3030 : 3299), slot == 121 ? 3 : 1))).ToImmutableArray()
            } };
            var released = shop.Buy(exchangeFull, Offer(3137, 3), out receipt);
            Check(released.Accepted && released.Plan!.After.Items.Length == 56 && released.Plan.After.Find(Bag(121)) is { Uid: 0, Core.ItemId: 3137, Core.Value: 3 },
                "full material page can use space freed by its exchange cost");
            var expiringCost = exchangeFull.Inventory.Find(Bag(121))!;
            var expiredCost = exchangeFull with { Inventory = exchangeFull.Inventory with
            {
                Items = exchangeFull.Inventory.Items.Replace(expiringCost, expiringCost with { Core = expiringCost.Core with { ExpireTime = 1 } })
            } };
            Check(!shop.Buy(expiredCost, Offer(3137, 3), out _).Accepted, "expired cost cannot buy a fresh reward");

            var limited = Offer(10099885, 3);
            Check(catalog.Get(limited.ItemId).ShopPrice.Limit is { AccountWide: true, Count: 3, ResetDaily: true }, "account daily definition");
            current = Commit(shop.Buy(current, limited, out _, now));
            Check(shop.Buy(current, limited with { Count = 1 }, out _, now) is { Accepted: false, Error: 0x85 }, "limit rejects over-cap buy");
            var sibling = repo.Create(owner, new("ShopSibling", towns.Room, towns.InitialReturn) { Level = 60, Inventory = InventorySnapshot.Empty with { Gold = 1_000_000 } });
            Check(!shop.Buy(sibling, limited with { Count = 1 }, out _, now).Accepted, "new sibling sees already-consumed account quota");
            using (var stranger = owners.Acquire("shop-other", () => { }))
            {
                var foreign = repo.Create(stranger, new("ShopOther", towns.Room, towns.InitialReturn) { Level = 60, Inventory = InventorySnapshot.Empty with { Gold = 1_000_000 } });
                Check(shop.Buy(foreign, limited, out _, now).Accepted, "other account has independent quota");
            }
            var nextDay = now.AddSeconds(1);
            Check(ShopPurchaseCount.Today(nextDay) == ShopPurchaseCount.Today(now) + 1 && shop.PurchaseCounts(current, limited.NpcId, nextDay)!.Length == 0,
                "06:00 UTC+8 resets effective daily quota without erasing durable history");
            current = Commit(shop.Buy(current, limited with { Count = 1 }, out _, nextDay));
            Check(current.Inventory.ShopPurchases.Single().Count == 1, "new day count and costs commit together");
            var charLimited = Offer(100060533, 1);
            Check(catalog.Get(charLimited.ItemId).ShopPrice.Limit is { AccountWide: false, Count: 1, ResetDaily: false }, "charac alias and permanent cap");
            current = Commit(shop.Buy(current, charLimited, out _, now));
            Check(!shop.Buy(current, charLimited, out _, now.AddDays(1)).Accepted, "nonreset character quota persists next day");
            sibling = repo.List(owner).Single(role => role.Id == sibling.Id);
            Check(shop.Buy(sibling, charLimited, out _, now).Accepted, "character quota does not leak to sibling");
            var rows = shop.PurchaseCounts(current, charLimited.NpcId, now)!;
            Check(rows.Any(row => row.ItemId == charLimited.ItemId && row.Count == 1) && ShopPackets.PurchaseCounts(rows).Length == 17 + 8 * rows.Length,
                "Client count reply includes all four required groups");
            Check(shop.PurchaseCounts(current, int.MaxValue) is null && ShopPackets.PurchaseCounts([]).Length == 17, "invalid and valid empty count queries remain distinct");

            var session = new GameSession(repo, towns, owner);
            session.Select(0);
            session.MarkReady();
            current = session.Selected!;
            string snapshot = Snapshot();
            Sql("CREATE TRIGGER fail_shop BEFORE INSERT ON shop_purchase_counts BEGIN SELECT RAISE(ABORT,'shop rollback'); END");
            Reject(() => session.MutateInventory(role => shop.Buy(role, Offer(10099865, 1), out _, now)), "counter failure rolls back fee, grant and quota");
            Check(Snapshot() == snapshot && session.Selected == current, "failed transaction publishes no new session state or audit");
            Sql("DROP TRIGGER fail_shop");
            var restored = new CharacterRepository(path).List(owner).First();
            Check(ShopPurchaseCount.Same(restored.Inventory.ShopPurchases, current.Inventory.ShopPurchases) && restored.Inventory.Items.SequenceEqual(current.Inventory.Items),
                "restart restores quotas, UID and complete items");
            Sql("UPDATE shop_purchase_counts SET count=count+1 WHERE item_id=10099885");
            Reject(() => repo.CommitInventory(owner, current, shop.Buy(current, new(3175, 1, 2, 1), out _).Plan!),
                "external shared quota mutation without character revision cannot be overwritten");
            using var replacement = owners.Acquire(owner.Account, () => { });
            Reject(() => session.MutateInventory(role => shop.Buy(role, new(3175, 1, 2, 1), out _)), "old generation cannot buy");
            Check(Query("PRAGMA integrity_check") is "ok", "shop persistence integrity");

            Check(ItemShopPrice.Parse(new("[value]\n900\n[need material]\n3037 2\n"), false, 200).Gold == 0, "exchange value never becomes a gold fee");
            Check(ItemShopPrice.Parse(new("[price]\n100\n[add price]\n-20\n[need material]\n3037 2\n"), false, 200).Gold == 80, "signed exchange price correction");
            var expires = ItemExpiration.Parse(new("[expiration date]\n`2026-10-04 06:00:00`\n"));
            Check(expires.AbsoluteTime == nextDay.ToUnixTimeSeconds(), "absolute PVF expiry is UTC+8");
            var timed = catalog.Get(3175) with { Expiration = new(expires.AbsoluteTime, 2) };
            Check(InventoryRewardGrantService.Create(timed, 1, now).ExpireTime == now.AddDays(2).ToUnixTimeSeconds(), "usable days takes creation-relative expiry");
            Check(!InventoryRewardGrantService.TryGrantBatch(current, [new(10005759, 1)], catalog, itemResources.GoldLimits, itemResources.EpicPieces).Accepted,
                "matched expired event reward is not turned into a permanent item");
            Check(ShopRequests.Buy(new byte[16]) == new BuyItemRequest(0, 0, 0, 0), "purchase request width");
            Check(ShopRequests.Sell(new byte[19]).Check == 0 && ShopRequests.PurchaseCounts(new byte[4]) == 0, "sell and quota request widths");
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
                Check(result.Accepted, "accepted planned operation");
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
        Console.WriteLine($"PASS {checks} NPC shop assertions");
    }
    static InventorySlot Bag(short index) => new(InventoryListType.Main, index);
    static int I32(byte[] bytes, int offset) => BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset));
}
