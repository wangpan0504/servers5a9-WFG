using static DfoServer.Testing.CashMileageTcpChecks;
using System.Buffers.Binary;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.Persistence;
using DfoServer.Infrastructure;
using DfoServer.Network.Management;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static partial class CashShopTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        await PrivilegeAsync(source, root, resources);
        Inventory.CashShopTests.RunCore();
        Inventory.CashShopTests.Run(resources.Towns, resources.Characters, resources.ItemResources);
        var originalTemplate = resources.ItemResources.Items.Get(490705385);
        try
        {
            LegacyExpiryFixture.Restore(resources.ItemResources.Items, (490705385, "2020-04-09 06:00:00"));
            await RunWithLegacyExpirationAsync(source, root, resources);
        }
        finally
        {
            resources.ItemResources.Items.PrefillTemplates([KeyValuePair.Create(originalTemplate.Id, originalTemplate)]);
        }
    }

    static async Task RunWithLegacyExpirationAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        var now = DateTimeOffset.UtcNow;
        var ordinaryProducts = resources.ItemResources.CashShop.Products.Values.Where(row => !resources.ItemResources.CashShop.IsCouponProduct(row.CommodityId));
        var product = ordinaryProducts.First(row =>
            row.CeraPrice > 0 && row.Section is not ("visual" or "selectable character premium" or "charac premium package") &&
            resources.ItemResources.Items.Get(row.ItemId).Kind is not DfoServer.GameWorld.Items.ItemKind.Equipment &&
            resources.ItemResources.Items.Get(row.ItemId).Expiration.Resolve(now) is var expiry && (expiry == 0 || expiry > now.ToUnixTimeSeconds()));
        // 固定真实历史促销主货；资源变化须明确失败，不能悄然选中另一种购买发布链。
        var expired = ordinaryProducts.Single(row => row.CommodityId == 105681);
        var expiredTemplate = resources.ItemResources.Items.Get(expired.ItemId);
        That(expired is { ItemId: 490705385, Count: 1, CeraPrice: > 0 and < 100_000, Section: "package" } &&
            expiredTemplate.Kind == ItemKind.Consumable &&
            expiredTemplate.Expiration is { Days: 0, AbsoluteTime: > 0 } expiration && expiration.AbsoluteTime <= now.ToUnixTimeSeconds(),
            "历史期限夹具明确使用过期的105681促销主货");
        var promotionConfig = JsonSerializer.Deserialize<PromotionConfiguration[]>(ServerRuntime.ReadShippedPromotionConfig())!;
        That(promotionConfig.Single(row => row.Rules.Products.Any(item => item.CommodityId == expired.CommodityId)) is
            { Enabled: true, Rules: var promotionRules } &&
            promotionRules.Scope == new PromotionScope("spring-2020", "replica-1", "v1", "default", "package") &&
            promotionRules.Products.Contains(new PromotionProduct(105681, 490705385, 1)),
            "历史期限夹具绑定真实启用的原促销规则期和单套映射");
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "cash-shop"), gm: true, resources: resources);
        var towns = resources.Towns;
        var characters = resources.Characters;
        var repository = new CharacterRepository(fixture.Database);
        var owners = new AccountOwners();
        ushort couponSlot;
        using (var owner = owners.Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var current = repository.Create(owner, new("CashShopTcp", towns.Room, towns.InitialReturn)
            {
                Level = 60, AcceptedStoryLevel = 1, Skills = characters.Get(0).Skills,
                Inventory = InventoryRewardGrantService.CreateStarter(characters.Get(0))
            });
            couponSlot = CreateMappedCouponRole(repository, owner, resources);
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
        That(Command("cash-shop-usable-period") == "OK {\"ignoreUsablePeriod\":false}", "Runtime默认不忽略相对期限");
        That(Command("cash-shop-expiration off") == "OK {\"ignoreAbsoluteExpiration\":false}", "可显式恢复PVF期限规则");
        using (var peer = Open(fixture))
        {
            peer.Select();
            byte[] failedBody = new byte[19];
            failedBody[2] = 1;
            BinaryPrimitives.WriteUInt32LittleEndian(failedBody.AsSpan(7), checked((uint)product.CommodityId));
            string unchanged = Snapshot();
            // 钱包写入及物品分配之后才失败，确认费用和所有奖励属于同一笔事务。
            fixture.Sql("CREATE TRIGGER fail_cash_shop_late BEFORE INSERT ON currency_audit_log " +
                "WHEN NEW.action_name='cash_shop_buy' BEGIN SELECT RAISE(ABORT,'cash shop failure'); END");
            peer.Send(64, failedBody);
            peer.Closed();
            That(Snapshot() == unchanged, "商城晚期SQL失败不发成功，点券、物品、UID、钱包版本及审计整笔回滚");
            fixture.Sql("DROP TRIGGER fail_cash_shop_late");
        }
        using (var peer = Open(fixture))
        {
            peer.Select();
            byte[] body = new byte[19];
            body[2] = 1;
            body[5] = 0xFF;
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(7), checked((uint)product.CommodityId));
            var mileage = MileageBefore(fixture, ExpectedCashMileage(product));
            peer.Send(64, body);
            var ack = peer.Expect(1, 64).Body;
            That(ack.Length == 24 && ack[0] == 1 && U32(ack, 6) == product.CommodityId,
                "商城成功 ACK 使用商品号和固定保留字段");
            var delta = peer.Expect(0, 14).Body;
            That(delta.Any(value => value == (byte)(product.ItemId & 0xff)), "商城奖励进入主背包差量");
            var cera = peer.Expect(0, 53).Body;
            That(cera.Length == 13 && cera[0] == 1 && U32(cera, 1) == expired.CeraPrice, "商城提交后实时刷新 Cera 余额");
            MileageAfter(peer, fixture, mileage);
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
            mileage = MileageBefore(fixture, ExpectedCashMileage(expired));
            peer.Send(64, body);
            That(peer.Expect(1,64).Body[0] == 1, "同一连接在失败后可调试购买历史商品");
            var debugDelta = peer.Expect(0,14).Body;
            var debugEntries = Enumerable.Range(0,U16(debugDelta,1)).Select(index => 3 + index * 123)
                .Where(at => U32(debugDelta,at + 2) == expired.ItemId).ToArray();
            That(debugEntries.Length == 1 && U32(debugDelta,debugEntries[0] + 56) == ItemExpiration.MaximumUnixTime,
                "历史商品NOTI14中对应物品条目的期限下发2037安全上限");
            That(U32(peer.Expect(0,53).Body,1) == 0 && fixture.Query("SELECT COALESCE((SELECT balance FROM account_currency_balances WHERE currency_kind=15),0)") is [[long paid]] && paid == 0,
                "调试购买仍原子扣款并投影真实余额");
            MileageAfter(peer, fixture, mileage);
            AssertPromotionNativeRefresh(peer.Expect(0, 108), 1, false, peer.ExpectedOpenEvents);
            MileageLimitsAfterRefresh(peer, fixture);
            peer.Barrier();
            That(fixture.Query("SELECT count,revision FROM cash_shop_promotion_progress") is [[1L, 1L]] &&
                fixture.Query("SELECT count(*) FROM cash_shop_promotion_orders") is [[1L]] &&
                fixture.Query("SELECT count(*) FROM cash_shop_promotion_awards") is [[0L]],
                "历史促销购买只提交一套累计和一张订单，首套没有跨档赠礼");
            string beforeInsufficientFunds = Snapshot();
            peer.Send(64, body);
            Bytes(peer.Expect(1,64).Body, [0,0,0], "开启期限调试仍拒绝点券不足");
            peer.Barrier();
            That(Snapshot() == beforeInsufficientFunds, "点券不足不改变库存、邮件、钱包、审计或促销累计订单和赠礼账本");
            That(Command("cash-shop-expiration off") == "OK {\"ignoreAbsoluteExpiration\":false}", "GM关闭后回读实际状态");
        }
        await fixture.StopAsync();
        using (var owner = owners.Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            That(repository.List(owner).Single(role => role.Name == "CashShopTcp").Inventory.Items.Single(row => row.Core.ItemId == expired.ItemId).Core.ExpireTime == ItemExpiration.MaximumUnixTime,
                "关闭开关不追溯修改已持久化调试物品");
            var couponRole = repository.LoadCharacter(owner, couponSlot);
            InventoryService.Commit(repository, owner, couponRole,
                new CurrencyService(resources.ItemResources.GoldLimits).Grant(couponRole, CurrencyKind.Cera, 100_000));
        }
        await fixture.StartAsync("restart");
        That(Command("cash-shop-expiration") == "OK {\"ignoreAbsoluteExpiration\":true}", "重建Runtime恢复默认开启");
        That(Command("cash-shop-usable-period") == "OK {\"ignoreUsablePeriod\":false}", "重建Runtime默认保留相对期限");
        await MappedCouponAsync(fixture, resources, couponSlot);
        await fixture.StopAsync();
        That(fixture.Query("PRAGMA integrity_check") is [["ok"]] && fixture.Query("PRAGMA foreign_key_check").Length == 0,
            "商城 TCP 事务保持 SQLite 完整性");
        await AvatarAsync(source, root, resources);
        await SkillTreeAsync(source, root, resources);
        await HongbaoAsync(source, root, resources);
        await LimitCubeTcpTests.RunPurchasedTicketsAsync(source, root, resources);

        string Snapshot() => PurchaseSnapshot(fixture) +
            JsonSerializer.Serialize(fixture.Query("SELECT * FROM cash_shop_promotion_orders ORDER BY rowid")) +
            JsonSerializer.Serialize(fixture.Query("SELECT * FROM cash_shop_promotion_progress ORDER BY rowid")) +
            JsonSerializer.Serialize(fixture.Query("SELECT * FROM cash_shop_promotion_awards ORDER BY rowid"));
    }

    static string PurchaseSnapshot(TcpTestFixture fixture) => JsonSerializer.Serialize(fixture.Query("SELECT id,revision,gold,revive_coins,win_points FROM characters ORDER BY id")) +
            JsonSerializer.Serialize(fixture.Query("SELECT * FROM character_inventory_items ORDER BY item_uid")) +
            JsonSerializer.Serialize(fixture.Query("SELECT * FROM character_inventory_slots ORDER BY item_uid")) +
            JsonSerializer.Serialize(fixture.Query("SELECT * FROM inventory_avatar_details ORDER BY item_uid")) +
            JsonSerializer.Serialize(fixture.Query("SELECT * FROM mailbox_messages ORDER BY mail_id")) +
            JsonSerializer.Serialize(fixture.Query("SELECT * FROM mailbox_attachments ORDER BY item_uid")) +
            JsonSerializer.Serialize(fixture.Query("SELECT * FROM account_wallets ORDER BY account")) +
            JsonSerializer.Serialize(fixture.Query("SELECT * FROM account_currency_balances ORDER BY account,currency_kind")) +
            JsonSerializer.Serialize(fixture.Query("SELECT * FROM sqlite_sequence ORDER BY name")) +
            JsonSerializer.Serialize(fixture.Query("SELECT count(*) FROM inventory_audit_log")) +
            JsonSerializer.Serialize(fixture.Query("SELECT count(*) FROM currency_audit_log"));
}
