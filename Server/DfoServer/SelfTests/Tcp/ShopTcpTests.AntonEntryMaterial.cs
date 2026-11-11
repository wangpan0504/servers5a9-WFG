using DfoServer.Game.Accounts;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Persistence;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static partial class ShopTcpTests
{
    public static async Task RunAntonEntryMaterialAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        const int item = 10096296, material = 10093971, goldCube = 3262;
        var items = resources.ItemResources;
        That(items.Shops.Shops[257].Raid && items.Shops.Shops[257].Items.Contains(item) &&
            !items.Shops.Shops[108].Items.Contains(item), "阻断装置仅在迁移后的安徒恩商店售卖，奈恩已移除");
        var stock = resources.Archive.ReadTokens("itemshop/raidshop_anton.shp");
        int first = Enumerable.Range(0, stock.Length).First(at => stock[at].Type == 3 &&
            resources.Archive.ResolveString(stock[at].Value) == "[item list]");
        That(stock[first + 1].Type == 0 && stock[first + 1].Value == item, "安徒恩第一页第一格为原装置");
        var price = items.Items.Get(item).ShopPrice;
        That(price.Gold == 200_000 && price.Materials.Length == 2 &&
            price.Materials.Any(row => row.ItemId == material && row.Count == 50) &&
            price.Materials.Any(row => row.ItemId == goldCube && row.Count == 100), "保留20万金币及原生50/100双材料价格");
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "anton-entry-material"), resources: resources);
        var repo = new CharacterRepository(fixture.Database);
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var role = repo.Create(owner, new("AntonDevice", resources.Towns.Room, resources.Towns.InitialReturn)
            { Level = 95, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(0).Skills,
                Inventory = InventorySnapshot.Empty with { Gold = 1_000_000 } });
            InventoryService.Commit(repo, owner, role, new InventoryService(items.Items, items.GoldLimits, items.EpicPieces)
                .Grant(role, [new(material, 99), new(goldCube, 200)]));
        }
        await fixture.StartAsync();
        using (var peer = Open(fixture))
        {
            peer.Select();peer.Barrier();
            string before = State();
            Buy(peer, item, 1, 108, 326);
            That(peer.Expect(1, 21).Body is [0, ..], "从旧奈恩店伪造购买被拒绝");
            peer.Barrier();That(State() == before, "旧店拒绝不改任何资产或审计");
            Buy(peer, item, 1, 257, 328);
            var ack = peer.Expect(1, 21).Body;
            That(ack.Length == 157 && ack[0] == 1 && U32(ack, 1) == 800_000 && U32(ack, 19) == item && U32(ack, 23) == 1,
                "新店真实CMD21成功并返回金币和装置");
            peer.Expect(0, 14);peer.Barrier();CheckCommitted();
            string paid = State();
            Buy(peer, item, 1, 257, 328);
            peer.Expect(1, 21, [0, 10, 0]);peer.Barrier();
            That(State() == paid, "第二次材料不足时金币、双材料、装置与审计全部不变");
        }
        using (var peer = Open(fixture)) { peer.Select();peer.Barrier();CheckCommitted(); }
        await fixture.StopAsync();
        That(fixture.Query("PRAGMA integrity_check") is [["ok"]] && fixture.Query("PRAGMA foreign_key_check").Length == 0,
            "装置迁移购买后数据库和外键完整");

        void CheckCommitted()
        {
            That(fixture.Query("SELECT gold FROM characters WHERE id=1") is [[800000L]] &&
                fixture.Query("SELECT balance FROM account_currency_balances WHERE account=$p0 AND currency_kind=$p1",
                    fixture.Options.DevelopmentAccount, (int)CurrencyKind.GoldCube) is [[100L]] &&
                fixture.Query("SELECT json_extract(i.item_core,'$.ItemId'),json_extract(i.item_core,'$.Value') " +
                    "FROM character_inventory_slots s JOIN character_inventory_items i ON i.item_uid=s.item_uid " +
                    "WHERE s.character_id=1 AND s.list_type=0 ORDER BY json_extract(i.item_core,'$.ItemId')")
                is [[10093971L, 49L], [10096296L, 1L]], "购买和重连持久化准确扣20万、50材料和100金色小晶块，只发一件装置");
        }
        string State() => fixture.Snapshot("SELECT * FROM characters ORDER BY id") +
            fixture.Snapshot("SELECT * FROM account_wallets ORDER BY account") +
            fixture.Snapshot("SELECT * FROM account_currency_balances ORDER BY account,currency_kind") +
            fixture.Snapshot("SELECT * FROM currency_audit_log ORDER BY audit_id") +
            fixture.Snapshot("SELECT * FROM character_inventory_items ORDER BY item_uid") +
            fixture.Snapshot("SELECT * FROM character_inventory_slots ORDER BY item_uid") +
            fixture.Snapshot("SELECT * FROM inventory_audit_log ORDER BY audit_id");
    }
}
