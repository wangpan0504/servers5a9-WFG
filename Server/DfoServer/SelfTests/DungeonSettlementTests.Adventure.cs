using System.Buffers.Binary;
using System.Globalization;
using DfoServer.Game.Accounts;
using DfoServer.Game.Adventure;
using DfoServer.Game.Auth;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Adventure;
using DfoServer.GameWorld.Characters;
using DfoServer.Persistence;
using DfoServer.Testing;
using Microsoft.Data.Sqlite;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class DungeonSettlementTests
{
    // 国服95模型：账号经验由仓储在结算事务内按角色有效里程增量计入，调用方不能传入或伪造计划。
    // 只有40级及以上角色提供经验：满级角色里程从95级入口线继续推进，未满级角色按100%计入且不产生结晶/胶囊溢出。
    public static void RunAdventureResources(PvfArchive pvf)
    {
        int before = TestCheck.Count;
        string root = Path.Combine(Path.GetTempPath(), "S5A9-settlement-adventure-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "game.db");
        try
        {
            var growth = global::DfoServer.Testing.SelfTestCatalogs.Growth(pvf);
            var adventure = AdventureCoreTests.SyntheticCatalog();
            var now = new DateTimeOffset(2026, 10, 11, 0, 0, 0, TimeSpan.Zero);
            var repository = new CharacterRepository(path, null, adventure, growth);
            ulong cap = growth.GetLevelStartExperience(CharacterGrowthCatalog.MaximumSupportedLevel);
            // account_adventures 以外键引用 accounts，先按真实登录路径建账号行再初始化冒险团。
            new AuthenticationService(new AuthenticationRepository(path)).Create("advsettle", "fixture-password");
            var owners = new AccountOwners();
            using var owner = owners.Acquire("advsettle", () => { });
            repository.InitializeAdventure(owner, adventure, now);
            var role = repository.Create(owner, MaxLevelFixture(growth, "MaxAdv"));
            var expected = AdventureProgression.Advance(AdventureState.Initial("advsettle", now),
                Reward.TotalExperience * adventure.ExperienceRate, Reward.TotalExperience, adventure);
            var (run, authority) = CreateRun(role);
            run.AcceptConfirmedDeath(authority.Death(run.Snapshot.Identity, 20));
            var prepared = run.PrepareSettlement(run.ClearFact!, new byte[] { 1, 2, 3 }, Reward).Pending!;

            Execute("CREATE TRIGGER fail_adventure BEFORE UPDATE OF level ON account_adventures BEGIN SELECT RAISE(ABORT,'adventure failure'); END");
            Reject<SqliteException>(() => repository.CommitDungeonSettlement(owner, role, run, prepared, growth),
                "冒险团等级写入的晚期故障回滚已写入的余额、角色成长与receipt");
            Execute("DROP TRIGGER fail_adventure");
            Check(SkillRepositoryTests.Equal(repository.List(owner).Single(), role) && Receipts() == 0 &&
                AdventureRow("revision") is 0L && AdventureRow("level") is 1L &&
                Position(role.Id) == checked((long)cap) && run.IsPendingSettlement(prepared),
                "晚期失败不留下半份账号进度或角色里程，原候选仍可重试");

            var committed = repository.CommitDungeonSettlement(owner, role, run, prepared, growth);
            Check(!committed.Duplicate && committed.Adventure is { } progress &&
                progress.State.Level == expected.Level && progress.State.Experience == expected.Experience &&
                progress.State.Remainder == expected.Remainder && progress.State.ExcludedExperience == expected.ExcludedExperience &&
                progress.State.CapsuleExperience == expected.CapsuleExperience &&
                progress.State.BraveExperience == expected.BraveExperience &&
                progress.State.Points.SequenceEqual(expected.Points) && progress.State.Revision == 1 &&
                progress.MaximumLevelExperience == Reward.TotalExperience && committed.Character.Level == 95,
                "满级通关的整单原始经验在同一事务按倍率推进账号等级、勇者结晶与胶囊");
            // 满级经验条钉在95级入口线：整单奖励不再推进角色经验，全部等额进入熟练度与账号冒险团。
            var proficiency = ProficiencyProgression.Advance(
                new(role.DungeonState!.ProficiencyLevel, role.DungeonState.ProficiencyExperience), Reward.TotalExperience, growth);
            Check(committed.Character.Learning.Experience == role.Learning.Experience && Receipts() == 1 &&
                committed.Character.DungeonState is { } progressed &&
                progressed.ProficiencyLevel == proficiency!.After.Level &&
                progressed.ProficiencyExperience == proficiency.After.Experience,
                "满级通关的经验条保持95级入口线，整单奖励等额推进熟练度");
            Check(AdventureRow("level") is 3L && AdventureRow("experience") is 270L &&
                (string)AdventureRow("remainder")! == expected.Remainder.ToString(CultureInfo.InvariantCulture) &&
                AdventureRow("capsule_experience") is 100L && AdventureRow("brave_experience") is 0L &&
                AdventureRow("revision") is 1L && Points() is { Length: 20 } points &&
                BinaryPrimitives.ReadUInt32LittleEndian(points) == 200 && points.Skip(4).All(value => value == 0) &&
                Position(role.Id) == checked((long)(cap + Reward.TotalExperience)),
                "账号等级、经验、余量、结晶、胶囊、点数与角色里程按同一笔提交落库，revision只递增一次");
            Check(Query("PRAGMA integrity_check") is "ok" && Convert.ToInt64(Query("SELECT count(*) FROM pragma_foreign_key_check")) == 0,
                "冒险团事务提交后数据库与外键完整");

            var replay = repository.CommitDungeonSettlement(owner, role, run, prepared, growth);
            Check(replay.Duplicate && replay.Adventure is null && Receipts() == 1 &&
                AdventureRow("revision") is 1L && AdventureRow("experience") is 270L &&
                Position(role.Id) == checked((long)(cap + Reward.TotalExperience)),
                "已提交同报告重放只读真实receipt，不重复推进账号冒险团或角色里程");
            Check(run.ConfirmCommittedSettlement(prepared) == DungeonRunOutcome.Accepted, "确定持久结果后确认原实例结算");

            var second = repository.Create(owner, MaxLevelFixture(growth, "MaxAdv2"));
            var expectedSecond = AdventureProgression.Advance(expected, Reward.TotalExperience * adventure.ExperienceRate,
                Reward.TotalExperience, adventure);
            var (secondRun, secondAuthority) = CreateRun(second);
            secondRun.AcceptConfirmedDeath(secondAuthority.Death(secondRun.Snapshot.Identity, 20));
            var secondPrepared = secondRun.PrepareSettlement(secondRun.ClearFact!, new byte[] { 4, 5, 6 }, Reward).Pending!;
            var committedSecond = repository.CommitDungeonSettlement(owner, second, secondRun, secondPrepared, growth);
            Check(committedSecond.Adventure is { } progress2 && progress2.State.Level == expectedSecond.Level &&
                progress2.State.Experience == expectedSecond.Experience &&
                progress2.State.Points.SequenceEqual(expectedSecond.Points) && progress2.State.Revision == 2 &&
                AdventureRow("revision") is 2L && AdventureRow("experience") is 570L && Points() is { Length: 20 } next &&
                BinaryPrimitives.ReadUInt32LittleEndian(next) == 250 &&
                Position(second.Id) == checked((long)(cap + Reward.TotalExperience)),
                "第二笔满级通关沿用事务内当前快照继续累计账号经验与勇者结晶并递增revision");

            // 已到40级但未满级：入口线以内按100%计入，原始溢出为0，不产生结晶与胶囊计量。
            ulong counting = growth.GetLevelStartExperience(AdventureCredit.CountingLevel);
            var normal = repository.Create(owner, LevelFixture(growth, "NormalAdv", AdventureCredit.CountingLevel));
            var expectedNormal = AdventureProgression.Advance(expectedSecond, Reward.TotalExperience, 0, adventure);
            var (normalRun, normalAuthority) = CreateRun(normal);
            normalRun.AcceptConfirmedDeath(normalAuthority.Death(normalRun.Snapshot.Identity, 20));
            var normalPrepared = normalRun.PrepareSettlement(normalRun.ClearFact!, new byte[] { 7, 8, 9 }, Reward).Pending!;
            var committedNormal = repository.CommitDungeonSettlement(owner, normal, normalRun, normalPrepared, growth);
            Check(committedNormal.Adventure is { } normalProgress &&
                normalProgress.State.Level == expectedNormal.Level &&
                normalProgress.State.Experience == expectedNormal.Experience &&
                normalProgress.State.Points.SequenceEqual(expectedSecond.Points) &&
                normalProgress.State.CapsuleExperience == expectedSecond.CapsuleExperience &&
                normalProgress.MaximumLevelExperience == 0 && normalProgress.State.Revision == 3 &&
                Position(normal.Id) == checked((long)(counting + Reward.TotalExperience)) && committedNormal.GainedExperience > 0,
                "40级以上未满级角色的通关经验按100%计入账号，不产生结晶或胶囊溢出");
            Check(Query("PRAGMA integrity_check") is "ok" && Convert.ToInt64(Query("SELECT count(*) FROM pragma_foreign_key_check")) == 0,
                "多次冒险团结算后数据库与外键仍然完整");
        }
        finally { Directory.Delete(root, true); }
        Console.WriteLine($"PASS {TestCheck.Count - before} 通关冒险团折算、里程、结晶胶囊、回滚与重放断言");

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
        object? AdventureRow(string column) => Query($"SELECT {column} FROM account_adventures WHERE account='advsettle'");
        long Position(uint characterId) => Convert.ToInt64(Query(
            $"SELECT position_experience FROM character_adventure_credits WHERE character_id={characterId} AND account='advsettle'"));
        byte[]? Points() => (byte[]?)Query("SELECT points FROM account_adventures WHERE account='advsettle'");
    }

    private static NewCharacter MaxLevelFixture(CharacterGrowthCatalog growth, string name) => Fixture(name) with
    {
        Level = CharacterGrowthCatalog.MaximumSupportedLevel,
        InitialLearning = Learning(growth.GetLevelStartExperience(CharacterGrowthCatalog.MaximumSupportedLevel))
    };

    private static NewCharacter LevelFixture(CharacterGrowthCatalog growth, string name, int level) => Fixture(name) with
    {
        Level = checked((byte)level),
        InitialLearning = Learning(growth.GetLevelStartExperience(level))
    };
}
