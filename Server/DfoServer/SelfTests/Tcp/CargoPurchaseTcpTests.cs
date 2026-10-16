using System.Buffers.Binary;
using DfoServer.Game.Accounts;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Services;
using DfoServer.Persistence;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class CargoPurchaseTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        var r = resources.ItemResources;
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "cargo-purchase"), resources: resources);
        var repository = new CharacterRepository(fixture.Database);
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var role = repository.Create(owner, new("CargoBuy", resources.Towns.Room, resources.Towns.InitialReturn)
            {
                Level = 60, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(0).Skills,
                Inventory = InventoryRewardGrantService.CreateStarter(resources.Characters.Get(0))
            });
            InventoryService.Commit(repository, owner, role, new CurrencyService(r.GoldLimits).Grant(role, CurrencyKind.Cera, 1000));
        }
        That(r.CashShop.Product(100063) is { ItemId: 50, CeraPrice: 60 }, "当前资源金库工具商品及价格");
        await fixture.StartAsync();
        string before;
        fixture.Sql("CREATE TRIGGER fail_cargo_purchase BEFORE UPDATE OF cargo_capacity ON characters WHEN NEW.cargo_capacity>OLD.cargo_capacity BEGIN SELECT RAISE(ABORT,'cargo rollback'); END");
        using (var peer = Open(fixture))
        {
            peer.Select(); peer.Barrier(); before = State(); peer.Send(64, Purchase()); peer.Closed();
        }
        That(State() == before, "容量写入失败时扣款、物品与容量一起回滚");
        fixture.Sql("DROP TRIGGER fail_cargo_purchase");
        using (var peer = Open(fixture))
        {
            peer.Select(); peer.Send(64, Purchase());
            That(peer.Expect(1, 64).Body[0] == 1, "金库商城直扩购买成功");
            That(U32(peer.Expect(0, 53).Body, 1) == 940, "只扣当前资源价格60");
            var cargo = peer.Expect(0, 13).Body;
            That(cargo[0] == 2 && U16(cargo, 1) == 24, "个人仓库权威列表立即同步24格");
            peer.Barrier();
            That(fixture.Query("SELECT cargo_capacity FROM characters") is [[24L]] &&
                fixture.Query("SELECT count(*) FROM character_inventory_items WHERE json_extract(item_core,'$.ItemId')=50") is [[0L]],
                "只扩容，不发旧工具实体");
            string committed = State();
            peer.Send(64, Purchase()); peer.Expect(1, 64, [0, 0, 0]); peer.Barrier();
            That(State() == committed, "已达该档容量拒绝重复扣款");
        }
        await fixture.StopAsync();
        await fixture.StartAsync("restart");
        using (var peer = Open(fixture))
        {
            var entry = peer.Select();
            That(entry[6].Body[0] == 2 && U16(entry[6].Body, 1) == 24, "重启及选角恢复24格个人金库");
            peer.Barrier();
        }
        await fixture.StopAsync();
        string State() => fixture.Snapshot("SELECT revision,cargo_capacity FROM characters") +
            fixture.Snapshot("SELECT * FROM account_currency_balances") + fixture.Snapshot("SELECT item_uid,item_core FROM character_inventory_items ORDER BY item_uid");
    }

    static byte[] Purchase()
    {
        byte[] body = new byte[19]; body[2] = 1;
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(7), 100063);
        return body;
    }
}
