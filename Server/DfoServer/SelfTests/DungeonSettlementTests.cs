using static DfoServer.Testing.TestCheck;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using DfoServer.Testing;
using Microsoft.Data.Sqlite;
using PvfLib;

namespace DfoServer;

internal static partial class DungeonSettlementTests
{
    private static readonly Location Location = new(38, 1, 450, 234);
    private static readonly DungeonClearExperienceReward Reward = new(990, 10, 1000, 1, "fixture-clear", "fixture/clear-exp.tbl");

    public static void RunCore()
    {
        int before = TestCheck.Count;
        var role = new Character(1, 0, "Settle", 0, Location, Location);
        var (run, authority) = CreateRun(role);
        var unconfirmed = new DungeonClearFact(run.Snapshot.Identity, 3, [Guid.NewGuid()]);
        Check(run.ClearFact is null && run.PrepareSettlement(unconfirmed, new byte[] { 1 }, Reward).Outcome == DungeonRunOutcome.Rejected,
            "尚未清图时客户端报告不能创建结算资格");
        var lastDeath = run.PrepareConfirmedDeath(authority.Death(run.Snapshot.Identity, 20)).Pending!;
        Check(lastDeath.Result.ClearFact is not null && run.ClearFact is null,
            "待提交的末次死亡不提前公开清图事实");
        var cleared = run.ConfirmCommittedDeath(lastDeath);
        var fact = run.ClearFact!;
        Check(ReferenceEquals(cleared.ClearFact, fact) && fact.Identity == run.Snapshot.Identity,
            "提交确认后实例保留唯一冻结清图对象");
        Check(run.PrepareSettlement(fact with { }, new byte[] { 1 }, Reward).Outcome == DungeonRunOutcome.Rejected,
            "复制或伪造相同清图字段不能取得结算资格");
        byte[] report = [1, 2, 3];
        var prepared = run.PrepareSettlement(fact, report, Reward).Pending!;
        report[0] = 99;
        Check(prepared.ReportBody.SequenceEqual(new byte[] { 1, 2, 3 }) && prepared.EventId != Guid.Empty,
            "首份报告复制冻结，后续输入集合修改不污染稳定事件");
        Check(ReferenceEquals(run.PrepareSettlement(fact, new byte[] { 1, 2, 3 }, Reward).Pending, prepared),
            "提交前同报告重试复用原候选与事件ID");
        Check(run.PrepareSettlement(fact, report, Reward).Outcome == DungeonRunOutcome.Rejected &&
            run.PrepareSettlement(fact, new byte[] { 1, 2, 3 }, Reward with { RuleId = "changed" }).Outcome == DungeonRunOutcome.Rejected,
            "首份报告冻结后拒绝修改报告或奖励规则");
        DungeonRunIdentity identity = run.Snapshot.Identity;
        Check(run.HasPendingSettlement && run.Return(identity, Guid.NewGuid()).Outcome == DungeonRunOutcome.Rejected &&
            run.Snapshot.Identity == identity, "SQL结果不明期间不能返回并销毁原结算身份");
        var fake = new PreparedDungeonSettlement(fact, new byte[] { 1, 2, 3 }, Reward, prepared.MapId);
        Check(!run.IsPendingSettlement(fake) && run.ConfirmCommittedSettlement(fake) == DungeonRunOutcome.Rejected,
            "重新构造结算候选不能确认原实例提交");
        Check(run.ConfirmCommittedSettlement(prepared) == DungeonRunOutcome.Accepted && !run.HasPendingSettlement &&
            run.IsCommittedSettlement(prepared), "存储提交后才确认结算内存");
        var replay = run.PrepareSettlement(fact, new byte[] { 1, 2, 3 }, Reward);
        Check(replay.Outcome == DungeonRunOutcome.Duplicate && ReferenceEquals(replay.Pending, prepared) &&
            run.ConfirmCommittedSettlement(prepared) == DungeonRunOutcome.Duplicate,
            "已提交同报告返回原冻结候选，不能重复产生奖励事实");
        Check(run.PrepareSettlement(fact, new byte[] { 9 }, Reward).Outcome == DungeonRunOutcome.Rejected,
            "提交后修改报告仍拒绝");
        Check(run.Return(identity, Guid.NewGuid()).Outcome == DungeonRunOutcome.Accepted &&
            run.PrepareSettlement(fact, new byte[] { 1, 2, 3 }, Reward).Outcome == DungeonRunOutcome.Rejected,
            "返回后原清图epoch不能重新结算");
        Console.WriteLine($"PASS {TestCheck.Count - before} 通关报告冻结、实例资格与提交协调断言");
    }

