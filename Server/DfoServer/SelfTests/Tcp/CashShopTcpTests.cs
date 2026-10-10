using System.Buffers.Binary;
using DfoServer.Game.Accounts;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.Persistence;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class CashShopTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        var product = resources.ItemResources.CashShop.Products.Values.First(row =>
            row.CeraPrice > 0 && row.Section is not ("visual" or "selectable character premium" or "charac premium package") &&
            resources.ItemResources.Items.Get(row.ItemId).Kind is not DfoServer.GameWorld.Items.ItemKind.Equipment);
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "cash-shop"));
        var towns = resources.Towns;
        var characters = resources.Characters;
        var repository = new CharacterRepository(fixture.Database);
        var owners = new AccountOwners();
        using (var owner = owners.Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var current = repository.Create(owner, new("CashShopTcp", towns.Room, towns.InitialReturn)
            {
                Level = 60, AcceptedStoryLevel = 1, Skills = characters.Get(0).Skills,
                Inventory = InventoryRewardGrantService.CreateStarter(characters.Get(0))
            });
            current = InventoryService.Commit(repository, owner, current,
                new CurrencyService(resources.ItemResources.GoldLimits).Grant(current, CurrencyKind.Cera, product.CeraPrice));
        }
        await fixture.StartAsync();
        using (var peer = Open(fixture))
        {
            peer.Select();
            byte[] body = new byte[19];
            body[2] = 1;
            body[5] = 0xFF;
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(7), checked((uint)product.CommodityId));
            peer.Send(64, body);
            var ack = peer.Expect(1, 64).Body;
            That(ack.Length == 24 && ack[0] == 1 && U32(ack, 6) == product.CommodityId,
                "商城成功 ACK 使用商品号和固定保留字段");
            var delta = peer.Expect(0, 14).Body;
            That(delta.Any(value => value == (byte)(product.ItemId & 0xff)), "商城奖励进入主背包差量");
            var cera = peer.Expect(0, 53).Body;
            That(cera.Length == 13 && cera[0] == 1 && U32(cera, 1) == 0, "商城提交后实时刷新 Cera 余额");
            That(fixture.Query("SELECT COALESCE((SELECT balance FROM account_currency_balances WHERE currency_kind=15),0)") is [[long balance]] && balance == 0,
                "商城扣款在数据库提交");
        }
        await fixture.StopAsync();
        That(fixture.Query("PRAGMA integrity_check") is [["ok"]] && fixture.Query("PRAGMA foreign_key_check").Length == 0,
            "商城 TCP 事务保持 SQLite 完整性");
    }
}
