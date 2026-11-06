using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Events;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Network.Management;
using DfoServer.Persistence;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class MileageShopTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        await PermanentAsync(source, root, resources);
        var clock = new Inventory.DailyBoosterTestClock(new(2026, 10, 29, 1, 0, 0, TimeSpan.Zero));
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "mileage-shop"), gm: true, resources: resources, clock: clock);
        var assets = resources.ItemResources;
        That(assets.CashShop.Mileage.Products.Count == 14 && assets.CashShop.Mileage.Limits.Count == 7 &&
            assets.CashShop.Mileage.Products[1000154] is { ItemId: 490705273, Count: 1, MileagePrice: 5 } &&
            assets.CashShop.Mileage.Products[1000161] is { ItemId: 490705481, MileagePrice: 2500 }, "当前中国区积分商城实际商品、行宽与七条限购装配");
        var repo = new CharacterRepository(fixture.Database);
        var owners = new AccountOwners();
        using (var owner = owners.Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var role = repo.Create(owner, new("MileageShopA", resources.Towns.Room, resources.Towns.InitialReturn)
            {
                Level = 60, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(0).Skills,
                Inventory = InventoryRewardGrantService.CreateStarter(resources.Characters.Get(0))
            });
            var currency = new CurrencyService(assets.GoldLimits);
            foreach (var row in new[] { (CurrencyKind.Mileage, 10000), (CurrencyKind.Cera, 1000), (CurrencyKind.TokenCera, 1000), (CurrencyKind.Gold, 2000000) })
                role = InventoryService.Commit(repo, owner, role, currency.Grant(role, row.Item1, row.Item2));
            role = InventoryService.Commit(repo, owner, role, new(true, 0, new(role.Inventory, role.Inventory with
            {
                AccountCargo = role.Inventory.AccountCargo with { Capacity = 32 },
                Items = role.Inventory.Items.Add(new(0, new(InventoryListType.Main, 65), new(2682968, GameWorld.Items.ItemKind.Consumable, 1)))
            }, "legacy_cargo_tool_fixture")));
            repo.Create(owner, new("MileageShopB", resources.Towns.Room, resources.Towns.InitialReturn)
                { Level = 60, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(0).Skills });
            var events = new ServerEventService(new ServerEventRepository(fixture.Database), clock);
            events.Override(266, true);
            var service = new InventoryCashShopService(assets.Items, assets.CashShop, assets.GoldLimits, assets.EpicPieces, assets.Cargo,
                events: events, clock: clock);
            var planned = service.Buy(role, new(0, [new(1000154, 255, 255)]), out _);
            string before = State();
            events.Override(266, false);
            bool rejected = false;
            try { InventoryService.Commit(repo, owner, role, planned); } catch (InventoryEventRejectedException) { rejected = true; }
            That(planned.Accepted && rejected && State() == before, "规划后活动关闭，在实际库存事务内拒绝旧活动版本，不先扣积分");
        }
        await fixture.StartAsync();
        using (var peer = Open(fixture))
        {
            peer.ExpectedOpenEvents = [282, 2449, 414, 181, ServerEventService.NolanVisibilityId]; // 本夹具已明确持久off，后续单独验证on/off及时段。
            peer.Select();
            var before = State();
            Buy(peer, 1000154); peer.Expect(1, 64, [0, 0, 0]); peer.Barrier();
            That(State() == before, "活动关闭的积分请求即使余额足够也不变更资产");
            Gm("event on 266");
            Events(peer, true); Limits(peer.Expect(0, 706).Body, 0); peer.Barrier();
            Buy(peer, 1000154); Ack(peer, 1000154);
            Item(peer.Expect(0, 14).Body, 490705273, 1);
            Balance(peer, 9995, 0); Limits(peer.Expect(0, 706).Body, 0); peer.Barrier();
            That(fixture.Query("SELECT mileage FROM account_wallets") is [[9995L]], "5积分商品只扣真实积分钱包，不扣点券");
            Buy(peer, 1000161, 2); Ack(peer, 1000161);
            Item(peer.Expect(0, 14).Body, 490705481, 1);
            Balance(peer, 7495, 0); Limits(peer.Expect(0, 706).Body, 1); peer.Barrier();
            before = State();
            Buy(peer, 1000161); peer.Expect(1, 64, [0, 0, 0]); peer.Barrier();
            That(State() == before, "限购按商品份数提交，重复购买拒绝且不扣积分");
            Buy(peer, 100050); Ack(peer, 100050);
            Item(peer.Expect(0, 14).Body, 37, 1);
            peer.Expect(0, 53, Build(w => { w.Write((byte)1); w.Write(920); w.Write(1000); w.Write(0); }));
            peer.Expect(0, 537, Build(w => { w.Write((byte)0); w.Write(7495); w.Write(7497); w.Write(2); })); peer.Barrier();
            That(fixture.Query("SELECT mileage,mileage_cash_earned FROM account_wallets") is [[7497L, 2L]],
                "仅点券名册商品不花代币券，实际消费按分类3%返2积分并保存消费累计");
            Buy(peer, 100004); Ack(peer, 100004);
            peer.Expect(0, 14);
            peer.Expect(0, 53, Build(w => { w.Write((byte)1); w.Write(920); w.Write(900); w.Write(0); }));
            peer.Expect(0, 537, Build(w => { w.Write((byte)0); w.Write(7497); w.Write(7500); w.Write(5); }));
            peer.Barrier();
            That(fixture.Query("SELECT mileage,mileage_cash_earned FROM account_wallets") is [[7500L, 5L]],
                "沿原生购物预览使用商品成交点券价返分，不在代币券自动分配后再次扣减基数");
            Buy(peer, 103155); Ack(peer, 103155);
            var goldPurchase = peer.Expect(0, 14).Body;
            That(goldPurchase[0] == 0 && U16(goldPurchase, 1) == 2 && U16(goldPurchase, 3) == 0 && U32(goldPurchase, 9) == 1200000 &&
                U32(goldPurchase, 128) == 10007367, "幸运魔锤按资源扣80万金币，不以零点券价免费发放");
            peer.Barrier(packet => That(packet.Opcode is not (53 or 427 or 537), "金币购买不扣点券，也不按金币伪造商城积分"));
            Buy(peer, 102045); Ack(peer, 102045);
            That(U32(peer.Expect(0, 14).Body, 5) == uint.MaxValue && U16(peer.Expect(0, 13).Body, 1) == 40,
                "历史金库工具兑换只消费原工具并扩大容量");
            peer.Barrier(packet => That(packet.Opcode is not (53 or 427 or 537), "预付工具兑换不重复扣款，也不凭商品原价重复返积分"));
            Gm("event off 266"); Events(peer, false); peer.Barrier();
            before = State(); Buy(peer, 1000154); peer.Expect(1, 64, [0, 0, 0]); peer.Barrier();
            That(State() == before, "在线关闭入口同时阻止新的积分购买");
            var now = clock.GetUtcNow();
            Gm($"event schedule 266 {now.AddSeconds(1):yyyy-MM-ddTHH:mm:sszzz} {now.AddSeconds(3):yyyy-MM-ddTHH:mm:sszzz}");
            await clock.WaitForOneShotTimerAsync(now.AddSeconds(1));
            clock.Advance(TimeSpan.FromSeconds(1)); Events(peer, true); Limits(peer.Expect(0, 706).Body, 1); peer.Barrier();
            await clock.WaitForOneShotTimerAsync(now.AddSeconds(3));
            clock.Advance(TimeSpan.FromSeconds(2)); Events(peer, false); peer.Barrier();
            Gm("event on 266"); Events(peer, true); Limits(peer.Expect(0, 706).Body, 1); peer.Barrier();
            before = State();
            fixture.Sql("CREATE TRIGGER mileage_shop_abort AFTER UPDATE ON account_wallets WHEN NEW.mileage<OLD.mileage BEGIN SELECT RAISE(ABORT,'mileage shop rollback'); END");
            Buy(peer, 1000160); peer.Closed();
            That(State() == before, "账号钱包写入后的SQL失败整体回滚积分、限购、物品及审计，无成功回包");
            fixture.Sql("DROP TRIGGER mileage_shop_abort");
        }
        using (var peer = Open(fixture))
        {
            peer.ExpectedOpenEvents = [282, 2449, 414, 181, ServerEventService.NolanVisibilityId, 266];
            var selected = peer.Select(1);
            Bytes(selected.Single(p => p.Opcode == 427).Body, Build(w => { w.Write((byte)0); w.Write(7500); w.Write(5); }),
                "同账号另一角色恢复余额和消费累计，不另建角色积分");
            Limits(peer.MileageLimits, 1); peer.Barrier();
            Buy(peer, 1000161); peer.Expect(1, 64, [0, 0, 0]); peer.Barrier();
        }
        using (var peer = Open(fixture, "mileage-other"))
        {
            peer.Create("MileageOther"); peer.ExpectedOpenEvents = [282, 2449, 414, 181, ServerEventService.NolanVisibilityId, 266];
            peer.Select(); Limits(peer.MileageLimits, 0); peer.Barrier();
            var before = State(); Buy(peer, 1000154); peer.Expect(1, 64, [0, 0, 0]); peer.Barrier();
            That(State() == before, "另一账号既不借用积分，也不继承商品已购次数");
        }
        await fixture.StopAsync();
        fixture.Sql("UPDATE account_wallets SET mileage_cash_earned=19999 WHERE account=$p0", fixture.Options.DevelopmentAccount);
        await fixture.StartAsync("mileage-reopen");
        using (var peer = Open(fixture))
        {
            peer.ExpectedOpenEvents = [282, 2449, 414, 181, ServerEventService.NolanVisibilityId, 266];
            peer.Select(); Limits(peer.MileageLimits, 1); peer.Barrier();
            Buy(peer, 100050); Ack(peer, 100050); peer.Expect(0, 14); peer.Expect(0, 53);
            peer.Expect(0, 537, Build(w => { w.Write((byte)0); w.Write(7500); w.Write(7501); w.Write(20000); })); peer.Barrier();
            That(fixture.Query("SELECT mileage,mileage_cash_earned FROM account_wallets WHERE account=$p0", fixture.Options.DevelopmentAccount)
                is [[7501L, 20000L]], "消费返积分按20000独立累计封顶，只发剩余额度，不扣除或封顶礼盒积分余额");
            // 实机原始请求：积分来源1，商品1000147；不是测试辅助发送者默认的来源0。
            peer.Send(64, Convert.FromHexString("0100010000FFFFD3420F000000000000000000"));
            Ack(peer, 1000147); Item(peer.Expect(0, 14).Body, 10158964, 10);
            Balance(peer, 7301, 20000); Limits(peer.Expect(0, 706).Body, 1, 1); peer.Barrier();
            That(fixture.Query("SELECT mileage,json_extract(mileage_shop_purchases,'$.\"1000147\"') FROM account_wallets WHERE account=$p0",
                fixture.Options.DevelopmentAccount) is [[7301L, 1L]],
                "实机请求扣200积分，发10件完整商品，限购只记1份");
            string before = State();
            Buy(peer, 100050, 1); peer.Expect(1, 64, [0, 0, 0]); peer.Barrier();
            That(State() == before, "来源1不能绕过普通商城币种校验、扣点券或伪造积分商品");
        }
        That(fixture.Query("PRAGMA integrity_check") is [["ok"]] && fixture.Query("PRAGMA foreign_key_check").Length == 0,
            "积分消费、限购、返积分和服务重建后数据库完整");

        void Gm(string command)
        {
            That(DevelopmentGmClient.TryExecute(fixture.Options.GmPort, command, out string reply) && reply.StartsWith("OK "), "真实GM活动入口: " + command);
        }
        string State() => fixture.Snapshot("SELECT account,revision,mileage,mileage_cash_earned,mileage_shop_purchases FROM account_wallets ORDER BY account") +
            fixture.Snapshot("SELECT id,revision,gold FROM characters ORDER BY id") +
            fixture.Snapshot("SELECT * FROM account_currency_balances ORDER BY account,currency_kind") +
            fixture.Snapshot("SELECT * FROM character_inventory_items ORDER BY item_uid") +
            fixture.Snapshot("SELECT * FROM character_inventory_slots ORDER BY item_uid") +
            fixture.Snapshot("SELECT * FROM currency_audit_log ORDER BY audit_id");
    }

    // 不写活动266配置：验证本服默认常驻策略沿原生商城、事务及账号恢复链生效。
    public static async Task PermanentAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "mileage-shop-permanent"), resources: resources);
        var repository = new CharacterRepository(fixture.Database);
        var owners = new AccountOwners();
        var currency = new CurrencyService(resources.ItemResources.GoldLimits);
        using (var owner = owners.Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var role = repository.Create(owner, new("PermMileA", resources.Towns.Room, resources.Towns.InitialReturn)
            {
                Level = 60, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(0).Skills,
                Inventory = InventoryRewardGrantService.CreateStarter(resources.Characters.Get(0))
            });
            foreach (var row in new[] { (CurrencyKind.Mileage, 1000), (CurrencyKind.Cera, 1000), (CurrencyKind.TokenCera, 1000) })
                role = InventoryService.Commit(repository, owner, role, currency.Grant(role, row.Item1, row.Item2));
            repository.Create(owner, new("PermMileB", resources.Towns.Room, resources.Towns.InitialReturn)
                { Level = 60, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(0).Skills });
        }
        var events = new ServerEventService(new ServerEventRepository(fixture.Database));
        That(events.Read(266) is { Active: true, Schedule: { Revision: 0, OverrideActive: null, StartsAt: null, EndsAt: null } } &&
            fixture.Query("SELECT count(*) FROM server_events WHERE event_id=266") is [[0L]],
            "未配置266时默认常驻，读取及测试准备均不补写活动配置");
        await fixture.StartAsync();
        byte[] rewardRow;
        using (var peer = Open(fixture))
        {
            var selected = peer.Select();
            Bytes(selected.Single(packet => packet.Opcode == 427).Body,
                Build(w => { w.Write((byte)0); w.Write(1000); w.Write(0); }), "无活动配置入城恢复原积分钱包和零消费累计");
            Limits(peer.MileageLimits, 0); peer.Barrier();
            peer.Send(64, Convert.FromHexString("0100010000FFFFD3420F000000000000000000"));
            Ack(peer, 1000147);
            var reward = peer.Expect(0, 14).Body;
            Item(reward, 10158964, 10);
            That(reward.Length == 126, "默认开放真实请求保留完整123B商品实体行");
            rewardRow = reward[3..];
            Balance(peer, 800, 0); Limits(peer.Expect(0, 706).Body, 0, 1); peer.Barrier();
            That(Wallet() is [[800L, 0L, 1L]], "默认积分兑换扣200、限购记1份，事务前置不因无配置误拒");
            string before = State();
            Buy(peer, 100050, 1);
            peer.Expect(1, 64, [0, 0, 0]); peer.Barrier();
            That(State() == before, "默认开放仍拒绝用积分来源购买普通点券商品，完整资产不变");
            Buy(peer, 100050); Ack(peer, 100050); Item(peer.Expect(0, 14).Body, 37, 1);
            peer.Expect(0, 53, Build(w => { w.Write((byte)1); w.Write(920); w.Write(1000); w.Write(0); }));
            peer.Expect(0, 537, Build(w => { w.Write((byte)0); w.Write(800); w.Write(802); w.Write(2); })); peer.Barrier();
            That(Wallet() is [[802L, 2L, 1L]], "无配置普通点券购买按原3%返2积分及真实消费累计");
            Buy(peer, 100004); Ack(peer, 100004); peer.Expect(0, 14);
            peer.Expect(0, 53, Build(w => { w.Write((byte)1); w.Write(920); w.Write(900); w.Write(0); }));
            peer.Expect(0, 537, Build(w => { w.Write((byte)0); w.Write(802); w.Write(805); w.Write(5); })); peer.Barrier();
            That(Wallet() is [[805L, 5L, 1L]], "默认常驻保原代币券分配及成交商品价返3分，不改成只按Cera分摊");
            before = State();
            fixture.Sql("CREATE TRIGGER permanent_mileage_abort AFTER UPDATE ON account_wallets WHEN NEW.mileage<OLD.mileage BEGIN SELECT RAISE(ABORT,'permanent mileage rollback'); END");
            Buy(peer, 1000154); peer.Closed();
            That(State() == before, "默认常驻积分购买晚SQL失败整体回滚，不发成功ACK");
            fixture.Sql("DROP TRIGGER permanent_mileage_abort");
        }
        using (var peer = Open(fixture))
        {
            var selected = peer.Select();
            var full = selected[4].Body;
            var matches = Enumerable.Range(0, U16(full, 3)).Select(index => 5 + index * 123)
                .Where(at => U16(full, at) == U16(rewardRow, 0)).ToArray();
            That(matches.Length == 1 && full.AsSpan(matches[0], 123).SequenceEqual(rewardRow),
                "新认证恢复默认兑换的同一完整实体行，晚SQL失败没有影响已购商品");
            Limits(peer.MileageLimits, 0, 1); peer.Barrier();
        }
        using (var peer = Open(fixture))
        {
            var selected = peer.Select(1);
            Bytes(selected.Single(packet => packet.Opcode == 427).Body,
                Build(w => { w.Write((byte)0); w.Write(805); w.Write(5); }), "同账号另一角色恢复默认兑换与返积分余额");
            Limits(peer.MileageLimits, 0, 1); peer.Barrier();
        }
        await fixture.StopAsync();
        await fixture.StartAsync("permanent-reopen");
        using (var peer = Open(fixture))
        {
            var selected = peer.Select();
            Bytes(selected.Single(packet => packet.Opcode == 427).Body,
                Build(w => { w.Write((byte)0); w.Write(805); w.Write(5); }), "Runtime重建仍无配置常驻并恢复同一账号余额");
            Limits(peer.MileageLimits, 0, 1); peer.Barrier();
            Buy(peer, 1000154); Ack(peer, 1000154); Item(peer.Expect(0, 14).Body, 490705273, 1);
            Balance(peer, 800, 5); Limits(peer.Expect(0, 706).Body, 0, 1); peer.Barrier();
            That(Wallet() is [[800L, 5L, 1L]], "服务重建后原失败请求可正常重新规划购买一次");
        }
        That(fixture.Query("SELECT count(*) FROM server_events WHERE event_id=266") is [[0L]] &&
            fixture.Query("PRAGMA integrity_check") is [["ok"]] && fixture.Query("PRAGMA foreign_key_check").Length == 0,
            "全链不伪造活动配置，积分、限购、完整实例及数据库一致");

        object[][] Wallet() => fixture.Query("SELECT mileage,mileage_cash_earned,json_extract(mileage_shop_purchases,'$.\"1000147\"') FROM account_wallets WHERE account=$p0", fixture.Options.DevelopmentAccount);
        string State() => fixture.Snapshot("SELECT * FROM account_wallets ORDER BY account") +
            fixture.Snapshot("SELECT * FROM characters ORDER BY id") +
            fixture.Snapshot("SELECT * FROM character_inventory_items ORDER BY item_uid") +
            fixture.Snapshot("SELECT * FROM character_inventory_slots ORDER BY item_uid") +
            fixture.Snapshot("SELECT * FROM currency_audit_log ORDER BY audit_id") +
            fixture.Snapshot("SELECT * FROM inventory_audit_log ORDER BY audit_id") +
            fixture.Snapshot("SELECT * FROM sqlite_sequence ORDER BY name");
    }

    static void Buy(TcpPeer peer, int commodity, byte mode = 0) => peer.Send(64, Build(w =>
    {
        w.Write(mode); w.Write((byte)0); w.Write((byte)1); w.Write((byte)0);
        w.Write((byte)0); w.Write((byte)255); w.Write((byte)255); w.Write(commodity);
        w.Write((byte)0); w.Write((byte)0); w.Write(0u); w.Write((ushort)0);
    }));
    static void Ack(TcpPeer peer, int commodity) => peer.Expect(1, 64, Build(w =>
    { w.Write((byte)1); w.Write((byte)0); w.Write(-1); w.Write(commodity); w.Write(0u); w.Write(0u); w.Write(0u); w.Write((ushort)0); }));
    static void Item(byte[] body, uint id, uint count) => That(body[0] == 0 && U16(body, 1) == 1 && U32(body, 5) == id && U32(body, 9) == count,
        "商城产物保留实际物品编号与整包数量");
    static void Balance(TcpPeer peer, int value, int earned) => peer.Expect(0, 427, Build(w => { w.Write((byte)0); w.Write(value); w.Write(earned); }));
    static void Limits(byte[] body, int titleUsed, int materialPacks = 0)
    {
        int[] ids = [1000146, 1000147, 1000157, 1000158, 1000159, 1000160, 1000161];
        Bytes(body, Build(w =>
        {
            w.Write((byte)0); w.Write((byte)ids.Length);
            foreach (int id in ids) { w.Write(id); w.Write(id == 1000161 ? titleUsed : id == 1000147 ? materialPacks : 0); }
        }), "706恢复全部七条商品限购及已购份数，不按包内数量填写");
    }
    static void Events(TcpPeer peer, bool active)
    {
        // 266变化只改积分入口；原生促销保持零累计，完整目录仍不得恢复退役333。
        ushort[] extraEvents = active ? [282, 2449, 414, 181, ServerEventService.NolanVisibilityId, 266] : [282, 2449, 414, 181, ServerEventService.NolanVisibilityId];
        CashShopTcpTests.AssertPromotionNativeRefresh(peer.Expect(0, 108), 0, false, extraEvents);
    }
}
