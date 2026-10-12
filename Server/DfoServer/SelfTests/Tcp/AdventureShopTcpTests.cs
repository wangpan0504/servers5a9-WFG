using Microsoft.Data.Sqlite;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static class AdventureShopTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        int checks = Count;
        await using var fixture = new TcpTestFixture(source with { DevelopmentDungeonEntryProbe = DfoServer.Development.Dungeons.DungeonEntryProbeMode.Disabled },
            Path.Combine(root, "adventure-shop"), resources: resources);
        await fixture.StartAsync();
        var catalog = resources.Adventure;
        var product = catalog.Products.Values.Where(row => row.Limit > 1).OrderBy(row => row.ItemId).First(row =>
        {
            var template = resources.ItemResources.Items.Get(row.ItemId);
            return template.Kind == ItemKind.Consumable && template.StackLimit >= 3 &&
                !InventoryRewardGrantService.Create(template).IsExpired(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        });
        var funds = Build(writer => { for (byte i = 0; i < 5; i++) writer.Write(catalog.Shops.GetValueOrDefault(i)?.Maximum ?? 0u); });
        using (var peer = Open(fixture, "advshop"))
        {
            peer.Send(1414, Purchase(product.ItemId)); peer.Expect(1, 1414, [0, 3, 0]);
            peer.Create("AdvShop"); peer.Select();
            Execute("UPDATE account_adventures SET level=35,experience=0,points=$points", funds);
            peer.Send(1414, Purchase(product.Index)); peer.Expect(1, 1414, [1]);
            var granted = peer.Expect(0, 14).Body; peer.Expect(0, 1343); peer.Expect(0, 1349); peer.Barrier();
            That(granted[0] == 0 && U16(granted, 1) == 1 && fixture.Query("SELECT count FROM account_adventure_purchases") is [[1L]],
                "原生商品声明序号购买一件，实际差量及限购同时提交");
            Execute("UPDATE account_adventure_purchases SET count=" + product.Limit);
            peer.Send(1414, Purchase(product.ItemId)); peer.Expect(1, 1414, [0, 19, 0]); peer.Barrier();
            Execute("UPDATE account_adventure_purchases SET count=1; UPDATE account_adventures SET points=zeroblob(20)");
            peer.Send(1414, Purchase(product.ItemId)); peer.Expect(1, 1414, [0, 22, 0]); peer.Barrier();
            Execute("UPDATE account_adventures SET points=$points", funds);
            string before = Snapshot();
            Execute("CREATE TRIGGER reject_purchase BEFORE INSERT ON account_adventure_receipts BEGIN SELECT RAISE(ABORT,'late failure'); END");
            peer.Send(1414, Purchase(product.ItemId)); peer.Closed();
            That(Snapshot() == before, "真实1414晚期SQL失败关闭连接但未发送成功或留下半份物品费用");
            Execute("DROP TRIGGER reject_purchase");
        }
        await fixture.StopAsync();
        await fixture.StartAsync("shop-restarted", isolatedProcess: true);
        using (var peer = Open(fixture, "advshop"))
        {
            peer.Select();
            That(U32(peer.AdventureInfo, 18 + 5312) == product.ItemId && U32(peer.AdventureInfo, 18 + 5316) == 1,
                "跨进程保留账号购买记录，不因角色重登清空");
            peer.Send(1414, Purchase(product.ItemId)); peer.Expect(1, 1414, [1]); peer.Expect(0, 14); peer.Expect(0, 1343); peer.Expect(0, 1349); peer.Barrier();
            That(fixture.Query("SELECT count FROM account_adventure_purchases") is [[2L]] &&
                fixture.Query("SELECT count(*) FROM account_adventure_receipts WHERE action='shop_purchase'") is [[2L]], "失败恢复后再购买仅计实际两次成功");
            FormalDungeonTcpTests.EntryTown(peer); FormalDungeonTcpTests.Enter(peer); FormalDungeonTcpTests.Load(peer);
            Execute("DELETE FROM account_adventure_purchases; UPDATE account_adventures SET points=$points", funds);
            peer.Send(1414, Purchase(product.ItemId)); peer.Expect(1, 1414, [1]); peer.Expect(0, 14); peer.Expect(0, 1343); peer.Expect(0, 1349); peer.Barrier();
            That(fixture.Query("SELECT count(*) FROM account_adventure_receipts WHERE action='shop_purchase'") is [[3L]],
                "已加载正式副本内购买仍提交原角色，专用ACK解开商店等待，不强制回城");
        }
        Console.WriteLine($"PASS {Count - checks} 冒险团商店购买、拒绝、SQL回滚及冷启动TCP断言");
        string Snapshot() => fixture.Snapshot("SELECT revision,hex(points) FROM account_adventures") +
            fixture.Snapshot("SELECT count FROM account_adventure_purchases") + fixture.Snapshot("SELECT * FROM character_inventory_items");
        byte[] Purchase(int item) => Build(writer => { writer.Write(product.Category); writer.Write(item); });
        void Execute(string sql, byte[]? points = null)
        {
            using var db = new SqliteConnection("Data Source=" + fixture.Database + ";Pooling=False;Foreign Keys=True"); db.Open();
            using var command = db.CreateCommand(); command.CommandText = sql;
            if (points is not null) command.Parameters.AddWithValue("$points", points);
            command.ExecuteNonQuery();
        }
    }
}
