using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Strongway2019;
using DfoServer.GameWorld.Dungeons;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class Strongway2019StatisticsTests
{
    public static void RunCore()
    {
        int before = Count;
        NormalBestAndCharacterScope();
        MatchingWinsAndBest();
        AuthorityAndRollback();
        RunReceiptIntegrity();
        MigrationAndSchema();
        Console.WriteLine($"PASS {Count - before} 首期强者之路角色历史、权威耗时、独立奖励额度与统计回执断言");
    }

    static void NormalBestAndCharacterScope()
    {
        using var f = new Fixture();
        That(f.Read(f.First.Id) == new Strongway2019Statistics(0, 0, 0, 0), "新角色历史为空，不补造胜场或时间");
        var simple = f.Clear(f.First.Slot, 5359, 12_340);
        That(f.Repository.CommitStrongway2019Statistics(f.Owner, simple) is { Recorded: true, Duplicate: false } &&
            f.Read(f.First.Id) == new Strongway2019Statistics(0, 0, 0, 12_340), "普通真实终点记录服务器耗时");
        f.Clock.Advance(50_000);
        That(simple.Run.Strongway2019ElapsedMilliseconds == 12_340 &&
            new CharacterRepository(f.Path).CommitStrongway2019Statistics(f.Owner, simple) is { Recorded: true, Duplicate: true },
            "真实Clear冻结时间，重建仓储重放不随后续时钟改变");
        foreach (var (dungeon, time, bestMode, bestTime) in new[]
        {
            (5359, 20_000, 0, 12_340), (5359, 10_000, 0, 10_000),
            (5363, 30_000, 2, 30_000), (5359, 1_000, 2, 30_000), (5363, 25_000, 2, 25_000)
        })
        {
            var plan = f.Clear(f.First.Slot, checked((uint)dungeon), time);
            f.Repository.CommitStrongway2019Statistics(f.Owner, plan);
            That(f.Read(f.First.Id) == new Strongway2019Statistics(0, 0, checked((uint)bestMode), checked((uint)bestTime)),
                "较高已开放难度优先，同难度最短；任意通关次数不受奖励日周额度限制");
        }
        That(f.Read(f.Second.Id) == new Strongway2019Statistics(0, 0, 0, 0) &&
            f.Scalar("SELECT count(*) FROM account_strongway2019_rewards") is 0L,
            "同账号其他角色历史隔离，记录统计不领取邮件或奖励额度");
        var second = f.Clear(f.Second.Slot, 5359, 7_000);
        f.Repository.CommitStrongway2019Statistics(f.Owner, second);
        That(f.Read(f.Second.Id).NormalBestElapsedMilliseconds == 7_000 && f.Read(f.First.Id).NormalBestElapsedMilliseconds == 25_000,
            "第二角色保存自己的最佳记录");
    }

    static void MatchingWinsAndBest()
    {
        using var f = new Fixture();
        var exit = f.Match(f.First.Slot, 0, exit: true, loading: true);
        That(exit.Run.Phase == SinglePlayerDungeonPhase.Loading && exit.Fact is null &&
            f.Repository.CommitStrongway2019Statistics(f.Owner, exit) is { Recorded: true, Duplicate: false } &&
            f.Read(f.First.Id) == new Strongway2019Statistics(1, 0, 0, 0),
            "开场对侧退出时Loading胜者计胜场，没有真实Clear便无最佳时间");
        That(f.Repository.CommitStrongway2019Statistics(f.Owner, exit) is { Duplicate: true } && f.Read(f.First.Id).MatchingWins == 1,
            "同一角色同一实例重复终局不增胜场");
        var clear = f.Match(f.First.Slot, 23_456);
        f.Repository.CommitStrongway2019Statistics(f.Owner, clear);
        f.Clock.Advance(90_000);
        That(clear.Terminal!.CombatElapsedMilliseconds == 23_456 &&
            f.Read(f.First.Id) == new Strongway2019Statistics(2, 23_456, 0, 0),
            "真实匹配Clear以双队开场后终局冻结耗时更新最佳，领域存储不混入客户端五秒补偿");
        var slow = f.Match(f.First.Slot, 30_000);
        f.Repository.CommitStrongway2019Statistics(f.Owner, slow);
        var fast = f.Match(f.First.Slot, 20_000);
        f.Repository.CommitStrongway2019Statistics(f.Owner, fast);
        var lost = f.Match(f.First.Slot, 1_000, victory: false);
        That(f.Repository.CommitStrongway2019Statistics(f.Owner, lost) is { Recorded: false, Duplicate: false } &&
            f.Read(f.First.Id) == new Strongway2019Statistics(4, 20_000, 0, 0), "真实失败不计胜场，匹配最佳只改善不退步");
    }

    static void AuthorityAndRollback()
    {
        using var f = new Fixture();
        var plan = f.Clear(f.First.Slot, 5359, 5_000);
        foreach (var invalid in new[] { plan with { Fact = plan.Fact! with { } }, plan with { Expected = f.Second },
            plan with { Identity = plan.Identity with { Epoch = plan.Identity.Epoch + 1 } } })
            Reject<InvalidDataException>(() => f.Repository.CommitStrongway2019Statistics(f.Owner, invalid), "复制终点、错误角色或epoch不能伪造历史");
        using (var foreign = f.Owners.Acquire("statistics-foreign", () => { }))
        {
            Reject<InvalidOperationException>(() => f.Repository.CommitStrongway2019Statistics(foreign, plan), "跨账号不能写角色统计");
            Reject<InvalidOperationException>(() => f.Repository.ReadStrongway2019Statistics(foreign, f.First.Id), "跨账号不能读角色历史");
        }
        f.Repository.Save(f.Owner, plan.Expected, new(38, 0, 450, 234), null);
        Reject<InvalidOperationException>(() => f.Repository.CommitStrongway2019Statistics(f.Owner, plan), "旧角色revision不能首次记录统计");
        var newer = f.Clear(f.First.Slot, 5359, 4_000);
        Reject<InvalidOperationException>(() => f.Repository.CommitStrongway2019Statistics(f.Owner, plan with { Expected = newer.Expected }),
            "新入场尝试后不能补录旧Run为首次统计");
        f.Sql("CREATE TRIGGER strongway_statistics_fail AFTER INSERT ON character_strongway2019_statistics BEGIN SELECT RAISE(ABORT,'statistics rollback'); END");
        Reject<SqliteException>(() => f.Repository.CommitStrongway2019Statistics(f.Owner, newer), "统计插入晚期失败整事务回滚");
        f.Sql("DROP TRIGGER strongway_statistics_fail");
        That(f.Scalar("SELECT count(*) FROM character_strongway2019_statistics") is 0L && f.Read(f.First.Id).NormalBestElapsedMilliseconds == 0,
            "SQL失败不留下统计回执或最佳时间");
        var stale = f.Owner;
        f.Owner = f.Owners.Acquire(Fixture.Account, () => { });
        Reject<InvalidOperationException>(() => f.Repository.CommitStrongway2019Statistics(stale, newer), "旧owner不能记录历史");
        f.Repository.CommitStrongway2019Statistics(f.Owner, newer);
        That(newer.Run.Return(newer.Run.Scene.Identity, Guid.NewGuid()).Outcome == DungeonRunOutcome.Accepted &&
            new CharacterRepository(f.Path).CommitStrongway2019Statistics(f.Owner, newer) is { Duplicate: true },
            "已提交原回执在返城epoch变化后仍可重放");
        var match = f.Match(f.Second.Slot, 2_000);
        Reject<InvalidDataException>(() => f.Repository.CommitStrongway2019Statistics(f.Owner, match with { Terminal = match.Terminal! with { } }),
            "值相同的复制终局不能冒原比赛权威事实");
        Reject<InvalidDataException>(() => f.Repository.CommitStrongway2019Statistics(f.Owner, match with { Competition = null }),
            "匹配个人Clear不能代替原比赛胜队鉴权");
        var unsupported = f.Clear(f.Second.Slot, 5364, 1_000);
        Reject<InvalidDataException>(() => f.Repository.CommitStrongway2019Statistics(f.Owner, unsupported), "未开放5364不能生成首期最佳模式");
    }

    static void MigrationAndSchema()
    {
        using var f = new Fixture();
        string before = JsonSerializer.Serialize(f.Repository.LoadCharacter(f.Owner, f.First.Slot));
        f.Sql("DROP TABLE character_strongway2019_statistics; PRAGMA user_version=177");
        var upgraded = new CharacterRepository(f.Path);
        That(f.Scalar("SELECT count(*) FROM character_strongway2019_statistics") is 0L &&
            JsonSerializer.Serialize(upgraded.LoadCharacter(f.Owner, f.First.Slot)) == before,
            "旧库迁移新建空角色历史，保留完整已有角色");
        f.Sql("ALTER TABLE character_strongway2019_statistics ADD COLUMN unknown INTEGER");
        Reject<InvalidDataException>(() => new CharacterRepository(f.Path), "未知统计表结构不能静默接受或删表重建");
        That(f.Scalar("SELECT count(*) FROM pragma_table_info('character_strongway2019_statistics') WHERE name='unknown'") is 1L,
            "拒绝schema后原库结构保留");
    }

    public static void RunReceiptIntegrity()
    {
        using var f = new Fixture();
        var plan = f.Clear(f.First.Slot, 5359, 5_000);
        f.Repository.CommitStrongway2019Statistics(f.Owner, plan);
        foreach (var (alter, restore) in new[]
        {
            ("elapsed_ms=5001", "elapsed_ms=5000"),
            ("task_attempt=task_attempt+1", "task_attempt=task_attempt-1"),
            ("run_epoch=run_epoch+1", "run_epoch=run_epoch-1"),
            ("dungeon_id=5363,mode=2", "dungeon_id=5359,mode=0")
        })
        {
            f.Sql("UPDATE character_strongway2019_statistics SET " + alter);
            Reject<InvalidDataException>(() => f.Repository.CommitStrongway2019Statistics(f.Owner, plan),
                "完整fact_json未变也不能重放与冻结耗时、尝试、epoch或模式列不一致的永久回执");
            f.Sql("UPDATE character_strongway2019_statistics SET " + restore);
        }
        That(f.Repository.CommitStrongway2019Statistics(f.Owner, plan) is { Recorded: true, Duplicate: true } &&
            f.Read(f.First.Id).NormalBestElapsedMilliseconds == 5_000, "恢复原冻结回执后只回放既有记录");
    }

    sealed class Fixture : IDisposable
    {
        internal const string Account = "strongway-statistics";
        readonly string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "S5A9-strongway-statistics-" + Guid.NewGuid().ToString("N"));
        public string Path { get; }
        public CharacterRepository Repository { get; }
        public AccountOwners Owners { get; } = new();
        public AccountOwners.Lease Owner { get; set; }
        public Character First { get; }
        public Character Second { get; }
        public Clock Clock { get; } = new();
        public Fixture()
        {
            Directory.CreateDirectory(directory);
            Path = System.IO.Path.Combine(directory, "game.db");
            Repository = new(Path);
            Owner = Owners.Acquire(Account, () => { });
            First = Repository.Create(Owner, new("StatsFirst", new(38, 1, 450, 234), null) { Level = 95, DungeonState = new(256, 0, 0, 0, 0) });
            Second = Repository.Create(Owner, new("StatsSecond", new(38, 1, 450, 234), null) { Level = 95, DungeonState = new(256, 0, 0, 0, 0) });
        }
        public Strongway2019Statistics Read(ushort character) => Repository.ReadStrongway2019Statistics(Owner, character);
        public Strongway2019StatisticsPlan Clear(ushort slot, uint dungeon, long elapsed)
        {
            var (plan, authority, monster) = Start(slot, dungeon);
            plan.Run.CompleteLoading(plan.Identity, Guid.NewGuid());
            Clock.Advance(elapsed);
            var death = plan.Run.PrepareConfirmedDeath(authority.Death(plan.Run.Scene.Identity, monster)).Pending!;
            That(plan.Run.ConfirmCommittedDeath(death).ClearFact is not null, "统计用例由真实Run确认终点死亡");
            return plan with { Fact = plan.Run.ClearFact, Identity = plan.Run.ClearFact!.Identity };
        }
        public Strongway2019StatisticsPlan Match(ushort slot, long elapsed, bool victory = true, bool exit = false, bool loading = false)
        {
            var (plan, authority, monster) = Start(slot, 5360);
            var fillers = Enumerable.Range(100, 7).Select(id => Strongway2019CompetitionTests.CreateRewardRun(checked((ushort)id), Guid.NewGuid(), 1)).ToArray();
            var competition = new Strongway2019Competition(Guid.NewGuid(), [plan.Run, .. fillers.Take(3)], fillers.Skip(3).ToArray(),
                Strongway2019CompetitionTests.Catalog(30, 10_000), TimeSpan.FromSeconds(600), Clock);
            if (!loading)
            {
                plan.Run.CompleteLoading(plan.Identity, Guid.NewGuid());
                foreach (int phase in new[] { 3, 4 })
                {
                    competition.ConfirmOpening(plan.Run, plan.Run.Scene.Identity, phase);
                    competition.ConfirmOpening(fillers[3], fillers[3].Scene.Identity, phase);
                }
                Clock.Advance(elapsed);
            }
            if (!victory) competition.Exit(plan.Run, plan.Run.Scene.Identity);
            else if (exit) competition.Exit(fillers[3], fillers[3].Scene.Identity);
            else
            {
                var death = plan.Run.PrepareConfirmedDeath(authority.Death(plan.Run.Scene.Identity, monster)).Pending!;
                plan.Run.ConfirmCommittedDeath(death);
                competition.ObserveClear(plan.Run, plan.Run.Scene.Identity, plan.Run.ClearFact!);
                plan = plan with { Fact = plan.Run.ClearFact, Identity = plan.Run.ClearFact!.Identity };
            }
            return plan with { Identity = plan.Run.Scene.Identity, Competition = competition, Terminal = competition.Snapshot.Terminal };
        }
        (Strongway2019StatisticsPlan Plan, Authority Authority, ushort Monster) Start(ushort slot, uint dungeon)
        {
            var character = Repository.LoadCharacter(Owner, slot);
            var map = new DungeonMap("boss", new(0, 0), 1, "map/strongway-statistics-fixture.map");
            var maze = new DungeonMaze(0, 1, 1, "strongway-statistics-fixture", [map.Position], [map.Position], [map], []);
            var entry = new DungeonEntryPlan(character.Id, character.Revision + 1, dungeon, 0, byte.MaxValue, 0, maze, map, map)
                { DisableMonsterAndClearExperience = true, DisableFatigueConsumption = true, Strongway2019TimeLimitSeconds = dungeon == 5360 ? null : 3600 };
            var monster = new DungeonRoomMonster(1, [1, 0, 95, 100, 100, 0, 1, 1], "[fixed]", "[boss]", "monster/strongway-fixture.mob");
            var monsters = DungeonMonsterPlan.FromRoom(new(map, [monster], [], []), character.Id, checked((ushort)character.DungeonState!.NextObjectKey));
            var authority = new Authority();
            That(SinglePlayerDungeonRun.TryCreate(entry, character.DungeonState.LastAttempt + 1, 1, new([monsters], []), authority,
                out var run, out _, Clock), "统计沿真实副本Run和已持久入场尝试");
            character = Repository.ReserveDungeonAttempt(Owner, character, character.DungeonState.NextObjectKey + 100);
            return (new(character, run!, null), authority, monsters.Monsters.Single().ObjectKey);
        }
        public void Sql(string sql) { using var db = Open(); using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
        public object? Scalar(string sql) { using var db = Open(); using var command = db.CreateCommand(); command.CommandText = sql; return command.ExecuteScalar(); }
        SqliteConnection Open() { var db = new SqliteConnection("Data Source=" + Path + ";Pooling=False"); db.Open(); return db; }
        public void Dispose() { Owner.Dispose(); TestFiles.DeleteDirectory(directory, true); }
    }
    sealed class Authority : DungeonCombatAuthority
    {
        public ConfirmedDungeonMonsterDeath Death(DungeonRunIdentity identity, ushort key) => ConfirmDeath(identity, Guid.NewGuid(), key, 1);
    }
    sealed class Clock : TimeProvider
    {
        long milliseconds;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Interlocked.Read(ref milliseconds);
        public void Advance(long value) => Interlocked.Add(ref milliseconds, value);
    }
}
