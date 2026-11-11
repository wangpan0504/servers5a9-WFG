using System.Buffers.Binary;
using System.IO.Compression;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Events;
using DfoServer.Game.PcRoom;
using DfoServer.Game.Settings;
using DfoServer.GameWorld.Items;
using DfoServer.Network.Builders;
using DfoServer.Persistence;
using DfoServer.Testing;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class DungeonSettlementTests
{
    public static void RunEndlessAdventureCore()
    {
        int before = Count;
        That(EndlessAdventurePolicy.Award(9, false, 0) == 9 && EndlessAdventurePolicy.Award(9, true, 0) == 18,
            "无尽大冒险按应耗发币，燃烧只乘一次二");
        That(EndlessAdventurePolicy.Award(9, true, 199) == 1 && EndlessAdventurePolicy.Award(uint.MaxValue, true, 0) == 200 &&
            EndlessAdventurePolicy.Award(1, false, 200) == 0 && EndlessAdventurePolicy.Award(9, true, 0, 3) == 3,
            "日额度和钱包容量独立裁剪，不溢出或浪费未到账额度");
        var boundary = DateTimeOffset.Parse("2026-11-03T06:00:00+08:00");
        That(EndlessAdventurePolicy.Day(boundary.AddTicks(-1)) + 1 == EndlessAdventurePolicy.Day(boundary),
            "活动额度按北京时间06点跨日");
        var body = EndlessAdventurePackets.Progress(200);
        That(body.Length == 152 && body.AsSpan(0, 8).SequenceEqual(new byte[] { 209, 8, 0, 0, 200, 0, 0, 0 }) &&
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(20)) == 128 &&
            body.AsSpan(8, 12).IndexOfAnyExcept((byte)0) == -1 && body.AsSpan(24).IndexOfAnyExcept((byte)0) == -1,
            "V31的1103进度保留五个DWORD及128B数据串，不套用S5A3的1100编号");
        Reject<InvalidDataException>(() => EndlessAdventurePackets.Progress(201), "面板进度不得越过原生上限");
        var calendar = SeriaLuckPackets.EventInfoCompressed([266], [266, 2257], [EndlessAdventurePackets.Page()]);
        using var compressed = new MemoryStream(calendar);
        using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
        using var plain = new MemoryStream();
        zlib.CopyTo(plain);
        string calendarText = System.Text.Encoding.UTF8.GetString(plain.ToArray());
        That(calendar.Length > 0 && calendarText.Contains("[show_item_index] 1 490011740", StringComparison.Ordinal),
            "完整2257原生路由页面登记账号活动道具，其他活动仍同时进入108数值表");
        Reject<InvalidDataException>(() => SeriaLuckPackets.EventInfoCompressed([], [],
            [EndlessAdventurePackets.Page() with { ShowItemIds = [490011740, 490011740] }]),
            "通用活动道具登记拒绝重复ID");
        That(ServerEventService.Definition(2257) is { DefaultState: "默认开启", SupportsSchedule: true }, "活动默认开启且沿既有GM目录与时段控制");
        var state = CharacterFatigueState.NewOrdinary;
        That(state.ConsumeOrdinaryRoom(FatigueAccelerationRules.Cost(1, 100)) is { Used: 2, Remaining: 154 },
            "燃烧房间真实扣两点，经验比例不作为费用百分比");
        That((state with { Used = 155, Remaining = 1 }).ConsumeOrdinaryRoom(2) is { Used: 156, Remaining: 0 },
            "最后一点夹到零，不能扣负数或超过已用上限");
        That(FatigueAccelerationRules.Cost(8, 100) == 16 && FatigueAccelerationRules.Cost(8, 0) == 8 &&
            FatigueAccelerationRules.Cost(8, 100, 9) == 9, "入场与地区费用共用冻结燃烧倍率，末池裁剪");
        var clock = new FormalClearClock(boundary);
        var debug = new GameDebugSettings();
        var coin = new ItemTemplate(490011740, ItemKind.Material, 0, 1, 0, int.MaxValue, 0, 1, 1, "[account]", ushort.MaxValue, 0, "能量硬币")
            { AccountMaterial = new(int.MaxValue) };
        var policy = new EndlessAdventurePolicy(coin, debug, clock);
        var role = new Character(1, 0, "EndlessCore", 0, Location, Location);
        DungeonEntryPlan Configure(DungeonEntryPlan entry) => entry with { QuestId = 0, ActiveQuestSlot = byte.MaxValue,
            RouteQuestId = 0, RecommendedQuestItems = true };
        foreach (bool infinite in new[] { false, true })
        foreach (bool burning in new[] { false, true })
        {
            debug.SetInfiniteFatigue("endless", infinite);
            var (run, _) = CreateRun(role, Configure, accelerationPercent: burning ? 100 : 0);
            var reward = policy.Prepare(run, true, true);
            That(run.Snapshot.ConsumedFatigue == 1 && reward?.Amount == (burning ? 2 : 1),
                "无限疲劳不改变加载应耗，燃烧与有限模式同额产币");
            That(policy.Prepare(run, false, true) is null && policy.Prepare(run, true, false) is null,
                "非推荐及关闭不新建奖励计划");
        }
        foreach (var excluded in new Func<DungeonEntryPlan, DungeonEntryPlan>[]
        { e => Configure(e) with { IsTutorial = true }, e => Configure(e) with { IsPractice = true },
            e => Configure(e) with { DisableFatigueConsumption = true }, e => Configure(e) with { DisableClearReward = true } })
            That(policy.Prepare(CreateRun(role, excluded).Run, true, true) is null, "免疲劳及特殊结算不冒普通活动奖励");
        Console.WriteLine($"PASS endless adventure rules ({Count - before} checks)");
    }

    public static void RunEndlessAdventureResources(TcpTestResources resources)
    {
        int before = Count;
        var coin = resources.ItemResources.Items.Get(EndlessAdventurePolicy.CoinId);
        That(coin is { Kind: ItemKind.Material, AccountMaterial: not null } && coin.AttachType.Trim('`', ' ', '\r', '\n') == "[account]",
            "当前PVF无参数账号共通标签进入原账号材料系统");
        foreach (int id in Enumerable.Range(490011735, 5))
            That(resources.ItemResources.Shops.Allows(400000023, 400000008, id), "五件原生疲劳商店商品保持PVF目录");
        var clock = new FormalClearClock(DateTimeOffset.Parse("2026-11-03T07:00:00+08:00"));
        var policy = new EndlessAdventurePolicy(coin, new(), clock);
        That(policy.Available, "本服原有忽略商城绝对日期政策兼容活动旧日期");
        var root = Path.Combine(Path.GetTempPath(), "endless-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "game.db");
        try
        {
            var repository = new CharacterRepository(path) { EndlessAdventure = policy };
            var owners = new AccountOwners();
            using var owner = owners.Acquire("endless-account", () => { });
            var growth = SelfTestCatalogs.Growth(resources.Archive);
            var first = repository.Create(owner, Fixture("EndlessA"));
            var second = repository.Create(owner, Fixture("EndlessB"));
            DungeonEntryPlan Configure(DungeonEntryPlan e) => e with { QuestId = 0, ActiveQuestSlot = byte.MaxValue,
                RouteQuestId = 0, RecommendedQuestItems = true };
            var (run, authority) = CreateRun(first, Configure, load: false, accelerationPercent: 100);
            var spent = repository.ConsumeDungeonRoomFatigue(owner, first, run);
            That(spent.Fatigue!.Remaining == first.Fatigue!.Remaining - 2 && spent.Revision == first.Revision + 1,
                "真实SQLite普通房间燃烧扣两点并只推进一次角色版本");
            That(repository.ConsumeDungeonRoomFatigue(owner, first, run).Revision == spent.Revision,
                "同房间结果丢失按原准备恢复，不能重复扣点");
            run.CompleteLoading(run.Snapshot.Identity, Guid.NewGuid());
            run.AcceptConfirmedDeath(authority.Death(run.Snapshot.Identity, 20));
            var prepared = run.PrepareSettlement(run.ClearFact!, [1, 2, 3], Reward).Pending!;
            prepared.FreezeEndlessAdventure(policy.Prepare(run, true, true));
            int day = EndlessAdventurePolicy.Day(clock.Now);
            Execute($"INSERT INTO account_endless_adventure_days VALUES('endless-account',{day},199)");
            Execute("CREATE TRIGGER endless_failure BEFORE UPDATE ON account_endless_adventure_days BEGIN SELECT RAISE(ABORT,'endless rollback'); END");
            Reject<SqliteException>(() => repository.CommitDungeonSettlement(owner, spent, run, prepared, growth),
                "实际材料写后日额度故障回滚整个通关事务");
            var rolledBack = repository.LoadCharacter(owner, first.Slot);
            That(rolledBack.Revision == spent.Revision && rolledBack.Learning.Experience == spent.Learning.Experience &&
                rolledBack.Inventory.AccountWallet.Material(490011740).Balance == 0 && repository.ReadEndlessAdventure(owner, day) == 199 &&
                Query("SELECT count(*) FROM character_dungeon_settlement_receipts") == 0,
                "失败保全经验、钱包、角色版本、额度及receipt，同Run可重试");
            Execute("DROP TRIGGER endless_failure");
            var committed = repository.CommitDungeonSettlement(owner, spent, run, prepared, growth);
            That(committed.EndlessAdventureAmount == 1 && committed.Character.Inventory.AccountWallet.Material(490011740).Balance == 1 &&
                committed.Character.Revision == spent.Revision + 1 && repository.ReadEndlessAdventure(owner, day) == 200,
                "199到200只到账一枚，成长与硬币共用原唯一角色CAS");
            var recovered = repository.CommitDungeonSettlement(owner, spent, run, prepared, growth);
            That(recovered.Duplicate && recovered.Character.Inventory.AccountWallet.Material(490011740).Balance == 1 &&
                repository.ReadEndlessAdventure(owner, day) == 200, "提交回执恢复不再次发币或累计额度");
            var cold = new CharacterRepository(path) { EndlessAdventure = policy };
            That(cold.LoadCharacter(owner, second.Slot).Inventory.AccountWallet.Material(490011740).Balance == 1 &&
                cold.ReadEndlessAdventure(owner, day) == 200, "新仓储与换角继承同一账号余额和额度");
            clock.Now = clock.Now.AddDays(1);
            That(cold.ReadEndlessAdventure(owner, EndlessAdventurePolicy.Day(clock.Now)) == 0 && cold.ReadEndlessAdventure(owner, day) == 200,
                "次日额度只读为零，历史日不回退或清空钱包");
            using var other = owners.Acquire("endless-other", () => { });
            That(cold.ReadEndlessAdventure(other, day) == 0, "不同账号不共享产币额度");
            using var replacement = owners.Acquire(owner.Account, () => { });
            Reject<InvalidOperationException>(() => cold.ReadEndlessAdventure(owner, day), "旧generation不能读取活动事实");

            long Query(string sql) { using var db = new SqliteConnection("Data Source=" + path); db.Open();
                using var command = db.CreateCommand(); command.CommandText = sql; return Convert.ToInt64(command.ExecuteScalar()); }
            void Execute(string sql) => Query(sql);
        }
        finally { SqliteDatabaseBootstrap.ReleaseRuntimePool(); SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
        Console.WriteLine($"PASS endless adventure storage ({Count - before} checks)");
    }
}
