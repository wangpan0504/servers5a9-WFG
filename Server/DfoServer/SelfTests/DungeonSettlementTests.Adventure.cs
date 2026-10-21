using System.Buffers.Binary;
using System.Globalization;
using DfoServer.Game.Accounts;
using DfoServer.Game.Adventure;
using DfoServer.Game.Auth;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Characters;
using DfoServer.Persistence;
using DfoServer.Testing;
using Microsoft.Data.Sqlite;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class DungeonSettlementTests
{
    // 满级角色的整单通关经验在结算事务内推进账号冒险团；事务内重读快照并按同一规则重算增量，拒绝伪造计划。
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
            var repository = new CharacterRepository(path);
            // account_adventures 以外键引用 accounts，先按真实登录路径建账号行再初始化冒险团。
            new AuthenticationService(new AuthenticationRepository(path)).Create("advsettle", "fixture-password");
            var owners = new AccountOwners();
            using var owner = owners.Acquire("advsettle", () => { });
            repository.InitializeAdventure(owner, adventure, now);
            var role = repository.Create(owner, MaxLevelFixture(growth, "MaxAdv"));
            var plan = AdventureProgression.Plan(repository.ReadAdventure(owner, adventure, now),
                Reward.TotalExperience, adventure, now);
            var (run, authority) = CreateRun(role);
            run.AcceptConfirmedDeath(authority.Death(run.Snapshot.Identity, 20));
            var prepared = run.PrepareSettlement(run.ClearFact!, new byte[] { 1, 2, 3 }, Reward).Pending!;

            Reject<InvalidOperationException>(() => repository.CommitDungeonSettlement(owner, role, run, prepared, growth,
                adventure: plan with { After = plan.After with { Level = 2 } }), "伪造冒险团提交后状态整笔拒绝");
            Check(SkillRepositoryTests.Equal(repository.List(owner).Single(), role) && Receipts() == 0 &&
                AdventureRow("revision") is 0L && AdventureRow("level") is 1L,
                "伪造计划在写入前失败，不留下角色经验、receipt或账号进度");

            Execute("UPDATE account_adventures SET revision=revision+1");
            Reject<InvalidOperationException>(() => repository.CommitDungeonSettlement(owner, role, run, prepared, growth,
                adventure: plan), "事务内账号进度版本与规划快照不一致拒绝");
            Execute("UPDATE account_adventures SET revision=revision-1");
            Check(AdventureRow("level") is 1L && AdventureRow("experience") is 0L && AdventureRow("revision") is 0L,
                "版本失配不改变账号冒险团进度");

            Execute("CREATE TRIGGER fail_adventure BEFORE UPDATE OF level ON account_adventures BEGIN SELECT RAISE(ABORT,'adventure failure'); END");
            Reject<SqliteException>(() => repository.CommitDungeonSettlement(owner, role, run, prepared, growth, adventure: plan),
                "冒险团等级写入的晚期故障回滚已写入的余额、角色成长与receipt");
            Execute("DROP TRIGGER fail_adventure");
            Check(SkillRepositoryTests.Equal(repository.List(owner).Single(), role) && Receipts() == 0 &&
                AdventureRow("revision") is 0L && AdventureRow("level") is 1L && run.IsPendingSettlement(prepared),
                "晚期失败不留下半份账号进度，原候选仍可重试");

            var committed = repository.CommitDungeonSettlement(owner, role, run, prepared, growth, adventure: plan);
            Check(!committed.Duplicate && committed.Adventure is { } progress &&
                progress.State.Level == 3 && progress.State.Experience == 270 &&
                progress.State.CapsuleExperience == 200 && progress.State.BraveExperience == 0 &&
                progress.State.Points.SequenceEqual(new uint[] { 200, 0, 0, 0, 0 }) && progress.State.Revision == 1 &&
                progress.MaximumLevelExperience == Reward.TotalExperience && committed.Character.Level == 95,
                "满级通关的整单经验在同一事务推进账号等级、勇者结晶与胶囊");
            ulong characterCap = growth.GetCumulativeExperienceBoundary(CharacterGrowthCatalog.MaximumSupportedLevel) - 1;
            Check(committed.Character.Learning.Experience == Math.Min(characterCap, role.Learning.Experience + Reward.TotalExperience) &&
                Receipts() == 1, "角色经验同时按满级截断提交，不因账号溢出多给角色经验");
            Check(AdventureRow("level") is 3L && AdventureRow("experience") is 270L &&
                (string)AdventureRow("remainder")! == plan.After.Remainder.ToString(CultureInfo.InvariantCulture) &&
                AdventureRow("capsule_experience") is 200L && AdventureRow("brave_experience") is 0L &&
                AdventureRow("revision") is 1L && Points() is { Length: 20 } points &&
                BinaryPrimitives.ReadUInt32LittleEndian(points) == 200 && points.Skip(4).All(value => value == 0),
                "账号等级、经验、小数余量、结晶、胶囊与点数按规划落库，revision只递增一次");
            Check(Query("PRAGMA integrity_check") is "ok" && Convert.ToInt64(Query("SELECT count(*) FROM pragma_foreign_key_check")) == 0,
                "冒险团事务提交后数据库与外键完整");

            var replay = repository.CommitDungeonSettlement(owner, role, run, prepared, growth, adventure: plan);
            Check(replay.Duplicate && replay.Adventure is null && Receipts() == 1 &&
                AdventureRow("revision") is 1L && AdventureRow("experience") is 270L,
                "已提交同报告重放只读真实receipt，不重复推进账号冒险团");
            Check(run.ConfirmCommittedSettlement(prepared) == DungeonRunOutcome.Accepted, "确定持久结果后确认原实例结算");

            var second = repository.Create(owner, MaxLevelFixture(growth, "MaxAdv2"));
            var plan2 = AdventureProgression.Plan(repository.ReadAdventure(owner, adventure, now),
                Reward.TotalExperience, adventure, now);
            var (secondRun, secondAuthority) = CreateRun(second);
            secondRun.AcceptConfirmedDeath(secondAuthority.Death(secondRun.Snapshot.Identity, 20));
            var secondPrepared = secondRun.PrepareSettlement(secondRun.ClearFact!, new byte[] { 4, 5, 6 }, Reward).Pending!;
            var committedSecond = repository.CommitDungeonSettlement(owner, second, secondRun, secondPrepared, growth, adventure: plan2);
            Check(committedSecond.Adventure is { } progress2 && progress2.State.Level == 3 &&
                progress2.State.Experience == 570 && progress2.State.Points[0] == 250 && progress2.State.Revision == 2 &&
                AdventureRow("revision") is 2L && AdventureRow("experience") is 570L && Points() is { Length: 20 } next &&
                BinaryPrimitives.ReadUInt32LittleEndian(next) == 250,
                "第二笔满级通关沿用事务内当前快照继续累计账号经验与勇者结晶并递增revision");
            Check(Query("PRAGMA integrity_check") is "ok" && Convert.ToInt64(Query("SELECT count(*) FROM pragma_foreign_key_check")) == 0,
                "多次冒险团结算后数据库与外键仍然完整");
        }
        finally { Directory.Delete(root, true); }
        Console.WriteLine($"PASS {TestCheck.Count - before} 通关冒险团升级、结晶胶囊、快照防伪及回滚断言");

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
        byte[]? Points() => (byte[]?)Query("SELECT points FROM account_adventures WHERE account='advsettle'");
    }

    private static NewCharacter MaxLevelFixture(CharacterGrowthCatalog growth, string name) => Fixture(name) with
    {
        Level = CharacterGrowthCatalog.MaximumSupportedLevel,
        InitialLearning = Learning(growth.GetLevelStartExperience(CharacterGrowthCatalog.MaximumSupportedLevel))
    };
}
