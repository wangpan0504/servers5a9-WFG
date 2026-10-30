using System.Buffers.Binary;
using System.Text.Json;
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

internal static class DevilContractTcpTests
{
    sealed class Clock : TimeProvider
    {
        long seconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(Interlocked.Read(ref seconds));
        public void Set(long value) => Interlocked.Exchange(ref seconds, value);
    }

    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        var r = resources.ItemResources;
        That(r.CashShop.Product(100817) is { ContractType: 586, Count: 1, DurationDays: 7, CeraPrice: 250 } &&
            r.CashShop.Product(103156) is { Count: 1, DurationDays: 30, CeraPrice: 3880 },
            "现行PVF服务编号和套餐价格不再误读为数量或零价格");
        var clock = new Clock();
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "devil-contract"), gm: true, resources: resources, clock: clock);
        var repository = new CharacterRepository(fixture.Database);
        var owners = new AccountOwners();
        Character initial;
        using (var owner = owners.Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            initial = repository.Create(owner, new("DevilTcp", resources.Towns.Room, resources.Towns.InitialReturn)
            {
                Level = 60, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(0).Skills,
                DungeonState = CharacterDungeonState.NewOrdinary,
                Inventory = new([new(0, new(InventoryListType.Main, 3),
                    InventoryRewardGrantService.Create(r.Items.Get(10400)) with { Durability = 0 })], Gold: 1000)
            });
            initial = InventoryService.Commit(repository, owner, initial,
                new CurrencyService(r.GoldLimits).Grant(initial, CurrencyKind.Cera, 10000));
            var withRepair = initial.Inventory with { AccountWallet = initial.Inventory.AccountWallet
                .ActivateContract(586, 14 * 86400, DateTimeOffset.UtcNow) };
            initial = InventoryService.Commit(repository, owner, initial,
                new(true, 0, new(initial.Inventory, withRepair, "devil_repair_test_qualification")));
        }
        PolicyChecks(initial, resources);
        string migrationAssets = MigrationAssets();
        // 恢复真实v48缺列结构，验证本次权益迁移。
        fixture.Sql("DROP TABLE character_skill_switch_inventory_items; DROP TABLE character_skill_switch_inventory; DROP TABLE character_quest_stage_claims; DROP TABLE character_quest_stage_witnesses");
        DatabaseSchemaTests.RewindRaidLifecycle(fixture.Database);
        fixture.Sql(DatabaseSchemaTests.GuildExtensionFixtureRewindSql + "ALTER TABLE characters DROP COLUMN devil_usage; ALTER TABLE characters DROP COLUMN stamina_until; ALTER TABLE characters DROP COLUMN daily_booster_usage; ALTER TABLE characters DROP COLUMN selected_skill_tree; PRAGMA user_version=48");
        That(fixture.Query("PRAGMA user_version") is [[48L]] &&
            fixture.Query("SELECT count(*) FROM sqlite_schema WHERE name IN('character_quest_stage_claims','character_quest_stage_witnesses')") is [[0L]] &&
            fixture.Query("SELECT count(*) FROM sqlite_schema WHERE name IN('character_skill_switch_inventory','character_skill_switch_inventory_items')") is [[0L]] &&
            fixture.Query("SELECT count(*) FROM pragma_table_info('characters') WHERE name IN('devil_usage','stamina_until','daily_booster_usage')") is [[0L]],
            "契约迁移输入为缺权益及每日礼盒列的v48旧库，不残留v53阶段或v54强化旁表");
        await fixture.StartAsync();
        That(fixture.Query("SELECT count(*) FROM sqlite_schema WHERE type='table' AND name IN('character_quest_stage_claims','character_quest_stage_witnesses')") is [[2L]] &&
            fixture.Query("SELECT count(*) FROM sqlite_schema WHERE type='table' AND name IN('character_skill_switch_inventory','character_skill_switch_inventory_items')") is [[2L]],
            "v48旧库真实迁移建立阶段与强化旁表");
        That(fixture.Query("SELECT devil_usage,daily_booster_usage FROM characters") is [["[]", "[]"]] &&
            fixture.Query("PRAGMA user_version") is [[long version]] && version == SqliteMigrations.CurrentVersion, "旧角色自动升级为空权益与每日礼盒次数账本及自然恢复期限");
        That(MigrationAssets() == migrationAssets, "v48升级保持角色归属、revision、金币、钱包和完整库存实体及槽位");
        fixture.Sql("CREATE TRIGGER fail_devil_purchase BEFORE INSERT ON account_currency_balances WHEN NEW.currency_kind=15 AND NEW.balance<10000 BEGIN SELECT RAISE(ABORT,'devil purchase rollback'); END");
        using (var peer = Open(fixture))
        {
            peer.Select(); peer.Barrier(); string before = State();
            peer.Send(64, Purchase(103156)); peer.Closed();
            That(State() == before, "套餐扣费失败回滚八项期限、每日领取、四种助手、钱包及revision");
        }
        fixture.Sql("DROP TRIGGER fail_devil_purchase");
        using (var peer = Open(fixture))
        {
            peer.Select();
            uint goldExpiry = 0;
            int balance = 10000;
            foreach (var (commodity, slot, price) in new[] { (100882, 7, 200),
                (100542, 0, 200), (100541, 3, 250), (100537, 4, 200), (100542, 0, 200) })
            {
                peer.Send(64, Purchase(commodity));
                That(peer.Expect(1, 64).Body[0] == 1, "独立魔王子服务购买成功");
                balance -= price;
                That(U32(peer.Expect(0, 53).Body, 1) == balance, "按PVF价格扣券");
                var overview = peer.Expect(0, 1760).Body;
                That(overview.Length == 10 && overview[0] == 1 && overview[1] == 58,
                    "原生概览只发送58，不把内部580系列账本键发给客户端");
                var services = peer.Expect(1, 905).Body;
                That(services.Length == 77 && services[0] == 1, "八项状态严格77字节");
                uint expiry = U32(services, 9 + slot * 9);
                That(expiry > DateTimeOffset.UtcNow.ToUnixTimeSeconds(), "CMD905对应槽位有效期已下发");
                if (slot == 0)
                {
                    if (goldExpiry != 0) That(expiry == goldExpiry + 7 * 86400, "黄金牌重复购买只累加本子项期限");
                    goldExpiry = expiry;
                }
            }
            string before = State();
            // 客户端传入全FF的期限和次数不能授予资格，城镇也不能伪造副本加成。
            byte[] forged = Enumerable.Repeat((byte)255, 76).ToArray();
            BinaryPrimitives.WriteUInt16LittleEndian(forged, 1);
            BinaryPrimitives.WriteUInt16LittleEndian(forged.AsSpan(2), 3);
            peer.Send(905, forged);
            That(peer.Expect(1, 905).Body[13 + 3 * 9] == 0, "城镇上报保持服务器实际次数");
            That(State() == before, "伪造快照不写资产或计数");
            // 真实TCP购买套餐，八项期限、四种道具及扣款必须同次提交。
            peer.Send(64, Purchase(103156));
            That(peer.Expect(1, 64).Body[0] == 1, "套餐购买开放");
            peer.Expect(0, 14);
            That(U32(peer.Expect(0, 53).Body, 1) == balance - 3880, "套餐扣款采用实际PVF价格");
            peer.Expect(0, 1760);
            var all = peer.Expect(1, 905).Body;
            That(Enumerable.Range(0, 8).All(slot => U32(all, 9 + slot * 9) > DateTimeOffset.UtcNow.ToUnixTimeSeconds()), "套餐激活八项期限");
            peer.Barrier();
        }
        int assistantId = r.DevilContracts.Assistants.Values.Where(row => row.MinimumLevel <= 60).OrderBy(row => row.ItemId).First().ItemId;
        var assistantSlot = fixture.Query("SELECT s.slot_index FROM character_inventory_slots s JOIN character_inventory_items i USING(item_uid) WHERE json_extract(i.item_core,'$.ItemId')=$p0", assistantId);
        ushort slotId = checked((ushort)(long)assistantSlot.Single().Single());
        using (var peer = Open(fixture))
        {
            peer.Select();
            byte[] request = Build(w => { w.Write(slotId); w.Write((byte)0); w.Write(0u); w.Write(assistantId); w.Write(0); });
            byte[] rejected = Build(w => { w.Write((byte)0); w.Write((ushort)123); w.Write((byte)0); w.Write(0u); w.Write(assistantId); });
            string before = State(); peer.Send(44, request); peer.Expect(1, 44, rejected); peer.Barrier();
            That(State() == before, "城镇拒绝召唤助手且不扣道具");
            FormalDungeonTcpTests.EntryTown(peer); FormalDungeonTcpTests.Enter(peer);
            before = State(); peer.Send(44, request); peer.Expect(1, 44, rejected); peer.Barrier();
            That(State() == before, "Loading阶段不能提前消耗助手");
            FormalDungeonTcpTests.Load(peer); peer.Send(44, request);
            peer.Expect(1, 44, Build(w => { w.Write((byte)1); w.Write(slotId); w.Write((byte)0); w.Write(0u); w.Write(assistantId); }));
            peer.Barrier();
            That(fixture.Query("SELECT count(*) FROM character_inventory_items WHERE json_extract(item_core,'$.ItemId')=$p0", assistantId) is [[0L]],
                "真实副本44提交助手消耗并返回原生完整ACK");
            before = State(); peer.Send(44, request); peer.Closed();
            That(State() == before, "助手成功token重放关闭原连接而不重复扣件");
        }
        fixture.Sql("CREATE TRIGGER fail_devil_repair BEFORE UPDATE ON character_inventory_items BEGIN SELECT RAISE(ABORT,'repair rollback'); END");
        string failed;
        using (var peer = Open(fixture))
        {
            peer.Select(); peer.Barrier(); failed = State(); peer.Send(23, [0, 255, 255, 255, 255, 1, 0]); peer.Closed();
            That(State() == failed, "自动修理SQL失败时耐久、次数、金币和revision一起回滚");
        }
        fixture.Sql("DROP TRIGGER fail_devil_repair");
        using (var peer = Open(fixture))
        {
            peer.Select(); peer.Send(23, [0, 255, 255, 255, 255, 1, 0]);
            var ack = peer.Expect(1, 23).Body;
            That(ack.Length == 10 && ack[0] == 1 && U32(ack, 1) == 1000, "自动修理不扣金币并返回D95完整ACK");
            peer.Expect(0, 14);
            That(peer.Expect(1, 905).Body[13 + 6 * 9] == 1, "耐久提交后发布自动修理次数1");
            string committed = State();
            peer.Send(23, [0, 255, 255, 255, 255, 1, 0]); peer.Expect(1, 23, [0, 19, 0]); peer.Barrier();
            That(State() == committed, "无零耐久装备的重复请求不消费次数");
        }
        await fixture.StopAsync();
        await fixture.StartAsync("restart");
        using (var peer = Open(fixture))
        {
            peer.Select();
            That(peer.DevilService0[13 + 6 * 9] == 1 && U32(peer.DevilService0, 9 + 7 * 9) > 0,
                "完整重启后保留各子服务期限和修理次数");
            peer.Barrier();
            FormalDungeonTcpTests.EntryTown(peer);
            FormalDungeonTcpTests.Enter(peer);
            string before = State();
            peer.Send(23, [0, 255, 255, 255, 255, 1, 0]);
            peer.Expect(1, 23, [0, 19, 0]);
            peer.Barrier();
            That(State() == before, "自动修理在副本加载阶段不抢先消费契约次数");
            FormalDungeonTcpTests.Load(peer);
            peer.Send(23, [0, 255, 255, 255, 255, 1, 0]);
            var ack = peer.Expect(1, 23).Body;
            That(ack.Length == 10 && ack[0] == 1 && U32(ack, 1) == 1000,
                "正式战斗按客户端触发执行自动修理，不要求存档已提前变成零耐久");
            var notifications = PartyDungeonTcpTests.Drain(peer);
            var main = notifications.Single(packet => packet.Kind == 0 && packet.Opcode == 14 && packet.Body[0] == 0).Body;
            That(main.Length == 3 + U16(main, 1) * 123 &&
                Enumerable.Range(0, U16(main, 1)).Select(index => 3 + index * 123)
                    .Any(at => U16(main, at) == 3 && U32(main, at + 2) == 10400 && U16(main, at + 11) == 33) &&
                notifications.Any(packet => packet.Kind == 1 && packet.Opcode == 905 && packet.Body[13 + 6 * 9] == 2) &&
                fixture.Query("SELECT gold FROM characters") is [[1000L]],
                "战斗自动修理用完整槽位差量同步权威耐久和次数2，金币保持不变");
        }
        VerifyStaminaExemptions(fixture, clock);
        await fixture.StopAsync();

        string MigrationAssets() => JsonSerializer.Serialize(fixture.Query("SELECT id,account,revision,gold FROM characters")) +
            JsonSerializer.Serialize(fixture.Query("SELECT * FROM account_wallets")) +
            JsonSerializer.Serialize(fixture.Query("SELECT * FROM account_currency_balances")) +
            JsonSerializer.Serialize(fixture.Query("SELECT * FROM character_inventory_items")) +
            JsonSerializer.Serialize(fixture.Query("SELECT * FROM character_inventory_slots"));
        string State() => JsonSerializer.Serialize(fixture.Query("SELECT revision,gold,devil_usage FROM characters")) +
            JsonSerializer.Serialize(fixture.Query("SELECT * FROM account_wallets")) +
            JsonSerializer.Serialize(fixture.Query("SELECT * FROM account_currency_balances")) +
            JsonSerializer.Serialize(fixture.Query("SELECT * FROM character_inventory_items"));
    }

    static void VerifyStaminaExemptions(TcpTestFixture fixture, Clock clock)
    {
        fixture.Sql("UPDATE characters SET level=19");
        using (var peer = Open(fixture))
        {
            AssertBlackDiamondActor(peer.Select()[2].Body, active: false);
            FormalDungeonTcpTests.EntryTown(peer);
            FormalDungeonTcpTests.Enter(peer);
            FormalDungeonTcpTests.Load(peer);
            Return(peer); peer.Barrier();
            That(fixture.Query("SELECT stamina_until FROM characters") is [[0L]], "19级真实退图不制造虚弱");
        }
        fixture.Sql("UPDATE characters SET level=20");
        using (var peer = Open(fixture))
        {
            peer.Select();
            FormalDungeonTcpTests.EntryTown(peer);
            FormalDungeonTcpTests.Enter(peer);
            FormalDungeonTcpTests.Load(peer);
            Return(peer);
            peer.Expect(0, 4, [10]);
            peer.Barrier();
            That(fixture.Query("SELECT stamina_until FROM characters") is [[long until]] && until > DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                "20级普通角色真实42退图保存虚弱；魔王免费恢复服务不自动免罚");
        }
        // 模拟存量误罚账号已具有黑钻；选角用例必须显式修复，不能依赖修改玩家库的外部脚本。
        long expires = DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds();
        fixture.Sql($"UPDATE account_wallets SET premium_contracts=json_set(premium_contracts,'$.56',{expires})");
        using (var peer = Open(fixture))
        {
            AssertBlackDiamondActor(peer.Select()[2].Body, active: true);
            That(fixture.Query("SELECT stamina_until FROM characters") is [[0L]], "黑钻重登清除既有误罚期限");
            FormalDungeonTcpTests.EntryTown(peer);
            FormalDungeonTcpTests.Enter(peer);
            FormalDungeonTcpTests.Load(peer);
            Return(peer);
            peer.Barrier();
            That(fixture.Query("SELECT stamina_until FROM characters") is [[0L]],
                "有效黑钻真实42退图不新增虚弱，完整回城包序和连接继续可用");
            SetBlackDiamond(0, active: false);
            SetBlackDiamond(30, active: true);
            SetBlackDiamond(31, active: true, appearanceChanged: false);
            SetPcRoom(true, blackDiamond: true);
            SetBlackDiamond(0, active: false, pcRoom: true);
            FormalDungeonTcpTests.Enter(peer);
            FormalDungeonTcpTests.Load(peer, pcRoom: true);
            Return(peer, pcRoom: true); peer.Barrier();
            That(fixture.Query("SELECT stamina_until FROM characters") is [[0L]],
                "只有全局网吧资格时真实42退图免虚弱，不需要黑钻期限");
            SetPcRoom(false, blackDiamond: false);
            SetPcRoom(false, blackDiamond: false, appearanceChanged: false);
            SetBlackDiamond(30, active: true);
            FormalDungeonTcpTests.Enter(peer);
            SetBlackDiamond(0, active: false, appearanceChanged: false);
            FormalDungeonTcpTests.Load(peer);
            AssertBlackDiamondActor(peer.Expect(0, 2).Body, active: false, dungeon: true);
            That(peer.Expect(0, 2).Body is [1, ..], "Loading变更只同步期限，待37完成后才按副本状态刷新Actor及本人属性");
            peer.Barrier();
            SetBlackDiamond(30, active: true, dungeon: true);
            SetBlackDiamond(0, active: false, dungeon: true);
            SetBlackDiamond(30, active: true, dungeon: true);
            Return(peer); peer.Barrier();
            That(fixture.Query("SELECT stamina_until FROM characters") is [[0L]], "副本内特权刷新后仍可退出，恢复当前账号实际黑钻免罚");
            FormalDungeonTcpTests.Enter(peer);
            SetPcRoom(true, blackDiamond: true, appearanceChanged: false);
            FormalDungeonTcpTests.Load(peer, pcRoom: true);
            AssertBlackDiamondActor(peer.Expect(0, 2).Body, active: true, dungeon: true, pcRoom: true);
            That(peer.Expect(0, 2).Body is [1, ..], "Loading期间网吧开关延后到37刷新，不提前重建场景");
            That(peer.Expect(0, 487).Body is [1, ..], "延后的网吧身份刷新同时补发487特权buff");
            peer.Barrier();
            SetPcRoom(false, blackDiamond: true, dungeon: true);
            Return(peer); peer.Barrier();
            long expiry = (long)fixture.Query("SELECT json_extract(premium_contracts,'$.56') FROM account_wallets")[0][0];
            string beforeExpiry = fixture.Snapshot("SELECT revision,premium_contracts FROM account_wallets");
            // 隔离跨30天日切与最后一秒的权益到期；跨日在线推送由fatigue-daily单独核验。
            peer.Send(7); peer.Expect(1, 7, [1]); peer.Expect(0, 2);
            clock.Set(expiry - 1);
            AssertBlackDiamondActor(peer.Select()[2].Body, active: true);
            peer.Barrier();
            clock.Set(expiry);
            peer.CheckBlackDiamondRefresh(active: false, allowDailyReset: true);
            peer.Barrier();
            That(fixture.Snapshot("SELECT revision,premium_contracts FROM account_wallets") == beforeExpiry,
                "自然到期刷新只更新已发视图，不改写期限或重复写库");

            void SetBlackDiamond(int days, bool active, bool dungeon = false, bool appearanceChanged = true, bool pcRoom = false)
            {
                That(DevelopmentGmClient.TryExecute(fixture.Options.GmPort, "black-diamond DevilTcp " + days, out string reply) &&
                    reply.StartsWith("OK ", StringComparison.Ordinal), "在线GM提交黑钻开通、续期或撤销");
                peer.CheckBlackDiamondRefresh(active, dungeon, appearanceChanged, pcRoom);
            }

            void SetPcRoom(bool enabled, bool blackDiamond, bool dungeon = false, bool appearanceChanged = true)
            {
                peer.Barrier();
                string before = fixture.Snapshot() + fixture.Snapshot("SELECT * FROM account_wallets");
                That(DevelopmentGmClient.TryExecute(fixture.Options.GmPort, "pc-room " + (enabled ? "on" : "off"), out string reply) &&
                    reply == "OK {\"premiumPcRoom\":" + (enabled ? "true}" : "false}"), "在线GM全局开关返回实际网吧身份");
                peer.CheckPcRoomRefresh(enabled, blackDiamond, dungeon, appearanceChanged);
                That(fixture.Snapshot() + fixture.Snapshot("SELECT * FROM account_wallets") == before,
                    "切换全局网吧身份不写角色资产、账号期限或revision");
            }
        }
        using (var peer = Open(fixture))
        {
            AssertBlackDiamondActor(peer.Select()[2].Body, active: false);
            That(peer.PremiumServices[56] == 0, "到期后重连不恢复黑钻外观，已关闭的网吧身份独立保持关闭");
            peer.Barrier();
        }

        static void Return(TcpPeer peer, bool pcRoom = false)
        {
            peer.Send(42);
            peer.Expect(1, 42, [1]);
            peer.Expect(0, 3, [1, (byte)peer.Id, (byte)(peer.Id >> 8), 0]);
            if (pcRoom) peer.Expect(0, 487);
            peer.Expect(0, 24);
            FormalDungeonTcpTests.RefreshedQuests(peer);
        }
    }

    static void PolicyChecks(Character initial, TcpTestResources resources)
    {
        var r = resources.ItemResources;
        var now = DateTimeOffset.UtcNow;
        var devil = new DevilContractService(r.DevilContracts, r.Items, r.GoldLimits, r.EpicPieces);
        var shop = new InventoryCashShopService(r.Items, r.CashShop, r.GoldLimits, r.EpicPieces, r.Cargo, contracts: r.Contracts, devil: devil);
        int[] mixedProducts = [103156, 100615, 103660, 104415, 100063];
        var mixedRequest = new CashShopPurchaseRequest(0, mixedProducts.Select(id => new CashShopPurchaseItem(id, 0)).ToArray());
        var mixed = shop.Buy(initial, mixedRequest, out _, now);
        That(mixed.Accepted && mixed.Plan!.After.CargoCapacity == 24 && mixed.Plan.After.AvatarExpansion == 112 &&
            mixed.Plan.After.AccountWallet.CharacterSlotCapacity == 33 &&
            mixed.Plan.After.Find(new(InventoryListType.Equipment, 30))?.Core.ItemId == 2681313 &&
            Enumerable.Range(0, 8).All(slot => DevilContractPolicy.Expiry(mixed.Plan.After.AccountWallet, slot) > now.ToUnixTimeSeconds()),
            "同单魔王套餐、名称装饰、角色栏、装扮栏、金库扩容互不覆盖权威状态");
        That(mixed.Plan!.After.AccountWallet.Get(CurrencyKind.Cera) == initial.Inventory.AccountWallet.Get(CurrencyKind.Cera) -
            mixedProducts.Sum(id => r.CashShop.Product(id)!.CeraPrice), "跨功能混单只按全部PVF价格扣费一次");
        That(!shop.Buy(initial, new(0, [.. mixedRequest.Items, new(104415, 0)]), out _, now).Accepted &&
            initial.Inventory.CargoCapacity == 8 && initial.Inventory.AvatarExpansion == 105 &&
            initial.Inventory.AccountWallet.CharacterSlotCapacity == 32, "混单末项重复扩容拒绝整笔且不修改原快照");
        var bought = shop.Buy(initial, new(0, [new(100541, 0)]), out _, now);
        That(bought.Accepted && bought.Plan!.After.Items.SequenceEqual(initial.Inventory.Items), "服务购买只激活账号期限，不产生伪实体");
        That(shop.Buy(initial, new(0, [new(100541, 0), new(100539, 0)]), out _, now).Accepted,
            "已实现子项允许混合购买");
        var bundle = shop.Buy(initial, new(0, [new(103156, 0)]), out _, now);
        That(bundle.Accepted, "PVF套餐能完整规划购买");
        var entitled = initial with { Inventory = bundle.Plan!.After };
        That(r.DevilContracts.PackageSlots[r.CashShop.Product(103156)!.ItemId].Order().SequenceEqual(Enumerable.Range(0, 8)) &&
            Enumerable.Range(0, 8).All(slot => DevilContractPolicy.Expiry(entitled.Inventory.AccountWallet, slot) > now.ToUnixTimeSeconds()), "八项从实际套餐内容映射");
        That(r.DevilContracts.Assistants.Count == 4 && r.DevilContracts.Assistants.Keys.All(id =>
            entitled.Inventory.Items.Count(item => item.Core.ItemId == id && item.ExpireTime > now.ToUnixTimeSeconds()) == 1), "四种当前PVF召唤道具各一件且有实际期限");
        That(!devil.DailyGift(entitled, now).Plan!.Changed && devil.DailyGift(entitled, now.AddDays(1)).Plan!.Changed,
            "日礼包幂等且次日可以重新领取");
        foreach (int commodity in new[] { 100539, 100540, 100538, 100817 })
            That(shop.Buy(initial, new(0, [new(commodity, 0)]), out _, now).Accepted, "剩余四种单项均开放购买");
        var stamina = new StaminaRecoveryService(r.DevilContracts);
        var weak = entitled with { Inventory = entitled.Inventory with { StaminaUntil = now.ToUnixTimeSeconds() + 600 } };
        That(StaminaRecoveryService.Percent(weak.Inventory, now) == 10 && StaminaRecoveryService.Percent(weak.Inventory, now.AddMinutes(5)) == 55 &&
            StaminaRecoveryService.Percent(weak.Inventory, now.AddMinutes(10)) == 100, "自然恢复按期限计算，重登不会重置");
        for (int i = 0; i < 10; i++)
        {
            weak = weak with { Inventory = weak.Inventory with { StaminaUntil = now.ToUnixTimeSeconds() + 600 } };
            var recovered = stamina.Recover(weak, 1, now);
            That(recovered.Accepted && recovered.Plan!.After.Gold == weak.Inventory.Gold && recovered.Plan.After.StaminaUntil == 0, "契约恢复不扣金币并清除虚弱");
            weak = weak with { Inventory = recovered.Plan!.After };
        }
        weak = weak with { Inventory = weak.Inventory with { StaminaUntil = now.ToUnixTimeSeconds() + 600 } };
        That(!stamina.Recover(weak, 1, now).Accepted && stamina.Recover(weak with { Inventory = weak.Inventory with { StaminaUntil = now.AddDays(1).ToUnixTimeSeconds() + 600 } }, 1, now.AddDays(1)).Accepted,
            "每日第11次拒绝、次日恢复额度");
        var paidWeak = weak with { Inventory = weak.Inventory with { Gold = 1000000 } };
        var paidRecovery = stamina.Recover(paidWeak, 0, now);
        That(paidRecovery.Accepted && paidRecovery.Plan!.After.Gold == 1000000 - r.DevilContracts.RecoveryPrices[paidWeak.Level - 1], "普通恢复按当前PVF等级价格扣金币");
        var apc = entitled.Inventory.Items.First(item => r.DevilContracts.Assistants.ContainsKey(item.Core.ItemId));
        var usable = new InventoryConsumableUseService(r.RecoveryConsumables);
        var preparedApc = entitled with { Inventory = entitled.Inventory with { Items = entitled.Inventory.Items.Replace(apc, apc with { Uid = 123 }) } };
        var usedApc = usable.Plan(preparedApc, new(apc.Position, 0, apc.Core.ItemId, 0), now.ToUnixTimeSeconds());
        That(usedApc.Accepted && usedApc.Plan!.After.Find(apc.Position) is null, "任务助手可进入实际44消耗链路");
        var stackable = PvfLib.PvfScript.List(resources.Archive.ReadText("stackable/stackable.lst"));
        int potId = stackable.Single(pair => pair.Value == "ect/uplegacy_smw01.stk").Key;
        int eventPotId = stackable.Single(pair => pair.Value == "ect/event_uplegacy_smw01.stk").Key;
        That(r.Boosters.AllowsDevilDouble(potId) && !r.Boosters.AllowsDevilDouble(eventPotId),
            "当前PVF土火木水金罐可双倍，活动同名罐排除");
        That(new[] { 896, 992, 2681522, 2681540, 2681541, 10007116 }.All(id => !r.Boosters.AllowsDevilDouble(id)),
            "封印之锁、魔盒、黄金罐、史诗武器罐均不能套用NPC袖珍罐双倍");
        var pot = new InventoryItem(1001, new(InventoryListType.Main, 65), InventoryRewardGrantService.Create(r.Items.Get(potId), 20, now));
        var opening = entitled with { Inventory = entitled.Inventory with { Items = [pot] } };
        var pots = new InventoryBoosterService(r.Items, r.Boosters, r.GoldLimits, r.EpicPieces, contracts: r.Contracts);
        for (int i = 0; i < 8; i++)
        {
            var opened = pots.OpenLottery(opening, new(0, 65), out var reward, now);
            That(opened.Accepted && reward is not null &&
                opened.Plan!.After.Items.Count(item => item.Core.Kind == GameWorld.Items.ItemKind.Equipment) == (i + 1) * 2,
                "普通袖珍罐同一随机奖励生成两件独立装备");
            opening = opening with { Inventory = opened.Plan!.After };
        }
        var ninth = pots.OpenLottery(opening, new(0, 65), out _, now);
        That(ninth.Accepted && ninth.Plan!.After.Items.Count(item => item.Core.Kind == GameWorld.Items.ItemKind.Equipment) == 17,
            "双倍额度耗尽后正常开罐只发一份");
        var renewedPot = shop.Buy(opening, new(0, [new(100539, 0)]), out _, now);
        That(renewedPot.Accepted && DevilContractPolicy.LotteryAllowance(renewedPot.Plan!.After, now) == 16 &&
            DevilContractPolicy.Used(renewedPot.Plan.After, 1, now) == 8, "续购增加当天8次额度且保留已用次数");
        var limited = renewedPot.Plan!.After;
        for (int i = 0; i < 4; i++) limited = DevilContractPolicy.RenewLottery(limited, now, true);
        That(DevilContractPolicy.LotteryAllowance(limited, now) == 30 && DevilContractPolicy.LotteryAllowance(limited, now.AddDays(1)) == 8,
            "续购每日最多30次，次日恢复基础8次");
        var legacy = initial with { Inventory = initial.Inventory with { AccountWallet = initial.Inventory.AccountWallet
            .ActivateContract(58, 14 * 86400, now) } };
        var renewed = shop.Buy(legacy, new(0, [new(100542, 0)]), out _, now);
        That(renewed.Accepted && DevilContractPolicy.Expiry(renewed.Plan!.After.AccountWallet, 0) == now.ToUnixTimeSeconds() + 21 * 86400,
            "旧58会员续购黄金牌子项累计期限，不浪费原有剩余时间");
        var repair = new InventoryRepairService(r.Items, r.Repair);
        var current = initial with { Inventory = bought.Plan!.After };
        for (int i = 0; i < 6; i++)
        {
            current = current with { Inventory = current.Inventory with { Items = initial.Inventory.Items } };
            var repaired = repair.Repair(current, new(new(InventoryListType.Main, -1), -1, 1, false), now);
            That(repaired.Accepted && repaired.Plan!.After.Gold == initial.Inventory.Gold, "前6次自动修理免费");
            current = current with { Inventory = repaired.Plan!.After };
        }
        current = current with { Inventory = current.Inventory with { Items = initial.Inventory.Items } };
        That(!repair.Repair(current, new(new(InventoryListType.Main, -1), -1, 1, false), now).Accepted,
            "第7次零耐久修理被拒绝");
        That(repair.Repair(current, new(new(InventoryListType.Main, -1), -1, 1, false), now.AddDays(1)).Accepted,
            "次日刷新额度，不能沿用昨日6次");
        var effect = initial with { Inventory = initial.Inventory with { AccountWallet = initial.Inventory.AccountWallet
            .ActivateContract(583, 7 * 86400, now).ActivateContract(584, 7 * 86400, now) } };
        Guid sameInstance = Guid.NewGuid();
        for (int i = 0; i < 10; i++)
        {
            var applied = DevilContractPolicy.ReportDungeonEffect(effect, 3, i == 0 ? sameInstance : Guid.NewGuid(), now);
            That(applied.Accepted, "增益每日前10次可用");
            effect = effect with { Inventory = applied.Plan!.After };
            var duplicate = DevilContractPolicy.ReportDungeonEffect(effect, 3, effect.Inventory.DevilUses.Single().InstanceId, now.AddSeconds(1));
            That(duplicate.Accepted && !duplicate.Plan!.Changed, "同副本通知幂等不扣第二次");
        }
        That(!DevilContractPolicy.ReportDungeonEffect(effect, 3, Guid.NewGuid(), now).Accepted, "增益第11次拒绝");
        var heal = DevilContractPolicy.ReportDungeonEffect(effect, 4, sameInstance, now);
        effect = effect with { Inventory = heal.Plan!.After };
        That(!DevilContractPolicy.ReportDungeonEffect(effect, 4, sameInstance, now.AddDays(1)).Plan!.Changed &&
            DevilContractPolicy.ReportDungeonEffect(effect, 4, Guid.NewGuid(), now).Plan!.Changed,
            "自动恢复每副本一次，跨日不重复、新副本可重用");
        That(!DevilContractPolicy.ReportDungeonEffect(effect, 4, Guid.NewGuid(), now.AddDays(8)).Accepted,
            "精确检查服务有效期，过期不授予效果");
    }

    static byte[] Purchase(int commodity)
    {
        byte[] body = new byte[19]; body[2] = 1;
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(7), commodity);
        return body;
    }
}
