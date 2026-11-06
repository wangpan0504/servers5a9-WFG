using System.Buffers.Binary;
using System.Collections.Immutable;
using System.IO.Compression;
using DfoServer.Game.Accounts;
using DfoServer.Game.Events;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.MonsterMockBattle;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.MonsterMockBattle;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Persistence;
using DfoServer.Testing.Inventory;
using Microsoft.Data.Sqlite;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class MonsterMockBattleTests
{
    internal static void RunCore()
    {
        var catalog = Catalog();
        var now = DateTimeOffset.Parse("2026-11-05T04:00:00Z");
        byte[] payload = new byte[20];
        var emptyAccount = MockBattleAccount.Create(now, "MockA");
        That(emptyAccount.Coins == 0 && emptyAccount.Hearts == 0 && emptyAccount.Snapshot.Parameters6[4] == 0,
            "卡册领域模型空状态不预先伪造资产或恢复倒计时");
        Supply(now);
        EventState();
        var initial = emptyAccount.WithCoins(1000);
        var single = MockBattleRules.Apply(catalog, initial, 0, 1, 0, payload, _ => 0);
        That(single.After.Coins == 900 && single.AcquiredSlots.SequenceEqual(new byte[] { 0 }) &&
            single.After.Snapshot.Cards[0] is { CardId: 0, Stars: 1, Position: -1 } &&
            single.After.Snapshot.Achievements[0].Value == 1, "单抽按真实权重扣费、首空槽及召唤成就");
        var batch = MockBattleRules.Apply(catalog, initial, 0, 5, 0, payload, _ => 1);
        That(batch.After.Coins == 550 && batch.AcquiredSlots.Length == 5 &&
            batch.After.Snapshot.Cards.Take(5).All(row => row.CardId == 1), "五连完整折扣及同一次规划的五张卡片");
        var selected = MockBattleRules.Apply(catalog, initial, 1, 50001, 2, payload, _ =>
            throw new InvalidOperationException("指定召唤不应抽随机数"));
        That(selected.ConsumedItem == 50001 && selected.After.Snapshot.Cards[0] is { CardId: 1, Stars: 2 },
            "零权重指定卡券按池内序号选卡，不将序号误当模板号");
        Reject<MockBattleRejectedException>(() => MockBattleRules.Apply(catalog, single.After, 2, 0, 0, payload, _ => 0));
        var locked = MockBattleRules.Apply(catalog, single.After, 4, 0, 1, payload, _ => 0).After;
        Reject<MockBattleRejectedException>(() => MockBattleRules.Apply(catalog, locked, 2, 0, 0, payload, _ => 0));
        var duplicate = single.After with { Snapshot = single.After.Snapshot with
        { Cards = single.After.Snapshot.Cards.SetItem(1, new(0, 1, -1, 0, 0)) } };
        var composed = MockBattleRules.Apply(catalog, duplicate, 3, 0, 1, payload, _ => 0).After;
        That(composed.Coins == 800 && composed.Snapshot.Cards[0].Stars == 2 && composed.Snapshot.Cards[1].IsEmpty,
            "合成扣资源费用并保留目标槽、消费同种同星源卡");
        byte[] formation = Enumerable.Repeat((byte)255, 20).ToArray();
        formation[0] = 0; formation[1] = 4; formation[17] = formation[18] = formation[19] = 0;
        var placed = MockBattleRules.Apply(catalog, composed, 5, 0, 0, formation, _ => 0).After;
        That(placed.Snapshot.Cards[0].Position == 4, "阵型按原生17B复制和三字节零尾接收");
        Reject<MockBattleRejectedException>(() => MockBattleRules.Apply(catalog, placed, 2, 0, 0, payload, _ => 0));
        var attribute = MockBattleRules.Apply(catalog, placed, 6, 0, 1, payload, _ => 0).After;
        That(attribute.Snapshot.Cards[0].Attributes == 1, "属性按星级双选一位保存");
        Reject<MockBattleRejectedException>(() => MockBattleRules.Apply(catalog, attribute, 6, 0, 1, payload, _ => 0));
        var claimMutation = MockBattleRules.Apply(catalog, single.After, 9, 0, 0, payload, _ => 0);
        var claim = claimMutation.After;
        That(claim.Coins == 900 && claim.Snapshot.Achievements[0].Claimed == 1 &&
            claimMutation.Reward == new MockBattleItemReward(490013685, 100), "成就规则冻结硬币奖励，由服务层规划真实钱包与领取位同事务提交");
        Reject<MockBattleRejectedException>(() => MockBattleRules.Apply(catalog, claim, 9, 0, 0, payload, _ => 0));
        var empty = MonsterMockBattlePackets.Info(MockBattleSnapshot.Empty);
        byte[] expected = new byte[980];
        expected.AsSpan(24, 9).Fill(255);
        for (int index = 0; index < 100; index++) { expected[138 + index * 8] = 255; expected[140 + index * 8] = 255; }
        Bytes(empty, expected, "1766完整980B空状态独立偏移与空卡值");
        byte[] request = new byte[45];
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(13), 6);
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(17), 99);
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(21), 5);
        request[25] = 1;
        That(MonsterMockBattleRequests.Parse(request) is { Operation: 6, Argument0: 99, Argument1: 5, Payload20.Length: 20 },
            "1736正文保留嵌入前缀后解析三DWORD与20B附属数据");
        Reject<InvalidDataException>(() => MonsterMockBattleRequests.Parse(request.AsSpan(0, 44)));
        byte[] ack = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 255, 255, 255, 1, 5, 0, 0, 0];
        Bytes(MonsterMockBattlePackets.Acknowledge(0, 1, [0, 1, 255, 255, 255], [1, 5, 0, 0, 0]), ack,
            "1736完整19B回执分别发送卡槽和星级，未获得的位置保留FF/0");
        Bytes(MonsterMockBattlePackets.Acknowledge(0, 0, [9, 2, 7, 0, 99], [1, 2, 3, 4, 5]),
            [1, 0, 0, 0, 0, 0, 0, 0, 0, 9, 2, 7, 0, 99, 1, 2, 3, 4, 5],
            "五连回执的一至五星分别对应各自卡槽，星级不能填锁定标记或全零");
        byte[] spawn = [1, 0xd2, 0x55, 0x42, 0x18, 0x34, 0x12, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 4];
        Bytes(MonsterMockBattlePackets.Spawns([new(new(16, 2, 4, 0, 1), 407000530, 0x1234, 1)]), spawn,
            "1767发送怪物407000530而非卡片16，对象key、阵营、普通品级和格位相互独立");
        Reject<InvalidDataException>(() => MonsterMockBattlePackets.Spawns([new(new(16, 1, 3, 0, 1), 407000530, 2186, 0, 95)]));
        Store(initial, now);
    }

    static void EventState()
    {
        byte[] expected = new byte[68];
        expected[0] = 1;
        expected[2] = 0xE4; expected[3] = 0x09; expected[4] = 1;
        expected[17] = 1;
        expected[18] = 0xE4; expected[19] = 0x09;
        expected[24] = 30;
        var encoded = SeriaLuckPackets.EventInfoCompressed(extraEvents: [2532], activeEvents: [2532],
            includeSeriaLuck: false, eventValues: new Dictionary<ushort, IReadOnlyList<uint>>
            { [2532] = [0, 30, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0] });
        Bytes(Inflate(encoded), expected, "108活动2532完整68B目录及状态，第二DWORD为体力上限30，其余11项为0");

        expected[2] = expected[18] = 0xD8;
        expected[20] = 0x78; expected[21] = 0x56; expected[22] = 0x34; expected[23] = 0x12;
        expected[24] = 0;
        var legacy = SeriaLuckPackets.EventInfoCompressed(extraEvents: [2520], activeEvents: [2520],
            firstValues: new Dictionary<ushort, uint> { [2520] = 0x12345678 }, includeSeriaLuck: false);
        Bytes(Inflate(legacy), expected, "108既有firstValues仍只写其它活动首DWORD，其余11项为0");

        static byte[] Inflate(byte[] compressed)
        {
            using var input = new MemoryStream(compressed);
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            zlib.CopyTo(output);
            return output.ToArray();
        }
    }

    static void Supply(DateTimeOffset now)
    {
        long origin = now.ToUnixTimeSeconds();
        var oldEmpty = MockBattleAccount.Create(now, "Supply") with { Revision = 1 };
        That(oldEmpty.NeedsSupplyInitialization && oldEmpty.WithCoins(250).NeedsSupplyInitialization,
            "旧首版空卡册可补初始化，已有硬币余额不剥夺资格");
        That(!(oldEmpty with { SupplyInitialized = true }).NeedsSupplyInitialization &&
            !(oldEmpty with { Revision = 2 }).NeedsSupplyInitialization,
            "已初始化或已变更卡册不重复补初始体力");
        That(!(oldEmpty with { Snapshot = oldEmpty.Snapshot with
            { Cards = oldEmpty.Snapshot.Cards.SetItem(0, new(0, 1, -1, 0, 0)) } }).NeedsSupplyInitialization &&
            !(oldEmpty with { Snapshot = oldEmpty.Snapshot with
            { Worlds = oldEmpty.Snapshot.Worlds.SetItem(0, new(1, 1)) } }).NeedsSupplyInitialization &&
            !(oldEmpty with { Snapshot = oldEmpty.Snapshot with
            { Achievements = oldEmpty.Snapshot.Achievements.SetItem(0, new(1, 0)) } }).NeedsSupplyInitialization,
            "旧卡片、关卡或成就进度均不按空档补发");
        var account = oldEmpty.WithCoins(250);
        account = account with
        {
            SupplyInitialized = true, CoinMailDay = 20261105,
            Snapshot = account.Snapshot with { Parameters6 = account.Snapshot.Parameters6.SetItem(1, 20) }
        };
        var partial = account.Recover(now.AddSeconds(249));
        That(partial.Hearts == 20 && partial.HeartAt == origin && partial.Snapshot.Parameters6[4] == 51,
            "未满五分钟只投影剩余秒数，不提前增加体力");
        var exact = account.Recover(now.AddSeconds(300));
        That(exact.Hearts == 21 && exact.HeartAt == origin + 300 && exact.Snapshot.Parameters6[4] == 300,
            "恢复整点恰好增加一点并开始下一周期");
        var remainder = account.Recover(now.AddSeconds(637));
        That(remainder.Hearts == 22 && remainder.HeartAt == origin + 600 && remainder.Snapshot.Parameters6[4] == 263 &&
            remainder.Recover(now.AddSeconds(900)).Hearts == 23,
            "离线累计完整周期后保留余秒，后续读取不丢失进度");
        var full = account.Recover(now.AddHours(2));
        That(full.Hearts == 30 && full.HeartAt == origin + 7200 && full.Snapshot.Parameters6[4] == 0,
            "长时间离线恢复封顶三十，满额不显示倒计时");
        var spent = full with { Snapshot = full.Snapshot with { Parameters6 = full.Snapshot.Parameters6.SetItem(1, 29) } };
        That(spent.Recover(now.AddSeconds(7499)) is { Hearts: 29 } waiting && waiting.Snapshot.Parameters6[4] == 1 &&
            spent.Recover(now.AddSeconds(7500)).Hearts == 30,
            "满额期间不储存额外周期，消费后重新计时");
        var extra = account with { Snapshot = account.Snapshot with { Parameters6 = account.Snapshot.Parameters6.SetItem(1, 40) } };
        var extraAfter = extra.Recover(now.AddHours(2));
        That(extraAfter.Hearts == 40 && extraAfter.HeartAt == origin + 7200 && extraAfter.Snapshot.Parameters6[4] == 0,
            "存量超额体力不裁剪，也不继续自动恢复");
        var rollback = remainder.Recover(now.AddSeconds(599));
        That(rollback.Hearts == 22 && rollback.HeartAt == origin + 600 && rollback.Snapshot.Parameters6[4] == 300,
            "时钟回拨不倒退恢复时点或额外增加体力");
        That(account.Hearts == 20 && account.HeartAt == origin && account.Snapshot.Parameters6[4] == 0 &&
            full.Coins == 250 && full.SupplyInitialized && full.CoinMailDay == 20261105 && full.Revision == 1,
            "恢复返回不可变候选，保持硬币、体力补给标记、业务日戳与卡册版本");
    }

    static void Store(MockBattleAccount initial, DateTimeOffset now)
    {
        string root = Path.Combine(Path.GetTempPath(), "S5A9-mock-battle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "game.db");
        try
        {
            var repository = new CharacterRepository(path);
            var owners = new AccountOwners();
            using var owner = owners.Acquire("mock-owner", () => { });
            var character = repository.Create(owner, new("MockA", new(1, 0, 100, 100), null));
            var schedule = new ServerEventRepository(path).Read(ServerEventService.NolanVisibilityId);
            var first = new MockBattlePlan(initial, new(initial, []),
                new(character.Inventory, character.Inventory with { Gold = 1 }, "mock-test"), schedule, now);
            var saved = repository.CommitMockBattle(owner, character, first);
            That(saved.Account.Revision == 1 && saved.Character.Inventory.Gold == 1, "卡册与真实资产同事务首次落库");
            var next = first with { Before = saved.Account, Mutation = new(saved.Account.WithCoins(888), []),
                Inventory = new(saved.Character.Inventory, saved.Character.Inventory with { Gold = 2 }, "mock-test") };
            using (var db = Open())
            {
                using var command = db.CreateCommand();
                command.CommandText = "CREATE TRIGGER mock_failure BEFORE UPDATE ON account_monster_mock_battle BEGIN SELECT RAISE(ABORT,'mock failure'); END";
                command.ExecuteNonQuery();
            }
            Reject<SqliteException>(() => repository.CommitMockBattle(owner, saved.Character, next));
            That(repository.ReadMockBattle(owner) is { Revision: 1, Coins: 1000 } &&
                repository.List(owner).Single().Inventory.Gold == 1, "资产真实写后卡册故障回滚角色revision和余额");
            using (var db = Open())
            {
                using var command = db.CreateCommand(); command.CommandText = "DROP TRIGGER mock_failure"; command.ExecuteNonQuery();
            }
            var retried = repository.CommitMockBattle(owner, saved.Character, next);
            That(retried.Account.Revision == 2 && new CharacterRepository(path).ReadMockBattle(owner)!.Coins == 888,
                "同计划重试与重开仓储恢复保留实际提交结果");
            Reject<MockBattleRejectedException>(() => repository.CommitMockBattle(owner, saved.Character, next));
            Economy(repository, path, now);
            using (var db = Open())
            {
                using var command = db.CreateCommand();
                command.CommandText = "DROP TABLE account_monster_mock_battle_runs; DROP TABLE account_monster_mock_battle; PRAGMA user_version=197";
                command.ExecuteNonQuery();
            }
            _ = new CharacterRepository(path);
            That(repository.List(owner).Single().Inventory.Gold == 2 && repository.ReadMockBattle(owner) is null,
                "197升级199保留原角色资产，不补造卡册进度");
            SqliteConnection Open()
            {
                var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
                db.Open(); return db;
            }
        }
        finally { TestFiles.DeleteDirectory(root, true); }
    }

    static void Economy(CharacterRepository repository, string path, DateTimeOffset now)
    {
        using var owner = new AccountOwners().Acquire("mock-economy", () => { });
        var character = repository.Create(owner, new("MockCoin", new(1, 0, 100, 100), null));
        var clock = new DailyBoosterTestClock(now);
        var catalog = Catalog(mixedWorldRewards: true);
        var items = new ItemCatalog(null!, new Dictionary<int, string>(), new Dictionary<int, string>(), 0);
        var coin = new ItemTemplate(MonsterMockBattleService.CoinItemId, ItemKind.Material, 0, 1, 0, int.MaxValue,
            0, 0, 0, "[trade]", ushort.MaxValue, 0, "MockCoin") { AccountMaterial = new(2000) };
        items.PrefillTemplates([new(coin.Id, coin), new(50001, coin with { Id = 50001, AccountMaterial = null })]);
        var events = new ServerEventService(new ServerEventRepository(path), clock);
        var schedule = events.Read(ServerEventService.NolanVisibilityId).Schedule;
        var service = new MonsterMockBattleService(repository, catalog, new(), items,
            new("[gold limit from level]\n0 1000000\n[/gold limit from level]"),
            new("[piece list]\n90001 90002\n[/piece list]"), events, clock, _ => 0);
        var initial = service.PrepareRefresh(owner, character)!;
        That(initial.Mail is null && initial.Mutation.After is { Coins: 100, Hearts: 30, SupplyInitialized: true } &&
            initial.Inventory.After.AccountWallet.Material(coin.Id).Balance == 100 && initial.Inventory.After.Items.IsEmpty,
            "首次仅领取当日100硬币，直接规划共通材料钱包，与30体力独立，不造实体物品或邮件");
        var saved = service.Commit(owner, character, initial);
        That(service.PrepareRefresh(owner, saved.Character) is null, "同日重放不重复日币或体力");

        // 准备已有当日日邮件日戳、缺少体力初始化标记的旧状态，不重复当天100。
        var legacy = saved.Account with { SupplyInitialized = false };
        saved = service.Commit(owner, saved.Character, new(saved.Account, new(legacy, []),
            new(saved.Character.Inventory, saved.Character.Inventory, "mock-legacy-coins-fixture"), schedule, now));
        var upgrade = service.PrepareRefresh(owner, saved.Character)!;
        That(upgrade.Mail is null && upgrade.Mutation.After is { Coins: 100, Hearts: 30, SupplyInitialized: true } &&
            upgrade.Mutation.After.CoinMailDay == legacy.CoinMailDay, "旧日邮件日戳阻止当天重复100，不额外补发硬币");
        saved = service.Commit(owner, saved.Character, upgrade);
        clock.Advance(TimeSpan.FromDays(1));
        var daily = service.PrepareRefresh(owner, saved.Character)!;
        That(daily.Mail is null && daily.Mutation.After.Coins == 200 && daily.Inventory.After.AccountWallet.Material(coin.Id).Balance == 200,
            "次日100直接足额进入钱包，保留体力初始化状态");
        saved = service.Commit(owner, saved.Character, daily);
        saved = service.Commit(owner, saved.Character, service.Prepare(owner, saved.Character, 0, 1, 0, new byte[20]));
        var achievement = service.Prepare(owner, saved.Character, 9, 0, 0, new byte[20]);
        That(achievement.Mail is null && achievement.Mutation.After.Coins == 200 &&
            achievement.Inventory.After.AccountWallet.Material(coin.Id).Balance == 200 && achievement.Mutation.After.Snapshot.Achievements[0].Claimed == 1,
            "召唤成就100硬币直接加回真实钱包，与领取位同一计划且不投邮件");
        saved = service.Commit(owner, saved.Character, achievement);
        Reject<MockBattleRejectedException>(() => service.Prepare(owner, saved.Character, 9, 0, 0, new byte[20]));

        // 此处仅准备奖励规划所需的入场revision；完整入场与真实通关由TCP用例验证。
        var admission = new MockBattleAdmission(catalog.Worlds[0], saved.Account, saved.Account, [], null, schedule, clock.GetUtcNow(), catalog);
        saved = service.Commit(owner, saved.Character, new(saved.Account, new(saved.Account, []),
            new(saved.Character.Inventory, saved.Character.Inventory, "mock-victory-plan-fixture"), schedule, clock.GetUtcNow()));
        var victory = service.PrepareVictory(owner, saved.Character, new(saved.Character.Id, 0, 1, Guid.NewGuid(), 1), admission);
        That(victory.After.Coins == 327 && victory.Inventory.After.AccountWallet.Material(coin.Id).Balance == 327 &&
            victory.Mail is { Items: [{ Core.ItemId: 50001, Core.Count: 2 }] },
            "通关120及Items内7币合并直入钱包，邮件仅保留两份非硬币道具");
        items.PrefillTemplates([new(coin.Id, coin with { AccountMaterial = new(200) })]);
        clock.Advance(TimeSpan.FromDays(1));
        Reject<MockBattleRejectedException>(() => service.PrepareRefresh(owner, saved.Character));
        That(repository.ReadMockBattle(owner) is { Coins: 200 } current &&
            current.Revision == saved.Account.Revision && current.CoinMailDay == saved.Account.CoinMailDay,
            "超过材料持有上限拒绝整笔日币，不吞奖励或提前消费业务日资格");
    }

    internal static MonsterMockBattleCatalog Catalog(bool mixedWorldRewards = false)
    {
        var names = new List<string>();
        var tokens = new List<PvfToken>();
        PvfToken Text(string value, byte kind = 7) { int index = names.Count; names.Add(value); return new(kind, index); }
        void Section(string name, IEnumerable<PvfToken> values)
        { tokens.Add(Text("[" + name + "]", 3)); tokens.AddRange(values); tokens.Add(Text("[/" + name + "]", 3)); }
        IEnumerable<PvfToken> Ints(params int[] values) => values.Select(value => new PvfToken(0, value));
        Section("monster status base parameter", Ints(2000, 100, 0, 0));
        foreach (int id in new[] { 0, 1 })
        {
            var card = Ints(id, 1001 + id, 0, 0, 0).ToList();
            for (int mode = 0; mode < 2; mode++)
            {
                for (int star = 0; star < 5; star++) card.AddRange(Ints(20, 10, 100, 3));
                for (int attribute = 0; attribute < 10; attribute++)
                { card.AddRange(Ints(attribute, 0, 0, 0)); for (int rate = 0; rate < 4; rate++) card.Add(new(2, 0)); }
            }
            card.AddRange(Ints(0, 0, 0)); Section("user card info", card);
        }
        Section("monster card buy cost", Ints(100, 10));
        Section("monster card compose cost", Ints(100, 200, 500, 950));
        Section("random monster card booster by coin", Ints(0, 1, 1, 1, 3, 1));
        Section("random monster card booster by item", Ints(50001, 2, 0, 0, 1, 1, 0, 2));
        Section("achievement info", Enumerable.Range(0, 21).SelectMany(id =>
            Ints(id, id == 20 ? 0 : 1, 0, id == 0 ? 1 : id == 20 ? 20 : 100, 490013685, 100, 0, 0)));
        foreach (var (id, mode) in new[] { (0, 0), (20, 1) })
        {
            var world = Ints(id, 0, 0, 0, 0, 0).ToList();
            world.AddRange(mixedWorldRewards ? Ints(2, 1, MonsterMockBattleService.CoinItemId, 7, 50001, 2, 120) : Ints(0, 1, 120));
            world.AddRange(Ints(0, 1, 60, 0, 8, 100 + id, mode));
            for (int index = 0; index < 5; index++) { world.Add(new(0, 1)); world.Add(Text("")); }
            Section("world info", world);
        }
        Section("pvp info", Ints(5, 20, 5));
        var positions = new List<PvfToken>();
        for (int index = 0; index < 18; index++)
        {
            positions.AddRange(Ints(1001, 1, 0, 100, 150, 0, 1, 1));
            positions.Add(Text("[fixed]")); positions.Add(Text("[normal]")); positions.Add(new(0, index / 9));
        }
        Section("monster disposition list", positions);
        Section("special dungeon info", new[] { Text("2026-11-05"), new PvfToken(0, 20) });
        return MonsterMockBattleCatalog.Parse(tokens, index => names[index]);
    }
}
