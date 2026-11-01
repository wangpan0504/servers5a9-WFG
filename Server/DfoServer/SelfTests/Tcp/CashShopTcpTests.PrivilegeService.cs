using System.Buffers.Binary;
using DfoServer.Game.Accounts;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Services;
using DfoServer.Infrastructure;
using DfoServer.Persistence;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static partial class CashShopTcpTests
{
    static async Task PrivilegeAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        PrivilegeServiceTests.RunCore();
        PrivilegeServiceTests.Run(resources.Towns, resources.Characters, resources.ItemResources, resources.Archive);
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "privilege-purchase"), resources: resources);
        var product = resources.ItemResources.CashShop.Products.Values.Single(row => row.Section == "privilege service");
        var repository = new CharacterRepository(fixture.Database);
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, static () => { }))
        {
            var character = repository.Create(owner, new("PrivWire", resources.Towns.Room, resources.Towns.InitialReturn)
            {
                Level = 60, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(0).Skills,
                Inventory = InventoryRewardGrantService.CreateStarter(resources.Characters.Get(0))
            });
            InventoryService.Commit(repository, owner, character,
                new CurrencyService(resources.ItemResources.GoldLimits).Grant(character, CurrencyKind.Cera, checked(product.CeraPrice * 2)));
        }
        await fixture.StartAsync();
        byte[] buy = new byte[19]; buy[2] = 1;
        BinaryPrimitives.WriteInt32LittleEndian(buy.AsSpan(7), product.CommodityId);
        using (var peer = Open(fixture))
        {
            peer.Select();
            That(U32(peer.PrivilegeServiceInformation, 0) == 2, "未购买角色选角恢复原生无特权状态");
            peer.Send(64, buy);
            That(peer.Expect(1, 64).Body[0] == 1, "真实商城请求购买礼遇成功");
            That(peer.Expect(0, 1643).Body.Length == 496, "购买后先刷新完整角色资格名册");
            byte[] state = peer.Expect(0, 1644).Body;
            That(state.Length == 21 && U32(state, 0) == 0 && U32(state, 9) == 1 && U32(state, 17) == 1,
                "购买立即下发真实有效期、购买日登录及资源季");
            peer.Barrier(packet => That(packet.Kind == 0 && packet.Opcode is 53 or 537, "购买后仅允许实际钱包和积分通知"));
            peer.Send(64, buy); peer.Expect(1, 64, [0, 0, 0]); peer.Barrier();
            byte[] claim = Enumerable.Repeat((byte)0xCD, 21).ToArray();
            BinaryPrimitives.WriteUInt32LittleEndian(claim.AsSpan(13), 0);
            BinaryPrimitives.WriteUInt32LittleEndian(claim.AsSpan(17), 0);
            peer.Send(1640, claim);
            peer.Expect(0, 1643);
            byte[] claimed = peer.Expect(0, 1644).Body;
            That(claimed[8] == 1 && U32(claimed, 9) == 1, "每日领取先投影提交后的已领取状态");
            peer.Expect(0, 14);
            peer.Expect(1, 1640, [1, 0, 0, 0, 0]); peer.Barrier();
        }
        using (var peer = Open(fixture))
        {
            peer.Select();
            That(peer.PrivilegeServiceInformation[8] == 1 && U32(peer.PrivilegeServiceInformation, 9) == 1 &&
                fixture.Query("SELECT login_days,daily_claim_day>0 FROM character_privilege_service") is [[1L, 1L]],
                "同日重登恢复已领取位且不会重复累计登录");
            peer.Barrier();
        }
    }
}
