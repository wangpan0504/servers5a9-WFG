using static DfoServer.Testing.CashMileageTcpChecks;
using DfoServer.Game.Accounts;
using DfoServer.Game.Currency;
using DfoServer.Persistence;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static partial class CashShopTcpTests
{
    static async Task HongbaoAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "hongbao-purchase"), resources: resources);
        var repo = new CharacterRepository(fixture.Database);
        var owners = new AccountOwners();
        var catalog = resources.ItemResources.GuildHongbao;
        var product = catalog.ByCommodity.Values.Single(row => row.Cera == catalog.DailyCeraLimit);
        ushort buyer;
        using (var owner = owners.Acquire(fixture.Options.DevelopmentAccount, static () => { }))
        {
            var character = repo.Create(owner, new("HongbaoWire", resources.Towns.Room, resources.Towns.InitialReturn)
            {
                Level = 60, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(0).Skills,
                Inventory = DfoServer.Game.Inventory.Services.InventoryRewardGrantService.CreateStarter(resources.Characters.Get(0))
            });
            buyer = character.Id;
            _ = DfoServer.Game.Inventory.Services.InventoryService.Commit(repo, owner, character,
                new CurrencyService(resources.ItemResources.GoldLimits).Grant(character, CurrencyKind.Cera, product.Cera * 2));
        }
        fixture.Sql("""
            INSERT INTO guilds(guild_id,name,address,master_character_id,created_at_unix) VALUES(93,'红包实线','hongbao-wire',$p0,1000);
            INSERT INTO guild_members(guild_id,character_id,grade,joined_at_unix) VALUES(93,$p0,1,1000);
            """, buyer);
        await fixture.StartAsync();
        using var peer = Open(fixture);
        peer.Select();
        // 购买者选角前就已是公会成员，入城尾部固定补发守护石1276（487），按真实账本核验后再发请求。
        peer.CheckGuildPassiveBuffs(peer.Expect(0, 487));
        byte[] request = new byte[19]; request[2] = 1;
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(request.AsSpan(7), product.CommodityId);
        string before = PurchaseSnapshot(fixture);
        peer.Send(64, request);
        peer.Expect(1, 64, [0, 0, 0]); peer.Barrier();
        That(PurchaseSnapshot(fixture) == before && fixture.Query("SELECT count(*) FROM character_guild_hongbao_purchases") is [[0L]],
            "真实商城请求人数不足正常拒绝，连接保留且资产和额度不变");
        // 真实成员关系在购买最终事务读取，不要求客户端重登取得人数投影。
        for (int index = 1; index < catalog.MinimumMembers; index++)
        {
            using var owner = owners.Acquire("hongbao-wire-" + index, static () => { });
            ushort id = repo.Create(owner, new("HongWire" + index, resources.Towns.Room, resources.Towns.InitialReturn)).Id;
            fixture.Sql("INSERT INTO guild_members(guild_id,character_id,grade,joined_at_unix) VALUES(93,$p0,4,1000)", id);
        }
        var mileage = MileageBefore(fixture, ExpectedCashMileage(resources.ItemResources.CashShop.Product(product.CommodityId)!));
        peer.Send(64, request);
        That(peer.Expect(1, 64).Body[0] == 1, "同连接人数达标后真实购买成功，不借旧人数投影拒绝");
        peer.Expect(0, 14);
        That(U32(peer.Expect(0, 53).Body, 1) == product.Cera &&
            fixture.Query("SELECT cera FROM character_guild_hongbao_purchases WHERE character_id=$p0", buyer) is [[long total]] && total == product.Cera,
            "Runtime注入正式红包目录，点券投影与实际角色购买累计一致");
        MileageAfter(peer, fixture, mileage);
        peer.Barrier();
        peer.Send(1302, []);
        byte[] list = peer.Expect(1, 1302).Body;
        That(list.Length == 15 && list[0] == 1 && U16(list, 1) == catalog.MinimumMembers && U32(list, 3) == product.Cera &&
            U32(list, 7) == 0 && U32(list, 11) == 0,
            "红包列表显示当前角色真实当日购买总额，未发行记录和领取次数仍为零");
        before = PurchaseSnapshot(fixture);
        peer.Send(64, request);
        peer.Expect(1, 64, [0, 0, 0]); peer.Barrier();
        That(PurchaseSnapshot(fixture) == before && fixture.Query("SELECT cera FROM character_guild_hongbao_purchases WHERE character_id=$p0", buyer) is [[long limit]] && limit == catalog.DailyCeraLimit,
            "点券仍充足也不能超过日限额，业务拒绝不会断开连接");

        byte[] issue = new byte[18];
        issue.AsSpan(0, 13).Fill(0x87);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(issue.AsSpan(13), product.ItemId);
        issue[17] = 6;
        peer.Send(1305, issue);
        peer.Expect(1, 1305, [1, .. new byte[4], .. BitConverter.GetBytes(product.ItemId), .. new byte[4]]);
        peer.Expect(0, 14);
        peer.Barrier();
        That(fixture.Query("SELECT total,remaining,portions,taken FROM guild_hongbao_records") is [[long face, long balance, 6L, 0L]] &&
            face == product.Cera && balance == face &&
            fixture.Query("SELECT count(*) FROM character_inventory_items WHERE character_id=$p0 AND json_extract(item_core,'$.ItemId')=" + product.ItemId, buyer) is [[0L]],
            "真实1305通过Runtime扣除刚购买的红包，发行面额和份数同事务落库");
        peer.Send(1302, []);
        list = peer.Expect(1, 1302).Body;
        uint record = U32(list, 15);
        That(list.Length == 59 && U32(list, 11) == 1 && U32(list, 19) == product.ItemId && list[53] == 6 && list[54] == 0,
            "真实1302返回44B发行记录，包含真实物品、剩余六份及未领完状态");
        byte[] take = [.. Enumerable.Repeat((byte)0xe1, 13), .. BitConverter.GetBytes(record)];
        peer.Send(1306, take);
        uint amount = checked((uint)(product.Cera / 6 + (product.Cera % 6 > 0 ? 1 : 0)));
        peer.Expect(1, 1306, [1, .. new byte[8], .. BitConverter.GetBytes(amount)]);
        That(U32(peer.Expect(0, 53).Body, 9) == amount, "真实领取提交后投影欢乐代币券余额");
        peer.Barrier();
        That(fixture.Query("SELECT amount FROM guild_hongbao_claims WHERE record_id=" + record) is [[long claimed]] && claimed == amount &&
            fixture.Query("SELECT balance FROM account_currency_balances WHERE account=$p0 AND currency_kind=17", fixture.Options.DevelopmentAccount) is [[long wallet]] && wallet == amount,
            "真实1306金额数字来自已提交份额，欢乐代币券进入原账号钱包");
        peer.Send(1304, take);
        byte[] claims = peer.Expect(1, 1304).Body;
        That(claims.Length == 41 && U32(claims, 1) == 1 && claims[5] == 60 && U32(claims, 36) == amount,
            "真实1304从领取账本恢复36B等级、姓名及金额记录");
        string claimedBefore = fixture.Snapshot("SELECT * FROM guild_hongbao_records") + fixture.Snapshot("SELECT * FROM guild_hongbao_claims");
        peer.Send(1306, take);
        peer.Expect(1, 1306, new byte[3]);
        peer.Barrier();
        That(fixture.Snapshot("SELECT * FROM guild_hongbao_records") + fixture.Snapshot("SELECT * FROM guild_hongbao_claims") == claimedBefore,
            "同连接重复领取正常拒绝，份额和回执完全不变");
        peer.Send(1302, []);
        list = peer.Expect(1, 1302).Body;
        That(U32(list, 7) == 1 && list[53] == 5, "领取后列表显示账号今日一次及真实剩余五份");
    }
}
