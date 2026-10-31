using DfoServer.Game.Accounts;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Persistence;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static partial class ShopTcpTests
{
    static async Task VerifyPeriodicLimitsAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "shop-periodic"), resources: resources);
        var items = resources.ItemResources;
        var repo = new CharacterRepository(fixture.Database);
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            foreach (string name in new[] { "QuotaTcpOne", "QuotaTcpTwo" })
            {
                var role = repo.Create(owner, new(name, resources.Towns.Room, resources.Towns.InitialReturn)
                { Level = 95, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(0).Skills });
                InventoryService.Commit(repo, owner, role,
                    new InventoryService(items.Items, items.GoldLimits, items.EpicPieces).Grant(role, [new(10306750, 1000)]));
            }
        }
        await fixture.StartAsync();
        var products = new[] { (Item: 10309042, Count: 12), (Item: 490012239, Count: 4), (Item: 490012565, Count: 3) };
        using (var peer = Open(fixture))
        {
            peer.Select();
            Counts(peer, 0);
            string before = State();
            fixture.Sql("CREATE TRIGGER fail_tcp_period AFTER INSERT ON item_purchase_counts BEGIN SELECT RAISE(ABORT,'period quota rollback'); END");
            Buy(peer, products[0].Item, 1, 274, 626);
            peer.Closed();
            That(State() == before, "新商品周期额度晚失败关闭连接且不扣材料、不发礼盒、不占额度");
            fixture.Sql("DROP TRIGGER fail_tcp_period");
        }
        using (var peer = Open(fixture))
        {
            peer.Select();
            for (int at = 0; at < products.Length; at++)
            {
                var product = products[at];
                Buy(peer, product.Item, product.Count, 274, 626);
                var ack = peer.Expect(1, 21).Body;
                That(ack[0] == 1 && U32(ack, 19) == product.Item && U32(ack, 23) == product.Count,
                    "实际715/21链购买完整漩涡限购商品并按原生增量回复");
                var delta = peer.Expect(0, 14).Body;
                That(delta[0] == 0 && U16(delta, 1) == 2 && delta.Length == 249 &&
                    Enumerable.Range(0, 2).Select(index => 3 + index * 123)
                        .Any(offset => U32(delta, offset + 2) == product.Item && U32(delta, offset + 6) == product.Count),
                    "购买ACK后发送完整奖励与扣材两格差量，不能跳过未校验库存包");
                peer.Barrier();
                That(fixture.Query("SELECT character_id,period,count FROM item_purchase_counts WHERE item_id=$p0", product.Item)
                    is [[0L, 2L, long count]] && count == product.Count, "账号周商品额度持久保存且不归某一个角色");
                Counts(peer, at + 1);
                string before = State();
                Buy(peer, product.Item, 1, 274, 626);
                peer.Expect(1, 21, [0, 0x85, 0]);
                peer.Barrier();
                That(State() == before, "超周限购请求不修改费用、礼盒及额度");
            }
        }
        using (var peer = Open(fixture))
        {
            peer.Select(1);
            Counts(peer, products.Length);
            Buy(peer, products[0].Item, 1, 274, 624);
            peer.Expect(1, 21, [0, 0x85, 0]);
            peer.Barrier();
            That(fixture.Query("SELECT sum(count) FROM item_purchase_counts") is [[19L]],
                "重连换角换商人仍共享同一账号的19件周额度");
        }
        await fixture.StopAsync();
        That(fixture.Query("PRAGMA integrity_check") is [["ok"]] && fixture.Query("PRAGMA foreign_key_check").Length == 0,
            "商品周期限购TCP事务后完整性与外键检查通过");

        void Counts(TcpPeer peer, int count)
        {
            peer.Send(715, Build(writer => writer.Write(626)));
            peer.Expect(1, 715, Build(writer =>
            {
                writer.Write((byte)1);
                writer.Write(0);
                writer.Write(count);
                foreach (var product in products.Take(count)) { writer.Write(product.Item); writer.Write(product.Count); }
                writer.Write(0);
                writer.Write(0);
            }));
        }
        string State() => fixture.Snapshot("SELECT id,revision,gold FROM characters ORDER BY id") +
            fixture.Snapshot("SELECT item_uid,item_core FROM character_inventory_items ORDER BY item_uid") +
            fixture.Snapshot("SELECT * FROM character_inventory_slots ORDER BY item_uid") +
            fixture.Snapshot("SELECT * FROM item_purchase_counts ORDER BY item_id") +
            fixture.Snapshot("SELECT * FROM inventory_audit_log ORDER BY audit_id");
    }
}
