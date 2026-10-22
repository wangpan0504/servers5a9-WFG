using DfoServer.Game.Accounts;
using DfoServer.Game.Adventure;
using DfoServer.Game.Auth;
using DfoServer.Game.Characters;
using DfoServer.GameWorld.Adventure;
using DfoServer.GameWorld.Characters;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class AdventureCapsuleTests
{
    public static void RunResources(PvfArchive pvf, SkillLearningCatalog learning, AdventureCatalog catalog)
    {
        int checks = Count;
        var growth = global::DfoServer.Testing.SelfTestCatalogs.Growth(pvf, learning);
        var service = new AdventureCapsuleService(catalog, growth);
        var now = DateTimeOffset.UtcNow;
        string root = Path.Combine(Path.GetTempPath(), "s5a9-capsule-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "game.db");
            // 装配冒险团与成长资源后启用经验账本：胶囊属于排除来源，只推进角色里程。
            var repo = new CharacterRepository(path, null, catalog, growth);
            new AuthenticationService(new AuthenticationRepository(path)).Create("capsule", "fixture-password");
            using var owner = new AccountOwners().Acquire("capsule", () => { });
            // 胶囊资源的等级下限可能高于40级门槛，角色必须同时满足两者才能生成合法兑换计划；
            // 40级门槛本身由 AdventureLedgerTests 覆盖，这里按胶囊下限取样。
            byte level = checked((byte)Math.Max(catalog.Capsule.MinimumLevel, AdventureCredit.CountingLevel));
            var actor = repo.Create(owner, new("CapActor", new(1, 0, 10, 10), null)
            {
                Level = level, DungeonState = CharacterDungeonState.NewOrdinary,
                InitialLearning = new(growth.GetLevelStartExperience(level), [new(0, 10, 0, []), new(1, 20, 0, [])])
            });
            repo.InitializeAdventure(owner, catalog, now);
            using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False;Foreign Keys=True"); db.Open();
            // 周期列必须与当前周四边界一致：否则载入时按公告清空刚种下的胶囊进度。
            Execute("UPDATE account_adventures SET capsule_experience=" + catalog.Capsule.MinimumExperience +
                ",capsule_week=" + AdventurePeriod.CapsuleWeek(catalog, now));
            var snapshot = repo.ReadAdventure(owner, catalog, now);
            That(service.Claim(snapshot, actor with { Level = (byte)(catalog.Capsule.MinimumLevel - 1) }).Error == 14 &&
                service.Claim(snapshot, actor with { Level = (byte)(catalog.Capsule.MaximumLevel + 1) }).Error == 13 &&
                service.Claim(snapshot, actor with { DungeonState = null }).Error == 3 &&
                service.Claim(snapshot, actor with { Learning = CharacterLearningState.Uninitialized }).Error == 3,
                "等级上下界、缺附属owner及无合法双树都不扣胶囊");
            var plan = service.Claim(snapshot, actor).Plan ?? throw new InvalidOperationException("匹配资源合法角色未生成胶囊计划");
            Execute("CREATE TRIGGER reject_capsule BEFORE INSERT ON account_adventure_receipts BEGIN SELECT RAISE(ABORT,'late failure'); END");
            Reject<SqliteException>(() => repo.CommitAdventureCapsule(owner, plan, catalog, now), "胶囊晚期凭据失败回滚角色成长和账号扣除");
            That(SkillRepositoryTests.Equal(repo.LoadCharacter(owner, actor.Slot), actor) &&
                Query("SELECT capsule_experience FROM account_adventures") is long progress && progress == (long)catalog.Capsule.MinimumExperience,
                "失败不留下经验、技能、版本或半份胶囊扣除");
            Execute("DROP TRIGGER reject_capsule");
            var committed = repo.CommitAdventureCapsule(owner, plan, catalog, now);
            That(committed.Character.Learning.Experience == actor.Learning.Experience + catalog.Capsule.GrantedExperience &&
                committed.Character.Learning.Trees[0].Sp - 10 == committed.Character.Learning.Trees[1].Sp - 20 &&
                committed.Snapshot.State.CapsuleExperience == 0 && committed.Snapshot.State.Level == 1 && committed.Snapshot.State.Experience == 0 &&
                // 胶囊兑换的角色经验不计入冒险团，按国服规则只累计排除额与角色里程。
                committed.Snapshot.State.ExcludedExperience == catalog.Capsule.GrantedExperience &&
                Convert.ToInt64(Query("SELECT position_experience FROM character_adventure_credits WHERE character_id=" + actor.Id +
                    " AND account='capsule'")) == checked((long)(growth.GetLevelStartExperience(level) + catalog.Capsule.GrantedExperience)),
                "正式XP与两树贡献同笔提交，胶囊经验只进排除额且不递归增加账号经验或胶囊");
            Reject<InvalidOperationException>(() => repo.CommitAdventureCapsule(owner, plan, catalog, now), "旧胶囊计划不重复扣除或发放");
            That(SkillRepositoryTests.Equal(new CharacterRepository(path).LoadCharacter(owner, actor.Slot), committed.Character) &&
                Query("PRAGMA integrity_check") is "ok" && Query("SELECT count(*) FROM pragma_foreign_key_check") is 0L, "重开恢复完整成长与账号归属");
            void Execute(string sql) { using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
            object? Query(string sql) { using var command = db.CreateCommand(); command.CommandText = sql; return command.ExecuteScalar(); }
        }
        finally { Directory.Delete(root, true); }
        Console.WriteLine($"PASS {Count - checks} 胶囊资源资格、双树成长、SQL回滚和重开断言");
    }
}
