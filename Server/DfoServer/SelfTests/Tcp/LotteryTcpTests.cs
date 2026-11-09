using DfoServer.Game.Characters;
using DfoServer.Game.Session;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Persistence;
using DfoServer.Testing.Inventory;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static partial class LotteryTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        LotteryTests.Run(resources);
        FiniteJarTests.RunResources(resources.ItemResources, resources.Towns);
        LotteryGoldCostTests.RunResources(resources.ItemResources, resources.Towns);
        var items = resources.ItemResources;
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "lottery"), gm: true, resources: resources);
        var repo = new CharacterRepository(fixture.Database);
        var owners = new AccountOwners();
        using (var owner = owners.Acquire(fixture.Options.DevelopmentAccount, () => { }))
            repo.Create(owner, new("PotTcp", resources.Towns.Room, resources.Towns.InitialReturn)
            {
                Job = 15, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(15).Skills,
                Inventory = new([new(0, Bag(117), Core(LotteryTests.SourceId, 2)),
                    .. Enumerable.Range(0, 6).Select(i => new InventoryItem(0, Bag((short)(65 + i)), Core(490705157 + i, 5)))], Gold: 12345)
            });
        await fixture.StartAsync();
        BoosterTcpTests.SetExpirationDebug(fixture, true);
        using (var peer = Open(fixture))
        {
            Send(peer, 117);
            peer.Expect(1, 27, [0, 19, 0]);
            peer.Select();
            string before = Snapshot();
            foreach (byte[] malformed in new[] { Array.Empty<byte>(), new byte[3], new byte[5], Convert.FromHexString("01007500"), Convert.FromHexString("0000FFFF") })
            {
                peer.Send(27, malformed);
                peer.Expect(1, 27, [0, 19, 0]);
            }
            peer.Barrier();
            That(before == Snapshot(), "畸形与未知模式不改变源、奖励、审计或UID");
            for (int opened = 1; opened <= 2; opened++)
            {
                Send(peer, 117);
                byte[] ack = peer.Expect(1, 27).Body;
                That(ack.Length == 130 && ack[0] == 1 && U16(ack, 1) == 117 && U32(ack, 9) == 1 && U32(ack, 126) == 0,
                    "ACK27完整130B，奖励数量为增量而非已有堆叠总数");
                int id = (int)U32(ack, 5);
                short slot = (short)U16(ack, 3);
                That(id is >= 490705157 and <= 490705162 && slot == 65 + id - 490705157, "应答引用实际既有奖励槽");
                byte[] delta = peer.Expect(0, 14).Body;
                BoosterTcpTests.Entry(delta, 117, opened == 1 ? LotteryTests.SourceId : -1, opened == 1 ? 1 : 0);
                int at = Enumerable.Range(0, U16(delta, 1)).Select(index => 3 + index * 123).Single(index => U16(delta, index) == slot);
                int total = (int)U32(delta, at + 6);
                That(total is 6 or 7, "提交后差量携带合并总数");
                var authoritative = delta.AsSpan(at, 123).ToArray();
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(authoritative.AsSpan(6), 1);
                That(ack.AsSpan(3, 123).SequenceEqual(authoritative), "ACK普通记录除增量数量外与权威差量逐字节一致");
            }
            peer.Barrier();
            before = Snapshot();
            Send(peer, 117);
            peer.Expect(1, 27, [0, 19, 0]);
            peer.Barrier();
            That(before == Snapshot(), "罐子耗尽后重放不重复发奖");
        }
        await BoosterSelectionTcpTests.WaitForInitialDisconnectAsync(fixture,
            Convert.ToUInt16(fixture.Query("SELECT id FROM characters WHERE name='PotTcp'").Single()[0]));
        using (var owner = owners.Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var current = repo.List(owner).Single();
            InventoryService.Commit(repo, owner, current, InventoryRewardGrantService.TryGrantBatch(current,
                [new(LotteryTests.SourceId, 1)], items.Items, items.GoldLimits, items.EpicPieces, shopExpiration: true));
        }
        using (var peer = Open(fixture))
        {
            peer.Select();
            short slot = SourceSlot();
            string before = Snapshot();
            fixture.Sql("CREATE TRIGGER fail_lottery BEFORE INSERT ON inventory_audit_log BEGIN SELECT RAISE(ABORT,'lottery rollback'); END");
            Send(peer, slot);
            peer.Closed();
            That(before == Snapshot(), "晚期SQL失败不回成功并原子回滚源、奖励、审计和UID");
            fixture.Sql("DROP TRIGGER fail_lottery");
        }
        using (var peer = Open(fixture))
        {
            short slot = SourceSlot();
            BoosterTcpTests.Entry(peer.Select()[4].Body, slot, LotteryTests.SourceId, 1, true);
            Send(peer, slot);
            That(peer.Expect(1, 27).Body.Length == 130, "失败后重连仍可正常打开");
            peer.Expect(0, 14);
            peer.Barrier();
        }
        string committed = Snapshot();
        await fixture.StopAsync();
        await fixture.StartAsync();
        using (var peer = Open(fixture))
        {
            peer.Select();
            peer.Barrier();
            That(committed == Snapshot(), "重启后源和全部奖励及UID保持提交结果");
        }
        await fixture.StopAsync();
        That(fixture.Query("SELECT gold FROM characters") is [[12345L]], "资源未声明费用时不额外扣金币");
        That(fixture.Query("PRAGMA integrity_check") is [["ok"]] && fixture.Query("PRAGMA foreign_key_check").Length == 0, "袖珍罐数据库完整性");
        await MaterialCostAsync(source, root, resources);
        await RestrictedPotsAsync(source, root, resources);
        await FiniteGoldAsync(source, root, resources);
        await GoldCostAsync(source, root, resources);
        await GoldCostAsync(source, root, resources, pot: 10159699, cost: 100000, openings: 1);
        ItemCore Core(int id, int count = 1) => InventoryRewardGrantService.Create(items.Items.Get(id), count, shopExpiration: true);
        short SourceSlot()
        {
            using var owner = owners.Acquire(fixture.Options.DevelopmentAccount, () => { });
            return repo.List(owner).Single().Inventory.Items.Single(item => item.Core.ItemId == LotteryTests.SourceId).Position.Index;
        }
        string Snapshot() => JsonSerializer.Serialize(fixture.Query("SELECT revision,gold FROM characters")) +
            JsonSerializer.Serialize(fixture.Query("SELECT item_uid,item_core FROM character_inventory_items ORDER BY item_uid")) +
            JsonSerializer.Serialize(fixture.Query("SELECT count(*) FROM inventory_audit_log")) +
            JsonSerializer.Serialize(fixture.Query("SELECT seq FROM sqlite_sequence WHERE name='character_inventory_items'"));
    }
    static async Task FiniteGoldAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        const int pot = 490001063;
        var items = resources.ItemResources;
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "lottery-finite-gold"), resources: resources);
        var repository = new CharacterRepository(fixture.Database);
        var owners = new AccountOwners();
        using (var owner = owners.Acquire(fixture.Options.DevelopmentAccount, () => { }))
            repository.Create(owner, new("GoldPot", resources.Towns.Room, resources.Towns.InitialReturn)
            {
                Level = 95, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(0).Skills,
                Inventory = new([new(0, Bag(117), InventoryRewardGrantService.Create(items.Items.Get(pot), 2))], Gold: 12345)
            });
        await fixture.StartAsync();
        using (var peer = Open(fixture))
        {
            peer.Select();
            string before = Snapshot();
            fixture.Sql("CREATE TRIGGER fail_gold_pot BEFORE INSERT ON inventory_audit_log BEGIN SELECT RAISE(ABORT,'gold pot rollback'); END");
            Send(peer, 117);
            peer.Closed();
            That(Snapshot() == before, "金币开罐晚SQL失败原子保留源、金币、版本、审计和UID");
            fixture.Sql("DROP TRIGGER fail_gold_pot");
        }
        using (var peer = Open(fixture))
        {
            peer.Select();
            for (int opened = 1; opened <= 2; opened++)
            {
                Send(peer, 117);
                byte[] ack = peer.Expect(1, 27).Body;
                That(ack.Length == 130 && ack[0] == 1 && U16(ack, 1) == 117 &&
                    ack.AsSpan(3, 123).IndexOfAnyExcept((byte)0) == -1 && U32(ack, 126) == 100000,
                    "真实金币ACK27零奖励记录和末尾十万增量");
                byte[] delta = peer.Expect(0, 14).Body;
                BoosterTcpTests.Entry(delta, 117, opened == 1 ? pot : -1, opened == 1 ? 1 : 0);
                BoosterTcpTests.Entry(delta, 0, 0, 12345 + opened * 100000);
                peer.Barrier();
                That(fixture.Query("SELECT gold FROM characters") is var saved &&
                    Convert.ToInt64(saved.Single()[0]) == 12345 + opened * 100000 &&
                    fixture.Query("SELECT count(*) FROM character_inventory_items") is var inventory &&
                    Convert.ToInt64(inventory.Single()[0]) == (opened == 1 ? 1 : 0),
                    "真实钱包到账与源消费同提交，金币不创建实体；商店材料不重复扣费");
            }
            string exhausted = Snapshot();
            Send(peer, 117);
            peer.Expect(1, 27, [0, 19, 0]);
            peer.Barrier();
            That(Snapshot() == exhausted, "金币罐耗尽重放不重复加钱");
        }
        string committed = Snapshot();
        await fixture.StopAsync();
        await fixture.StartAsync();
        using (var peer = Open(fixture))
        {
            peer.Select();
            peer.Barrier();
            That(Snapshot() == committed && fixture.Query("PRAGMA integrity_check") is [["ok"]],
                "金币罐重启与重登保持真实金币及已消费源");
        }
        await fixture.StopAsync();
        string Snapshot() => JsonSerializer.Serialize(fixture.Query("SELECT revision,gold FROM characters")) +
            JsonSerializer.Serialize(fixture.Query("SELECT item_uid,item_core FROM character_inventory_items ORDER BY item_uid")) +
            JsonSerializer.Serialize(fixture.Query("SELECT count(*) FROM inventory_audit_log")) +
            JsonSerializer.Serialize(fixture.Query("SELECT seq FROM sqlite_sequence WHERE name='character_inventory_items'"));
    }

    static async Task RestrictedPotsAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        const int pot = 490002024;
        var items = resources.ItemResources;
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "lottery-restricted"), gm: true, resources: resources);
        var repository = new CharacterRepository(fixture.Database);
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
            repository.Create(owner, new("Restricted", resources.Towns.Room, resources.Towns.InitialReturn)
            {
                Level = 95, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(0).Skills,
                Inventory = new([new(0, new(InventoryListType.Main, 117), InventoryRewardGrantService.Create(items.Items.Get(pot), 2)),
                    new(0, new(InventoryListType.Main, 9), InventoryRewardGrantService.Create(items.Items.Get(27582)))])
            });
        await fixture.StartAsync();
        using (var peer = Open(fixture))
        {
            peer.Select();
            string before = Snapshot();
            fixture.Sql("CREATE TRIGGER fail_restricted_pot BEFORE INSERT ON inventory_audit_log BEGIN SELECT RAISE(ABORT,'restricted pot rollback'); END");
            Send(peer, 117);
            peer.Closed();
            That(Snapshot() == before, "英雄罐限制、奖励、源、UID及审计在晚SQL失败时一起回滚");
            fixture.Sql("DROP TRIGGER fail_restricted_pot");
        }
        using (var peer = Open(fixture))
        {
            peer.Select();
            for (int index = 0; index < 2; index++)
            {
                Send(peer, 117);
                var ack = peer.Expect(1, 27).Body;
                That(ack.Length == 130 && ack[0] == 1 && items.Boosters.Get(pot).Pools.Single().Rewards.Any(row => row.ItemId == U32(ack, 5)),
                    "英雄罐沿真实CMD27发完整原生装备条目，不改变随机奖池");
                peer.Expect(0, 14);
                peer.Barrier();
                var saved = fixture.Query("SELECT s.slot_index,i.item_core FROM character_inventory_slots s JOIN character_inventory_items i USING(item_uid) ORDER BY s.slot_index");
                var rewards = saved.Where(row => Convert.ToInt64(row[0]) != 9 && Convert.ToInt64(row[0]) != 117).ToArray();
                That(rewards.Length == index + 1 && rewards.All(row =>
                {
                    var core = JsonSerializer.Deserialize<ItemCore>((string)row[1]!)!;
                    return core.DisjointForbidden && core.DestructionSoulForbidden;
                }), "真实落库奖励保留两项实例限制，原生wire长度不变");
                string before = Snapshot();
                peer.Send(26, Build(writer =>
                { writer.Write((short)U16(ack, 3)); writer.Write((byte)0); writer.Write((short)-1); writer.Write(0); }));
                peer.Expect(1, 26, [0, 19, 0]);
                peer.Expect(0, 14);
                peer.Barrier();
                That(Snapshot() == before, "英雄罐奖励真实CMD26拒绝分解，限制不是只存在于JSON");
            }
            var ordinary = JsonSerializer.Deserialize<ItemCore>((string)fixture.Query("SELECT i.item_core FROM character_inventory_slots s JOIN character_inventory_items i USING(item_uid) WHERE s.slot_index=9").Single()[0]!)!;
            That(!ordinary.DisjointForbidden && !ordinary.DestructionSoulForbidden,
                "同模板既有普通装备不被按模板反补限制");
            string exhausted = Snapshot();
            Send(peer, 117);
            peer.Expect(1, 27, [0, 19, 0]);
            peer.Barrier();
            That(Snapshot() == exhausted, "英雄罐耗尽重放不重复发奖或改变限制");
        }
        string committed = Snapshot();
        await fixture.StopAsync();
        await fixture.StartAsync();
        using (var peer = Open(fixture))
        {
            peer.Select();
            peer.Barrier();
            That(Snapshot() == committed, "进程重启及重登保留英雄奖励实例限制和UID");
        }
        await fixture.StopAsync();
        string Snapshot() => JsonSerializer.Serialize(fixture.Query("SELECT revision FROM characters")) +
            JsonSerializer.Serialize(fixture.Query("SELECT item_uid,item_core FROM character_inventory_items ORDER BY item_uid")) +
            JsonSerializer.Serialize(fixture.Query("SELECT count(*) FROM inventory_audit_log")) +
            JsonSerializer.Serialize(fixture.Query("SELECT seq FROM sqlite_sequence WHERE name='character_inventory_items'"));
    }

    public static async Task MaterialCostAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        // RED时先保存两个旧API独立观察，再断言；不依赖新增定义属性即可编译。
        LotteryTests.RunMaterialCost(resources);
        const int pot = 490705287, material = 490705286;
        var items = resources.ItemResources;
        var now = DateTimeOffset.UtcNow;
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "lottery-material"), resources: resources);
        var repo = new CharacterRepository(fixture.Database);
        var owners = new AccountOwners();
        var random = new MaterialRandom();
        var clock = new DailyBoosterTestClock(now);
        var service = new InventoryBoosterService(items.Items, items.Boosters, items.GoldLimits, items.EpicPieces,
            random, contracts: items.Contracts, clock: clock);
        using (var owner = owners.Acquire("pot-material-plan", () => { }))
        {
            var created = repo.Create(owner, New("CostPlan", 1, 1));
            var session = new GameSession(repo, resources.Towns, owner);
            session.Select(created.Slot);
            session.MarkReady();
            var saved = session.Selected!;
            var planned = service.OpenLottery(saved, new(0, 117), out var receipt, now);
            That(planned.Accepted && receipt is { ItemId: 490705288, Count: 1 } &&
                planned.Plan!.After.Find(Bag(117)) is null && planned.Plan.After.Find(Bag(3)) is null,
                "真实资源同冻结计划同时扣罐、扣材并发完整奖励");
            int calls = random.Calls;
            string before = Snapshot(), frozen = JsonSerializer.Serialize(planned.Plan!.After), frozenReceipt = JsonSerializer.Serialize(receipt);
            fixture.Sql("CREATE TRIGGER fail_material_plan BEFORE INSERT ON inventory_audit_log WHEN NEW.item_id_after=490705288 BEGIN SELECT RAISE(ABORT,'material plan rollback'); END");
            Reject(() => session.PlanAndCommitBooster(_ => planned, service.UsePlaces(pot), pot), "奖励写入后的晚期审计失败传播");
            That(Snapshot() == before && ReferenceEquals(session.Selected, saved) && random.Calls == calls &&
                JsonSerializer.Serialize(planned.Plan.After) == frozen && JsonSerializer.Serialize(receipt) == frozenReceipt,
                "晚期SQL失败原子回滚罐、材料、奖励、详情、版本、审计和UID，冻结结果不重抽");
            fixture.Sql("DROP TRIGGER fail_material_plan");
            var committed = session.PlanAndCommitBooster(_ => planned, service.UsePlaces(pot), pot);
            That(committed.Accepted && session.Selected!.Revision == saved.Revision + 1 && random.Calls == calls &&
                JsonSerializer.Serialize(new CharacterRepository(fixture.Database).List(owner).Single()) == JsonSerializer.Serialize(session.Selected),
                "确认回滚后提交同一冻结结果一次，重开库保留完整结果");
            before = Snapshot();
            Reject(() => repo.CommitInventory(owner, saved, planned.Plan), "旧revision计划不能二次扣材或发奖");
            That(Snapshot() == before, "旧revision失败不分配UID或增加审计");
            var current = session.Selected!;
            using var replacement = owners.Acquire("pot-material-plan", () => { });
            Reject(() => repo.CommitInventory(owner, current, new(current.Inventory, current.Inventory, "stale_owner")),
                "旧owner不能提交当前快照计划");
            That(Snapshot() == before, "旧owner失败无资产变化");
        }
        using (var owner = owners.Acquire("pot-material-expiry", () => { }))
        {
            var created = repo.Create(owner, New("CostExpiry", 1, 1) with { Inventory = new([
                new(0, Bag(117), Core(pot)), new(0, Bag(3), Core(material) with { ExpireTime = (int)now.AddSeconds(5).ToUnixTimeSeconds() })]) });
            var session = new GameSession(repo, resources.Towns, owner);
            session.Select(created.Slot);
            session.MarkReady();
            var planned = service.OpenLottery(session.Selected!, new(0, 117), out _, now);
            That(planned.Accepted, "到期前材料可生成合法冻结计划");
            string before = Snapshot();
            int calls = random.Calls;
            clock.Advance(TimeSpan.FromSeconds(5));
            var rejected = session.PlanAndCommitBooster(_ => planned, service.UsePlaces(pot), pot);
            That(!rejected.Accepted && rejected.RejectReason == "qualification_expired" && Snapshot() == before && random.Calls == calls,
                "规划后材料恰到期经既有Commit资格拒绝，罐材料奖励和UID完整保留");
        }
        using (var owner = owners.Acquire(fixture.Options.DevelopmentAccount, () => { }))
            repo.Create(owner, New("CostTcp", 2, 0));
        await fixture.StartAsync();
        using (var peer = Open(fixture))
        {
            peer.Select();
            string before = Snapshot();
            Send(peer, 117);
            peer.Expect(1, 27, [0, 19, 0]);
            peer.Barrier();
            That(Snapshot() == before, "完整CMD27无材料拒绝，不扣罐、不发奖、不增加审计或UID");
        }
        using (var owner = owners.Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var current = repo.List(owner).Single();
            repo.CommitInventory(owner, current, new(current.Inventory,
                current.Inventory with { Items = current.Inventory.Items.Add(new(0, Bag(3), Core(material, 2))) }, "material_test_seed"));
        }
        using (var peer = Open(fixture))
        {
            peer.Select();
            Send(peer, 117);
            Ack(peer, sourceRemaining: 1, materialRemaining: 1);
            peer.Barrier();
        }
        using (var peer = Open(fixture))
        {
            peer.Select();
            string before = Snapshot();
            fixture.Sql("CREATE TRIGGER fail_material_tcp BEFORE INSERT ON inventory_audit_log BEGIN SELECT RAISE(ABORT,'material tcp rollback'); END");
            Send(peer, 117);
            peer.Closed();
            That(Snapshot() == before, "真实CMD27晚期SQL失败不回成功，罐材料奖励与UID整笔回滚");
            fixture.Sql("DROP TRIGGER fail_material_tcp");
        }
        using (var peer = Open(fixture))
        {
            var selected = peer.Select();
            BoosterTcpTests.Entry(selected[4].Body, 117, pot, 1, true);
            BoosterTcpTests.Entry(selected[4].Body, 3, material, 1, true);
            Send(peer, 117);
            Ack(peer, sourceRemaining: 0, materialRemaining: 0);
            peer.Barrier();
            string before = Snapshot();
            Send(peer, 117);
            peer.Expect(1, 27, [0, 19, 0]);
            peer.Barrier();
            That(Snapshot() == before, "耗尽重放不重复扣材、发奖或分配UID");
        }
        string committedState = Snapshot();
        await fixture.StopAsync();
        await fixture.StartAsync();
        using (var peer = Open(fixture))
        {
            peer.Select();
            peer.Barrier();
            That(Snapshot() == committedState, "Runtime重建真实登录后完整保留两次扣材发奖结果");
        }
        await fixture.StopAsync();
        That(fixture.Query("PRAGMA integrity_check") is [["ok"]] && fixture.Query("PRAGMA foreign_key_check").Length == 0,
            "材料开罐临时库完整性与外键保持");

        NewCharacter New(string name, int pots, int materials) => new(name, resources.Towns.Room, resources.Towns.InitialReturn)
        {
            Job = 0, GrowType = 1, Level = 95, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(0).Skills,
            Inventory = new([new(0, Bag(117), Core(pot, pots)), .. (materials == 0 ? Array.Empty<InventoryItem>() :
                new InventoryItem[] { new(0, Bag(3), Core(material, materials)) })], Gold: 12345)
        };
        ItemCore Core(int id, int count = 1) => InventoryRewardGrantService.Create(items.Items.Get(id), count, now);
        string Snapshot() => JsonSerializer.Serialize(fixture.Query("SELECT id,revision,gold FROM characters ORDER BY id")) +
            JsonSerializer.Serialize(fixture.Query("SELECT item_uid,item_core FROM character_inventory_items ORDER BY item_uid")) +
            JsonSerializer.Serialize(fixture.Query("SELECT * FROM character_inventory_slots ORDER BY character_id,list_type,slot_index")) +
            JsonSerializer.Serialize(fixture.Query("SELECT * FROM inventory_avatar_details ORDER BY item_uid")) +
            JsonSerializer.Serialize(fixture.Query("SELECT * FROM inventory_creature_details ORDER BY item_uid")) +
            JsonSerializer.Serialize(fixture.Query("SELECT count(*) FROM inventory_audit_log")) +
            JsonSerializer.Serialize(fixture.Query("SELECT seq FROM sqlite_sequence WHERE name='character_inventory_items'"));
        void Ack(TcpPeer peer, int sourceRemaining, int materialRemaining)
        {
            byte[] ack = peer.Expect(1, 27).Body;
            That(ack.Length == 130 && ack[0] == 1 && U16(ack, 1) == 117 && U32(ack, 9) == 1 && U32(ack, 126) == 0,
                "材料罐成功保持完整130B ACK27与单份奖励增量");
            int id = (int)U32(ack, 5);
            short slot = (short)U16(ack, 3);
            That(id is >= 490705288 and <= 490705292, "实际回执为原五项之一，不跟入子盒");
            byte[] delta = peer.Expect(0, 14).Body;
            BoosterTcpTests.Entry(delta, 117, sourceRemaining == 0 ? -1 : pot, sourceRemaining);
            BoosterTcpTests.Entry(delta, 3, materialRemaining == 0 ? -1 : material, materialRemaining);
            int at = Enumerable.Range(0, U16(delta, 1)).Select(index => 3 + index * 123).Single(index => U16(delta, index) == slot);
            var authoritative = delta.AsSpan(at, 123).ToArray();
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(authoritative.AsSpan(6), 1);
            That(ack.AsSpan(3, 123).SequenceEqual(authoritative), "ACK奖励和已提交权威差量完整一致，仅堆叠数量用增量");
        }
        static void Reject(Action action, string message)
        {
            bool rejected = false;
            try { action(); }
            catch (Exception error) when (error is InvalidOperationException or SqliteException) { rejected = true; }
            That(rejected, message);
        }
    }
    sealed class MaterialRandom : Random
    {
        public int Calls { get; private set; }
        public override long NextInt64(long maxValue) { Calls++; return 0; }
    }
    static InventorySlot Bag(short slot) => new(InventoryListType.Main, slot);
    static void Send(TcpPeer peer, short slot) => peer.Send(27, Build(writer => { writer.Write((ushort)0); writer.Write(slot); }));
}
