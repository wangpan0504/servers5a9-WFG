using System.Buffers.Binary;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Network.Management;
using DfoServer.Persistence;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static partial class CashShopTcpTests
{
    static async Task AvatarAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        var items = resources.ItemResources.Items;
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "avatar-shop"), gm: true, resources: resources);
        var repo = new CharacterRepository(fixture.Database);
        var owners = new AccountOwners();
        var template = items.Get(106550541);
        using (var owner = owners.Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var current = repo.Create(owner, Make("AvatarBuy", new([Coupon(2)])));
            var full = Enumerable.Range(0, ItemSlotBoundService.AvatarCapacity).Select(slot => InventoryRewardGrantService.CreateItem(template,
                new(InventoryListType.Avatar, (short)slot), InventoryRewardGrantService.Create(template)));
            repo.Create(owner, Make("AvatarFull", new([Coupon(1), .. full])));
            InventoryService.Commit(repo, owner, current,
                new CurrencyService(resources.ItemResources.GoldLimits).Grant(current, CurrencyKind.Cera, 10_000));
        }
        await fixture.StartAsync();
        That(DevelopmentGmClient.TryExecute(fixture.Options.GmPort, "cash-shop-expiration off", out string reply) &&
            reply == "OK {\"ignoreAbsoluteExpiration\":false}", "时装期限TCP保留绝对期限，相对期限默认保留");
        foreach (bool coupon in new[] { false, true })
        {
            using var peer = Open(fixture);
            peer.Select(); peer.Barrier();
            string before = PurchaseSnapshot(fixture);
            fixture.Sql("CREATE TRIGGER fail_avatar_buy BEFORE INSERT ON inventory_avatar_details BEGIN SELECT RAISE(ABORT,'avatar buy failure'); END");
            peer.Send(64, BuyAvatar(270623, 4, 3, coupon ? (byte)1 : (byte)0)); peer.Closed();
            That(before == PurchaseSnapshot(fixture), "新增详情晚期SQL失败回滚点券/券、核心、UID、版本与审计，不提前成功 " + coupon);
            fixture.Sql("DROP TRIGGER fail_avatar_buy");
        }
        using (var peer = Open(fixture))
        {
            peer.Select();
            peer.Send(64, BuyAvatar(270623, 4, 3));
            Bought(peer, 270623);
            AvatarRow(peer.Expect(0, 14).Body, 4, 0);
            That(U32(peer.Expect(0, 53).Body, 1) == 8680, "永久散件按EQU扣1320点券，随后同步真实账号余额");
            for (int index = 0; index < 2; index++)
            {
                peer.Send(64, BuyAvatar(270623, (byte)index, 3, 1));
                Bought(peer, 270623);
                BoosterTcpTests.Entry(peer.Expect(0, 14).Body, 65, index == 0 ? 2681590 : -1, index == 0 ? 1 : 0);
                AvatarRow(peer.Expect(0, 14).Body, (byte)index, 0);
                peer.Barrier();
                That(fixture.Query("SELECT balance FROM account_currency_balances WHERE currency_kind=15") is [[8680L]],
                    "券支付不扣点券，也不发虚构钱包变化");
            }
            string before = PurchaseSnapshot(fixture);
            foreach (byte[] invalid in new[] { BuyAvatar(270623, 0, 3, 1), BuyAvatar(270623, 255, 3), BuyAvatar(270623, 0, 0), BuyAvatar(270623, 0, 3)[..^1] })
            {
                peer.Send(64, invalid); peer.Expect(1, 64, [0, 0, 0]); peer.Barrier();
                That(before == PurchaseSnapshot(fixture), "耗尽源、非法属性、虚假期限及畸形包拒绝，连接继续且库存不变");
            }
            long started = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            peer.Send(64, BuyAvatar(270621, 0, 1)); Bought(peer, 270621);
            AvatarRow(peer.Expect(0, 14).Body, 0, 7 * 86400, started);
            That(U32(peer.Expect(0, 53).Body, 1) == 8480, "7天类型按EQU扣200点券，不按商品类型列发1/3件");
            peer.Barrier();
        }
        using (var peer = Open(fixture))
        {
            peer.Select(); peer.Barrier();
            var body = peer.SpecialContainers[1].Body;
            That(body[0] == 1 && U16(body, 3) == 4 && body.Length == 5 + 4 * 165,
                "重登完整恢复四件装扮及165B详情，不依赖购买时临时UID");
            var details = fixture.Query("SELECT json_extract(i.item_core,'$.Durability'),json_extract(d.detail,'$.ExpireTime') " +
                "FROM character_inventory_items i JOIN inventory_avatar_details d USING(item_uid) WHERE i.character_id=1 ORDER BY i.item_uid");
            That(details.Length == 4 && details.Take(3).Select(row => Convert.ToInt32(row[0])).SequenceEqual([4, 0, 1]) &&
                details.Take(3).All(row => Convert.ToInt64(row[1]) == 0) && Convert.ToInt64(details[3][1]) > DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                "持久化核心属性、独立期限和唯一详情全部恢复");
            That(DevelopmentGmClient.TryExecute(fixture.Options.GmPort, "cash-shop-usable-period on", out string relativeReply) &&
                relativeReply == "OK {\"ignoreUsablePeriod\":true}", "只开启相对忽略，不改变绝对开关");
            long started = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            peer.Send(64, BuyAvatar(270621, 0, 1)); Bought(peer, 270621);
            AvatarRow(peer.Expect(0, 14).Body, 0, checked((int)(int.MaxValue - started)), started);
            That(U32(peer.Expect(0, 53).Body, 1) == 8280, "忽略相对期限不改变七天商品的真实收费");
            peer.Barrier();
            That(DevelopmentGmClient.TryExecute(fixture.Options.GmPort, "cash-shop-usable-period off", out relativeReply) &&
                relativeReply == "OK {\"ignoreUsablePeriod\":false}" &&
                DevelopmentGmClient.TryExecute(fixture.Options.GmPort, "cash-shop-expiration", out string absoluteReply) &&
                absoluteReply == "OK {\"ignoreAbsoluteExpiration\":false}", "相对开关切换后绝对开关仍为false");
        }
        using (var peer = Open(fixture))
        {
            peer.Select(1); peer.Barrier();
            string before = PurchaseSnapshot(fixture);
            fixture.Sql("CREATE TRIGGER fail_avatar_buy_mail BEFORE INSERT ON mailbox_messages BEGIN SELECT RAISE(ABORT,'avatar buy mail failure'); END");
            peer.Send(64, BuyAvatar(270623, 4, 3, 1)); peer.Closed();
            That(before == PurchaseSnapshot(fixture), "满页邮件写入失败连同源券及全部详情原子回滚，不先吞券");
            fixture.Sql("DROP TRIGGER fail_avatar_buy_mail");
        }
        using (var peer = Open(fixture))
        {
            peer.Select(1);
            peer.Send(64, BuyAvatar(270623, 4, 3, 1)); Bought(peer, 270623);
            BoosterTcpTests.Entry(peer.Expect(0, 14).Body, 65, -1, 0);
            peer.Expect(0, 99, [1, 0]); peer.Barrier();
            That(fixture.Query("SELECT count(*) FROM mailbox_attachments WHERE claimed=0") is [[1L]] &&
                fixture.Query("SELECT json_extract(i.item_core,'$.Durability'),json_extract(d.detail,'$.ExpireTime') FROM mailbox_attachments a " +
                    "JOIN character_inventory_items i USING(item_uid) JOIN inventory_avatar_details d USING(item_uid) WHERE a.claimed=0") is [[4L, 0L]],
                "满时装页完整属性和永久期限交付现有邮件，不生成半件核心");
        }
        byte[] dyedCart = Convert.FromHexString("00000200000403EF340400000000000000000000FFFF3B9C01000000000000000000");
        using (var peer = Open(fixture))
        {
            peer.Select(); peer.Barrier();
            string before = PurchaseSnapshot(fixture);
            fixture.Sql("CREATE TRIGGER fail_mixed_avatar_buy BEFORE INSERT ON inventory_avatar_details BEGIN SELECT RAISE(ABORT,'mixed avatar buy failure'); END");
            peer.Send(64, dyedCart); peer.Closed();
            That(before == PurchaseSnapshot(fixture), "现场时装加染色剂混合车晚期失败，全部商品、点券、详情、版本与审计一起回滚");
            fixture.Sql("DROP TRIGGER fail_mixed_avatar_buy");
        }
        using (var peer = Open(fixture))
        {
            peer.Select(); peer.Barrier();
            peer.Send(64, dyedCart);
            Bought(peer, 275695); Bought(peer, 105531);
            BoosterTcpTests.Entry(peer.Expect(0, 14).Body, 65, 490704677, 1);
            AvatarRow(peer.Expect(0, 14).Body, 4, 0, itemId: 406560053);
            That(U32(peer.Expect(0, 53).Body, 1) == 5540, "现场34B请求逐商品ACK后完整同步两个容器，合计只扣2740点券");
            peer.Barrier();
            That(fixture.Query("SELECT count(*) FROM character_inventory_items WHERE character_id=1 AND json_extract(item_core,'$.ItemId')=490704677") is [[1L]],
                "染色剂持久化为普通STK，不按FF选项创建时装详情");
        }
        await fixture.StopAsync();
        That(fixture.Query("PRAGMA integrity_check") is [["ok"]] && fixture.Query("PRAGMA foreign_key_check").Length == 0,
            "时装散件收费、兑换及邮件事务保持数据库完整性");

        NewCharacter Make(string name, InventorySnapshot inventory) => new(name, resources.Towns.Room, resources.Towns.InitialReturn)
        { Job = 3, Level = 60, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(3).Skills, Inventory = inventory };
        InventoryItem Coupon(int count) => new(0, new(InventoryListType.Main, 65),
            InventoryRewardGrantService.Create(items.Get(2681590), count) with { ExpireTime = 0 });
    }

    static byte[] BuyAvatar(int commodity, byte ability, byte type, byte payment = 0)
    {
        byte[] body = new byte[19]; body[2] = 1; body[4] = payment; body[5] = ability; body[6] = type;
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(7), commodity);
        return body;
    }

    static void Bought(TcpPeer peer, int commodity)
    {
        byte[] expected = new byte[24]; expected[0] = 1;
        BinaryPrimitives.WriteInt32LittleEndian(expected.AsSpan(2), -1);
        BinaryPrimitives.WriteInt32LittleEndian(expected.AsSpan(6), commodity);
        peer.Expect(1, 64, expected);
    }

    static void AvatarRow(byte[] body, byte option, int seconds, long? started = null, int itemId = 106550541)
    {
        That(body.Length == 168 && body[0] == 1 && U16(body, 1) == 1 && U32(body, 5) == itemId && U16(body, 14) == option &&
            U32(body, 126) == 30 && U32(body, 160) == 4, "装扮增量携带所选属性及完整孔位/颜色RAW，未误用普通123B模板");
        uint remaining = U32(body, 9);
        long received = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        That(seconds == 0 ? remaining == 0 : started is not null && remaining > 0 && remaining <= seconds &&
            remaining >= seconds - (received - started), "Avatar期限字段为剩余秒数而非UID或Unix绝对截止");
    }
}
