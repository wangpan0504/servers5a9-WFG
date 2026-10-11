using System.Buffers.Binary;
using DfoServer.Game.Accounts;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.Persistence;
using DfoServer.Network.Management;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class CashShopTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        Inventory.CashShopTests.RunCore();
        Inventory.CashShopTests.Run(resources.Towns, resources.Characters, resources.ItemResources);
        var now = DateTimeOffset.UtcNow;
        var product = resources.ItemResources.CashShop.Products.Values.First(row =>
            row.CeraPrice > 0 && row.Section is not ("visual" or "selectable character premium" or "charac premium package") &&
            resources.ItemResources.Items.Get(row.ItemId).Kind is not DfoServer.GameWorld.Items.ItemKind.Equipment &&
            resources.ItemResources.Items.Get(row.ItemId).Expiration.Resolve(now) is var expiry && (expiry == 0 || expiry > now.ToUnixTimeSeconds()));
        var expired = resources.ItemResources.CashShop.Products.Values.First(row => row.CeraPrice > 0 && row.CeraPrice < 100_000 &&
            row.Section is not ("visual" or "selectable character premium" or "charac premium package") &&
            resources.ItemResources.Items.Get(row.ItemId).Kind == ItemKind.Consumable &&
            resources.ItemResources.Items.Get(row.ItemId).Expiration is { Days: 0, AbsoluteTime: > 0 } expiration && expiration.AbsoluteTime <= now.ToUnixTimeSeconds());
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "cash-shop"), gm: true, resources: resources);
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
                new CurrencyService(resources.ItemResources.GoldLimits).Grant(current, CurrencyKind.Cera, product.CeraPrice + expired.CeraPrice));
        }
        string Command(string line)
        {
            That(DevelopmentGmClient.TryExecute(fixture.Options.GmPort, line, out string reply), "GM端口返回 " + line);
            return reply;
        }
        await fixture.StartAsync();
        That(Command("cash-shop-expiration") == "OK {\"ignoreAbsoluteExpiration\":true}", "Runtime商城调试默认开启");
        That(Command("cash-shop-expiration off") == "OK {\"ignoreAbsoluteExpiration\":false}", "可显式恢复PVF期限规则");
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
            That(cera.Length == 13 && cera[0] == 1 && U32(cera, 1) == expired.CeraPrice, "商城提交后实时刷新 Cera 余额");
            That(fixture.Query("SELECT COALESCE((SELECT balance FROM account_currency_balances WHERE currency_kind=15),0)") is [[long balance]] && balance == expired.CeraPrice,
                "商城扣款在数据库提交");
            var before = fixture.Query("SELECT revision FROM characters");
            int beforeItems = fixture.Query("SELECT * FROM character_inventory_items").Length;
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(7), checked((uint)expired.CommodityId));
            peer.Send(64, body);
            Bytes(peer.Expect(1, 64).Body, [0,0,0], "默认期限拒绝使用完整通用失败包");
            peer.Barrier();
            That(fixture.Query("SELECT revision FROM characters")[0][0].Equals(before[0][0]) &&
                fixture.Query("SELECT * FROM character_inventory_items").Length == beforeItems &&
                fixture.Query("SELECT balance FROM account_currency_balances WHERE currency_kind=15") is [[long unchanged]] && unchanged == expired.CeraPrice,
                "拒绝过期商品不扣款、不发物品、不推进revision");
            peer.Send(64, [0]);
            Bytes(peer.Expect(1, 64).Body, [0,0,0], "畸形购买请求也返回3字节失败");
            peer.Barrier();
            That(Command("cash-shop-expiration invalid").StartsWith("ERR ") &&
                Command("cash-shop-expiration") == "OK {\"ignoreAbsoluteExpiration\":false}", "错误GM命令不改变开关");
            That(Command("cash-shop-expiration on") == "OK {\"ignoreAbsoluteExpiration\":true}", "GM开启实际购买服务的调试开关");
            peer.Send(64, body);
            That(peer.Expect(1,64).Body[0] == 1, "同一连接在失败后可调试购买历史商品");
            var debugDelta = peer.Expect(0,14).Body;
            var debugEntries = Enumerable.Range(0,U16(debugDelta,1)).Select(index => 3 + index * 123)
                .Where(at => U32(debugDelta,at + 2) == expired.ItemId).ToArray();
            That(debugEntries.Length == 1 && U32(debugDelta,debugEntries[0] + 56) == int.MaxValue,
                "历史商品NOTI14中对应物品条目的期限下发2038协议上限");
            That(U32(peer.Expect(0,53).Body,1) == 0 && fixture.Query("SELECT COALESCE((SELECT balance FROM account_currency_balances WHERE currency_kind=15),0)") is [[long paid]] && paid == 0,
                "调试购买仍原子扣款并投影真实余额");
            peer.Send(64, body);
            Bytes(peer.Expect(1,64).Body, [0,0,0], "开启期限调试仍拒绝点券不足");
            peer.Barrier();
            That(Command("cash-shop-expiration off") == "OK {\"ignoreAbsoluteExpiration\":false}", "GM关闭后回读实际状态");
        }
        await fixture.StopAsync();
        using (var owner = owners.Acquire(fixture.Options.DevelopmentAccount, () => { }))
            That(repository.List(owner).Single().Inventory.Items.Single(row => row.Core.ItemId == expired.ItemId).Core.ExpireTime == int.MaxValue,
                "关闭开关不追溯修改已持久化调试物品");
        await fixture.StartAsync("restart");
        That(Command("cash-shop-expiration") == "OK {\"ignoreAbsoluteExpiration\":true}", "重建Runtime恢复默认开启");
        await fixture.StopAsync();
        That(fixture.Query("PRAGMA integrity_check") is [["ok"]] && fixture.Query("PRAGMA foreign_key_check").Length == 0,
            "商城 TCP 事务保持 SQLite 完整性");
    }
}
