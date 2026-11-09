using DfoServer.Game.Accounts;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Services;
using DfoServer.Infrastructure;
using DfoServer.Persistence;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.CashMileageTcpChecks;

namespace DfoServer.Testing;

internal static partial class CashShopTcpTests
{
    static async Task RestrictionsAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "cash-restrictions"), resources: resources);
        var repo = new CharacterRepository(fixture.Database);
        var product = resources.ItemResources.CashShop.Products[105520];
        That(resources.ItemResources.CashShop.BuyRestrictions[105520] is
            { RestrictId: 124, Maximum: 2, AccountWide: true }, "真实超星空礼包账号月限购两次");
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, static () => { }))
        {
            var character = repo.Create(owner, new("LimitWire", resources.Towns.Room, resources.Towns.InitialReturn)
            {
                Level = 60, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(0).Skills,
                Inventory = InventoryRewardGrantService.CreateStarter(resources.Characters.Get(0))
            });
            InventoryService.Commit(repo, owner, character,
                new CurrencyService(resources.ItemResources.GoldLimits).Grant(character, CurrencyKind.Cera, product.CeraPrice * 3));
        }
        await fixture.StartAsync();
        using (var peer = Open(fixture))
        {
            peer.Select();
            Bytes(peer.CashShopRestrictions, [0, 0, 0, 0], "新账号空限购快照");
            for (uint remaining = 1; ; remaining--)
            {
                var mileage = MileageBefore(fixture, ExpectedCashMileage(product));
                peer.Send(64, Build(w => { w.Write((byte)0); w.Write((byte)0); w.Write((byte)1);
                    w.Write(0); w.Write(105520); w.Write(0); w.Write(0); }));
                That(peer.Expect(1, 64).Body[0] == 1, "账号月限购购买成功");
                var limits = peer.Expect(0, 863).Body;
                Check(limits, remaining);
                var items = peer.Expect(0, 14).Body;
                That(items.Length >= 3 && U16(items, 1) > 0, "限购购买仍发布真实库存增量");
                That(U32(peer.Expect(0, 53).Body, 1) == product.CeraPrice * (remaining + 1), "限购刷新不重复扣点券");
                MileageAfter(peer, fixture, mileage);
                peer.Barrier();
                if (remaining == 0) break;
            }
            peer.Send(64, Build(w => { w.Write((byte)0); w.Write((byte)0); w.Write((byte)1);
                w.Write(0); w.Write(105520); w.Write(0); w.Write(0); }));
            Bytes(peer.Expect(1, 64).Body, [0, 0, 0], "额度耗尽后拒绝第三次购买");
            peer.Barrier();
        }
        using (var restored = Open(fixture))
        {
            restored.Select();
            Check(restored.CashShopRestrictions, 0);
        }
        await fixture.StopAsync();

        static void Check(byte[] body, uint remaining) => That(body.Length == 16 && U32(body, 0) == 1 &&
            U32(body, 4) == 124 && U32(body, 8) > DateTimeOffset.UtcNow.ToUnixTimeSeconds() && U32(body, 12) == remaining,
            "真实Noti863恢复restrictId124、月重置时间及剩余" + remaining + "/2");
    }
}
