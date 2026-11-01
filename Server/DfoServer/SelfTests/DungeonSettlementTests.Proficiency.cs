using DfoServer.Game.Accounts;
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
    // 国服95模型：角色经验到账与熟练度到账来自同一笔通关经验，二者之和是本次有效经验。
    // 满级入口线之上整单进熟练度；289级封顶后整单丢弃；跨入口线的一单按入口线拆分。
    public static void RunProficiencyResources(PvfArchive pvf)
    {
        int before = TestCheck.Count;
        string root = Path.Combine(Path.GetTempPath(), "S5A9-settlement-proficiency-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "game.db");
        try
        {
            var growth = global::DfoServer.Testing.SelfTestCatalogs.Growth(pvf);
            var repository = new CharacterRepository(path);
            var owners = new AccountOwners();
            using var owner = owners.Acquire("settle-proficiency", () => { });
            ulong cap = growth.GetLevelStartExperience(CharacterGrowthCatalog.MaximumSupportedLevel);

            // 入口线模型：满级角色角色经验到账0，整单1000记入已有20级熟练度。
            var maxed = repository.Create(owner, MaxLevelFixture(growth, "ProfMax") with { DungeonState = new(256, 1, 0, 20, 0) });
            var (maxCommit, maxRun, maxPrepared) = Settle(repository, owner, growth, maxed);
            var maxState = maxCommit.Character.DungeonState!;
            Check(!maxCommit.Duplicate && maxCommit.GainedExperience == 0 && maxCommit.ProficiencyGained == Reward.TotalExperience &&
                maxCommit.Character.Level == CharacterGrowthCatalog.MaximumSupportedLevel &&
                maxCommit.Character.Learning.Experience == cap && maxState.ProficiencyLevel == 20 &&
                maxState.ProficiencyExperience == Reward.TotalExperience &&
                Receipt(maxed) == Reward.TotalExperience,
                "满级20级熟练度的通关经验整单记入熟练度，角色经验保持95级入口线");
            var maxReplay = repository.CommitDungeonSettlement(owner, maxed, maxRun, maxPrepared, growth);
            Check(maxReplay.Duplicate && maxReplay.GainedExperience == 0 &&
                maxReplay.ProficiencyGained == Reward.TotalExperience &&
                SkillRepositoryTests.Equal(maxReplay.Character, maxCommit.Character) && Receipts() == 1,
                "满级通关重复报告按回执回放同额熟练度到账，不重复推进或写账本");

            // 英雄90封顶：289级没有下一行经验条，整单丢弃，回执保存0。
            var capped = repository.Create(owner, MaxLevelFixture(growth, "ProfCapped") with
            { DungeonState = new(256, 1, 0, ProficiencyCatalog.MaximumLevel, 0) });
            var (cappedCommit, _, _) = Settle(repository, owner, growth, capped);
            var cappedState = cappedCommit.Character.DungeonState!;
            Check(!cappedCommit.Duplicate && cappedCommit.GainedExperience == 0 && cappedCommit.ProficiencyGained == 0 &&
                cappedCommit.Character.Learning.Experience == cap &&
                cappedState.ProficiencyLevel == ProficiencyCatalog.MaximumLevel && cappedState.ProficiencyExperience == 0 &&
                Receipt(capped) == 0,
                "熟练度289级封顶后通关经验丢弃，角色与熟练度都不再推进");

            // 94→95跨入口线：1000中600到角色、400到熟练度，合计仍是整单。
            ulong entry = cap - 600;
            var crossing = repository.Create(owner, LevelFixture(growth, "ProfCross", 94)
                with { InitialLearning = Learning(entry), DungeonState = new(256, 1, 0, 0, 0) });
            var (crossingCommit, _, _) = Settle(repository, owner, growth, crossing);
            var crossingState = crossingCommit.Character.DungeonState!;
            Check(!crossingCommit.Duplicate && crossingCommit.GainedExperience == 600 &&
                crossingCommit.ProficiencyGained == 400 &&
                crossingCommit.GainedExperience + crossingCommit.ProficiencyGained == Reward.TotalExperience &&
                crossingCommit.Character.Level == CharacterGrowthCatalog.MaximumSupportedLevel &&
                crossingCommit.Character.Learning.Experience == cap && crossingState.ProficiencyLevel == 0 &&
                crossingState.ProficiencyExperience == 400 && Receipt(crossing) == 400,
                "跨95级入口线的通关经验按入口线拆分到角色与熟练度，合计等于整单");

            // 未满级且不跨入口线：整单到账角色，熟练度不因通关经验凭空增加。
            var ordinary = repository.Create(owner, LevelFixture(growth, "ProfBelow", 90));
            var (ordinaryCommit, _, _) = Settle(repository, owner, growth, ordinary);
            var ordinaryState = ordinaryCommit.Character.DungeonState!;
            Check(growth.GetLevelExperienceSpan(90) > Reward.TotalExperience &&
                !ordinaryCommit.Duplicate && ordinaryCommit.GainedExperience == Reward.TotalExperience &&
                ordinaryCommit.ProficiencyGained == 0 && ordinaryCommit.Character.Level == 90 &&
                ordinaryState.ProficiencyLevel == 0 && ordinaryState.ProficiencyExperience == 0 && Receipt(ordinary) == 0,
                "未跨入口线的普通通关经验全部到账角色，熟练度保持0");
            Check(Query("PRAGMA integrity_check") is "ok" && Convert.ToInt64(Query("SELECT count(*) FROM pragma_foreign_key_check")) == 0,
                "熟练度拆分提交后数据库与外键完整");
        }
        finally { TestFiles.DeleteDirectory(root, true); }
        Console.WriteLine($"PASS {TestCheck.Count - before} 通关熟练度到账拆分、封顶与回执回放断言");

        (DungeonSettlementCommit Commit, SinglePlayerDungeonRun Run, PreparedDungeonSettlement Prepared) Settle(
            CharacterRepository repo, AccountOwners.Lease lease, CharacterGrowthCatalog catalog, Character role)
        {
            var (run, authority) = CreateRun(role);
            run.AcceptConfirmedDeath(authority.Death(run.Snapshot.Identity, 20));
            var prepared = run.PrepareSettlement(run.ClearFact!, new byte[] { 1, 2, 3 }, Reward).Pending!;
            return (repo.CommitDungeonSettlement(lease, role, run, prepared, catalog), run, prepared);
        }
        object? Query(string sql)
        {
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = path, Pooling = false, ForeignKeys = true }.ToString());
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = sql;
            return command.ExecuteScalar();
        }
        long Receipts() => Convert.ToInt64(Query("SELECT count(*) FROM character_dungeon_settlement_receipts"));
        long Receipt(Character role) => Convert.ToInt64(Query(
            $"SELECT proficiency_gain FROM character_dungeon_settlement_receipts WHERE character_id={role.Id}"));
    }
}