    public static void RunResources(PvfArchive pvf)
    {
        int before = TestCheck.Count;
        string root = Path.Combine(Path.GetTempPath(), "S5A9-settlement-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "game.db");
        try
        {
            var growth = global::DfoServer.Testing.SelfTestCatalogs.Growth(pvf);
            var repository = new CharacterRepository(path);
            var owners = new AccountOwners();
            using var owner = owners.Acquire("settlement", () => { });
            var initial = repository.Create(owner, Fixture("ClearExp"));
            var (run, authority) = CreateRun(initial);
            run.AcceptConfirmedDeath(authority.Death(run.Snapshot.Identity, 20));
            var prepared = run.PrepareSettlement(run.ClearFact!, new byte[] { 1, 2, 3 }, Reward).Pending!;
            Execute("CREATE TRIGGER fail_clear BEFORE INSERT ON character_dungeon_settlement_receipts BEGIN SELECT RAISE(ABORT,'clear failure'); END");
            Reject<SqliteException>(() => repository.CommitDungeonSettlement(owner, initial, run, prepared, growth), "晚期receipt写入故障真实回滚");
            Check(SkillRepositoryTests.Equal(repository.List(owner).Single(), initial) && Receipts() == 0 &&
                run.IsPendingSettlement(prepared), "回滚不留下经验、SP、revision或receipt，原候选可恢复");
            Execute("DROP TRIGGER fail_clear");
            var first = repository.CommitDungeonSettlement(owner, initial, run, prepared, growth,
                grantLuckyStar: true, luckyStarLimit: 999);
            Check(!first.Duplicate && first.Reward == Reward && first.Character.Level == 2 &&
                first.Character.Learning.Experience == 1000 && first.Character.Revision == initial.Revision + 1 &&
                first.Character.Learning.Trees.Select(tree => tree.Sp).SequenceEqual(new ushort[] { 33, 38 }) &&
                first.Character.Learning.Trees.Select(tree => tree.Tp).SequenceEqual(new ushort[] { 4, 5 }) &&
                first.LuckyStarGained && first.LuckyStarTotal == 1 && first.Character.Inventory.AccountWallet.RentalPoints == 1,
                "基础加评分经验与推荐地图幸运星在同一事务提交");
            Check(first.Character.Inventory.Gold == initial.Inventory.Gold &&
                first.Character.Inventory.Items.SequenceEqual(initial.Inventory.Items) &&
                first.Character.ActiveMainQuests.SequenceEqual(initial.ActiveMainQuests) &&
                first.Character.Position == initial.Position && first.Character.Return == initial.Return &&
                first.Character.Skills.SequenceEqual(initial.Skills) &&
                Convert.ToInt64(Query("SELECT count(*) FROM character_dungeon_growth_receipts")) == 0,
                "结算保留任务、位置、技能和资产，不伪造击杀或翻牌奖励");
            // 模拟COMMIT后返回结果丢失：实例尚未确认，保留旧expected与同一候选恢复。
            var recovered = repository.CommitDungeonSettlement(owner, initial, run, prepared, growth);
            Check(recovered.Duplicate && !recovered.LuckyStarGained && recovered.LuckyStarTotal == 1 &&
                SkillRepositoryTests.Equal(recovered.Character, first.Character) && Receipts() == 1 &&
                run.IsPendingSettlement(prepared), "已提交结果不明重试查询真实receipt，不重复增加经验或SP");
            using (var writer = Open())
            using (var held = writer.BeginTransaction(deferred: false))
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var readOnly = repository.CommitDungeonSettlement(owner, initial, run, prepared, growth);
                Check(readOnly.Duplicate && watch.Elapsed < TimeSpan.FromSeconds(1), "精确恢复只读，不等待其他writer");
                held.Rollback();
            }
            Check(run.ConfirmCommittedSettlement(prepared) == DungeonRunOutcome.Accepted && Receipts() == 1,
                "确定持久结果之后才确认实例结算");
            Reject<InvalidDataException>(() => repository.CommitDungeonSettlement(owner, first.Character, run, prepared, growth),
                "已经确认的候选不能再次进入资产提交入口");
            Check(SkillRepositoryTests.Equal(new CharacterRepository(path).List(owner).Single(), first.Character), "重新打开仓储保留通关经验与去重记录");

            var stale = repository.Create(owner, Fixture("StaleClear"));
            var (staleRun, staleAuthority) = CreateRun(stale);
            staleRun.AcceptConfirmedDeath(staleAuthority.Death(staleRun.Snapshot.Identity, 20));
            var stalePrepared = staleRun.PrepareSettlement(staleRun.ClearFact!, new byte[] { 1 }, Reward).Pending!;
            var moved = repository.Save(owner, stale, stale.Position, stale.Return);
            Reject<InvalidOperationException>(() => repository.CommitDungeonSettlement(owner, stale, staleRun, stalePrepared, growth), "旧revision拒绝覆盖后续角色保存");
            Check(SkillRepositoryTests.Equal(repository.List(owner).Single(role => role.Id == stale.Id), moved) && Receipts() == 1,
                "revision冲突不留下通关奖励");
            using var outsider = owners.Acquire("clear-other", () => { });
            Reject<InvalidDataException>(() => repository.CommitDungeonSettlement(outsider, stale, staleRun, stalePrepared, growth), "其他账号不能结算本角色");
            Reject<InvalidDataException>(() => repository.CommitDungeonSettlement(owner, moved, staleRun,
                new(staleRun.ClearFact!, new byte[] { 1 }, Reward, stalePrepared.MapId), growth), "复制准备对象不能获得写库资格");
            Execute($"UPDATE character_skill_points SET sp=sp+1 WHERE character_id={stale.Id} AND context=0");
            Reject<InvalidOperationException>(() => repository.CommitDungeonSettlement(owner, moved, staleRun, stalePrepared, growth), "相同revision下学习前提变化也拒绝");

            foreach (var unsupported in new[] { Fixture("GrowClear") with { GrowType = 15 }, Fixture("OldClear") with { InitialLearning = CharacterLearningState.Uninitialized },
                Fixture("SpClear") with { InitialLearning = Learning(0, ushort.MaxValue) } })
            {
                var role = repository.Create(owner, unsupported);
                var (deniedRun, deniedAuthority) = CreateRun(role);
                deniedRun.AcceptConfirmedDeath(deniedAuthority.Death(deniedRun.Snapshot.Identity, 20));
                var denied = deniedRun.PrepareSettlement(deniedRun.ClearFact!, new byte[] { 1 }, Reward).Pending!;
                Reject<InvalidDataException>(() => repository.CommitDungeonSettlement(owner, role, deniedRun, denied, growth),
                    "非法成长快照和SP溢出门禁保留，满级合法快照由封顶规则接管");
                Check(SkillRepositoryTests.Equal(repository.List(owner).Single(saved => saved.Id == role.Id), role) && Receipts() == 1,
                    "成长拒绝不截断奖励、不改变资产或新增receipt");
            }
            foreach (byte job in new byte[] { 9, 10 })
            {
                var role = repository.Create(owner, Fixture("Clear" + job) with { Job = job });
                var (specialRun, specialAuthority) = CreateRun(role);
                specialRun.AcceptConfirmedDeath(specialAuthority.Death(specialRun.Snapshot.Identity, 20));
                var specialPrepared = specialRun.PrepareSettlement(specialRun.ClearFact!, new byte[] { 1 }, Reward).Pending!;
                var committed = repository.CommitDungeonSettlement(owner, role, specialRun, specialPrepared, growth).Character;
                Check(committed.Learning.Experience == role.Learning.Experience + Reward.TotalExperience,
                    "特殊职业通关经验走既有原子成长事务 " + job);
            }
            Execute($"UPDATE character_dungeon_state SET last_attempt=2 WHERE character_id={stale.Id}");
            Reject<InvalidOperationException>(() => repository.CommitDungeonSettlement(owner, moved, staleRun, stalePrepared, growth), "旧副本尝试不能提交奖励");
            using var replacement = owners.Acquire(owner.Account, () => { });
            Reject<InvalidOperationException>(() => repository.CommitDungeonSettlement(owner, moved, staleRun, stalePrepared, growth), "旧account generation不能写入结算");
            Check(Query("PRAGMA integrity_check") is "ok" && Convert.ToInt64(Query("SELECT count(*) FROM pragma_foreign_key_check")) == 0,
                "全部成功与失败路径后数据库和外键完整");
        }
        finally { Directory.Delete(root, true); }
        Console.WriteLine($"PASS {TestCheck.Count - before} 通关经验、双树SP、原子回滚、receipt恢复及owner断言");

        SqliteConnection Open()
        {
            var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, ForeignKeys = true }.ToString());
            db.Open();
            return db;
        }
        object? Query(string sql)
        {
            using var db = Open();
            using var command = db.CreateCommand();
            command.CommandText = sql;
            return command.ExecuteScalar();
        }
        void Execute(string sql) => _ = Query(sql);
        long Receipts() => Convert.ToInt64(Query("SELECT count(*) FROM character_dungeon_settlement_receipts"));
    }

    private static NewCharacter Fixture(string name) => new(name, Location, Location)
    {
        InitialLearning = Learning(0), ActiveMainQuests = [new(0, 3145, 1)],
        DungeonState = new(256, 1, 0, 0, 0)
    };
    private static CharacterLearningState Learning(ulong experience, ushort firstSp = 3) =>
        new(experience, Array.AsReadOnly(new[] { new CharacterSkillTree(0, firstSp, 4, []), new CharacterSkillTree(1, 8, 5, []) }));

    private static (SinglePlayerDungeonRun Run, FixtureAuthority Authority) CreateRun(Character character)
    {
        var map = new DungeonMap("boss", new(0, 0), 76126, "map/settlement-fixture.map");
        var maze = new DungeonMaze(0, 1, 1, "fixture-only", [map.Position], [map.Position], [map], []);
        var entry = new DungeonEntryPlan(character.Id, character.Revision, 3, 3145, 0, 0, maze, map, map);
        var source = new DungeonRoomMonster(1, Array.AsReadOnly(new[] { 1, 0, 3, 100, 100, 0, 1, 1 }),
            "[fixed]", "[normal]", "monster/settlement-fixture.mob");
        var room = new DungeonRoomDefinition(map, [source], [], []);
        var plan = new DungeonMonsterPlan(room, [new(0, 20, 1, 3, source)]);
        var authority = new FixtureAuthority();
        if (!SinglePlayerDungeonRun.TryCreate(entry, character.DungeonState?.LastAttempt ?? 1, 1, new([plan], []), authority, out var run, out _))
            throw new InvalidOperationException("通关合成实例未建立");
        run!.CompleteLoading(run.Snapshot.Identity, Guid.NewGuid());
        return (run, authority);
    }
    private sealed class FixtureAuthority : DungeonCombatAuthority
    {
        public ConfirmedDungeonMonsterDeath Death(DungeonRunIdentity identity, ushort key)
            => ConfirmDeath(identity, Guid.NewGuid(), key, 1);
    }
    private static void Check(bool value, string name) => TestCheck.That(value, name);
}
