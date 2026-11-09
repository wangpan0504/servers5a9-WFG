using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Persistence;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static partial class ShopTcpTests
{
    static async Task VerifyAntonRaidExchangeAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        const int shop = 257, npc = 328, item = 10304374, material = 10095702;
        var items = resources.ItemResources;
        // 原始资源的安徒恩店为空；恢复候选的非空店必须实际走兑换，不能因代表商品缺失而跳过。
        if (!items.Shops.Shops.TryGetValue(shop, out var catalog) || catalog.Items.Count == 0)
        {
            Console.WriteLine(JsonSerializer.Serialize(new { kind = "anton-shop-exchange", shop, status = "not-present-in-resource" }));
            return;
        }
        That(catalog.Raid && catalog.Items.Contains(item), "非空安徒恩257攻坚商店必须包含代表荒古遗尘武器礼盒");
        var price = items.Items.Get(item).ShopPrice;
        That(price.Gold == 0 && price.Materials.Length == 1 &&
            price.Materials[0].ItemId == material && price.Materials[0].Count == 5,
            "真实荒古礼盒模板固定以五个安徒恩灵魂碎片兑换");
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "shop-anton"), resources: resources);
        var repo = new CharacterRepository(fixture.Database);
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var role = repo.Create(owner, new("ShopAnton", resources.Towns.Room, resources.Towns.InitialReturn)
            {
                Level = 95, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(0).Skills,
                Inventory = InventorySnapshot.Empty with { Gold = 1_000_000 }
            });
            InventoryService.Commit(repo, owner, role,
                new InventoryService(items.Items, items.GoldLimits, items.EpicPieces).Grant(role, [new(material, 9)]));
        }
        short materialSlot = checked((short)(long)fixture.Query(
            "SELECT slot_index FROM character_inventory_slots WHERE character_id=1 AND list_type=0")[0][0]);
        await fixture.StartAsync();
        using (var peer = Open(fixture))
        {
            peer.Select();
            Buy(peer, item, 1, shop, npc);
            var ack = peer.Expect(1, 21).Body;
            That(ack.Length == 149 && ack[0] == 1 && U32(ack, 1) == 1_000_000 &&
                U32(ack, 19) == item && U32(ack, 23) == 1 && ack[140] == 1 &&
                U32(ack, 141) == material && U32(ack, 145) == 4,
                "257店真实CMD21成功回执给出单件礼盒、金币不变及九扣五余四材料");
            var delta = peer.Expect(0, 14).Body;
            That(delta[0] == 0 && U16(delta, 1) == 2 && delta.Length == 249,
                "安徒恩兑换成功回执后发布完整材料与奖励两格差量");
            Entry(delta, materialSlot, material, 4);
            Entry(delta, checked((short)U16(ack, 17)), item, 1);
            peer.Barrier();
            CheckCommitted();
            string paid = State();
            Buy(peer, item, 1, shop, npc);
            peer.Expect(1, 21, [0, 10, 0]);
            peer.Barrier();
            That(State() == paid, "仅余四个灵魂碎片时拒绝再次兑换，全部资产、额度和审计原样保留");
        }
        using (var peer = Open(fixture))
        {
            peer.Select();
            peer.Barrier();
            CheckCommitted();
        }
        await fixture.StopAsync();
        That(fixture.Query("PRAGMA integrity_check") is [["ok"]] && fixture.Query("PRAGMA foreign_key_check").Length == 0,
            "安徒恩兑换及不足材料拒绝后的数据库与外键完整");
        Console.WriteLine(JsonSerializer.Serialize(new { kind = "anton-shop-exchange", shop, item, material,
            status = "executed", granted = 1, materialSpent = 5, materialRemaining = 4, insufficientRejected = true }));

        void CheckCommitted()
        {
            That(fixture.Query("SELECT json_extract(i.item_core,'$.ItemId'),json_extract(i.item_core,'$.Value') " +
                "FROM character_inventory_slots s JOIN character_inventory_items i ON i.item_uid=s.item_uid " +
                "WHERE s.character_id=1 AND s.list_type=0 ORDER BY json_extract(i.item_core,'$.ItemId')")
                is [[10095702L, 4L], [10304374L, 1L]] &&
                fixture.Query("SELECT gold FROM characters WHERE id=1") is [[1000000L]],
                "实际持久化及重连后均仅有四个灵魂碎片与一个荒古礼盒，金币保持不变");
        }
        string State() => fixture.Snapshot("SELECT * FROM characters ORDER BY id") +
            fixture.Snapshot("SELECT * FROM account_wallets ORDER BY account") +
            fixture.Snapshot("SELECT * FROM account_currency_balances ORDER BY account,currency_kind") +
            fixture.Snapshot("SELECT * FROM currency_audit_log ORDER BY audit_id") +
            fixture.Snapshot("SELECT * FROM character_inventory_items ORDER BY item_uid") +
            fixture.Snapshot("SELECT * FROM character_inventory_slots ORDER BY item_uid") +
            fixture.Snapshot("SELECT * FROM shop_purchase_counts ORDER BY item_id") +
            fixture.Snapshot("SELECT * FROM item_purchase_counts ORDER BY item_id") +
            fixture.Snapshot("SELECT * FROM inventory_audit_log ORDER BY audit_id");
    }
}
