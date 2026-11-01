using System.Globalization;
using DfoServer.Game.Accounts;
using DfoServer.Game.Adventure;
using DfoServer.Game.Auth;
using DfoServer.Game.Characters;
using DfoServer.GameWorld.Adventure;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using DfoServer.Testing;
using Microsoft.Data.Sqlite;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

// 国服95经验账本的仓储入口：一次性开启、开启标记幂等、失败整笔回滚，以及GM改等级只记排除额。
internal static class AdventureLedgerTests
{
    static readonly Location Spawn = new(38, 1, 450, 234);

    public static void Run(PvfArchive pvf)
    {
        int before = Count;
        var growth = SelfTestCatalogs.Growth(pvf);
        var adventure = AdventureCoreTests.SyntheticCatalog();
        var now = new DateTimeOffset(2026, 10, 11, 0, 0, 0, TimeSpan.Zero);
        string root = Path.Combine(Path.GetTempPath(), "S5A9-adventure-ledger-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Opening(Path.Combine(root, "opening.db"), growth, adventure, now);
            Rollback(Path.Combine(root, "rollback.db"), growth, adventure, now);
            Gm(Path.Combine(root, "gm.db"), growth, adventure, now);
            Deletion(Path.Combine(root, "deletion.db"), growth, adventure, now);
        }
        finally { TestFiles.DeleteDirectory(root, true); }
        Console.WriteLine($"PASS {Count - before} 冒险团开启、排除来源、GM改写与回滚断言");
    }

    // 旧库角色先于账本存在：开启时只按40级以上角色的名册经验与熟练度换算一次性建立账号总量；
    // 40级以下角色只建立待追溯里程（基准0），升到40级时再计入。
    static void Opening(string path, CharacterGrowthCatalog growth, AdventureCatalog adventure, DateTimeOffset now)
    {
        new AuthenticationService(new AuthenticationRepository(path)).Create("advopen", "fixture-password");
        new AuthenticationService(new AuthenticationRepository(path)).Create("advopen2", "fixture-password");
        var owners = new AccountOwners();
        using var owner = owners.Acquire("advopen", () => { });
        using var owner2 = owners.Acquire("advopen2", () => { });
        // 角色由未装配冒险团的仓储建立，模拟 v73 之前的真实存档：只有 characters 行，没有里程行。
        var legacy = new CharacterRepository(path);
        var alpha = legacy.Create(owner, Character("OpenAlpha"));
        var beta = legacy.Create(owner, Character("OpenBeta"));
        var gamma = legacy.Create(owner2, Character("OpenGamma"));
        ulong cap = growth.GetLevelStartExperience(CharacterGrowthCatalog.MaximumSupportedLevel);
        ulong alphaExperience = checked(growth.GetLevelStartExperience(10) + 5000);
        Execute($"UPDATE characters SET level=10,experience={alphaExperience} WHERE id={alpha.Id}");
        Execute($"UPDATE characters SET level=95,experience={checked(cap + 12345)} WHERE id={beta.Id}");
        Execute($"UPDATE character_dungeon_state SET proficiency_level=2,proficiency_experience=5 WHERE character_id={alpha.Id}");

        var repository = new CharacterRepository(path, null, adventure, growth);
        // 已存在的账号精确总量高于名册换算时取较大者：开启不能降低玩家已有的冒险团等级。
        repository.InitializeAdventure(owner2, adventure, now);
        Execute("UPDATE account_adventures SET level=3,experience=20 WHERE account='advopen2'");

        // 10级角色即使有经验与熟练度也不提供冒险团经验；名册只含满级 beta，熟练度换算同样跳过 alpha。
        decimal roster = cap;
        decimal honor = 0m;
        decimal opening = roster + honor;
        var commit = repository.OpenAdventureLedger(now);
        var expected = AdventureCredit.FromExactTotal(opening, AdventureState.Initial("advopen", now), adventure);
        // 开启总量按账号汇总：advopen2 已有精确总量 50（等级3、当前级20，合成表边界10+20）大于名册换算，取现值。
        Check(commit.Ran && commit.Accounts == 2 && commit.Characters == 3 && commit.Roster == roster &&
            commit.Honor == honor && commit.Opening == opening + 50m,
            $"开启账本只按40级以上角色的名册经验与熟练度建立总量（ran={commit.Ran},accounts={commit.Accounts},characters={commit.Characters},roster={commit.Roster},honor={commit.Honor},opening={commit.Opening}）");
        var state = repository.ReadAdventure(owner, adventure, now).State;
        Check(state.Level == expected.Level && state.Experience == expected.Experience &&
            state.Remainder == expected.Remainder && state.ExcludedExperience == 0 &&
            AdventureCredit.ExactTotal(state, adventure) == opening &&
            Query("SELECT revision FROM account_adventures WHERE account='advopen'") is 0L &&
            Convert.ToDecimal(Query("SELECT opening_experience FROM account_adventures WHERE account='advopen'"), CultureInfo.InvariantCulture) == opening &&
            Convert.ToDecimal(Query("SELECT excluded_experience FROM account_adventures WHERE account='advopen'"), CultureInfo.InvariantCulture) == 0,
            "开启后的等级、当前级经验与小数余量精确等于名册总量，排除额归零");
        Check(Query("SELECT value FROM adventure_ledger_meta WHERE key='opening'") is string marker &&
            marker == SqliteMigrations.CurrentVersion.ToString(CultureInfo.InvariantCulture) &&
            Position(alpha.Id) == checked((long)alphaExperience) && Baseline(alpha.Id) == 0L &&
            Position(beta.Id) == checked((long)(cap + 12345)) && Baseline(beta.Id) == checked((long)(cap + 12345)) &&
            Position(gamma.Id) == 0L && Baseline(gamma.Id) == 0L,
            "开启标记写入当前版本，40级以上角色基准落在当前位置，40级以下只记待追溯里程");
        // 已有精确总量更大的账号保持原等级，只补写开启量与标记。
        Check(Query("SELECT level FROM account_adventures WHERE account='advopen2'") is 3L &&
            Query("SELECT experience FROM account_adventures WHERE account='advopen2'") is 20L &&
            Query("SELECT revision FROM account_adventures WHERE account='advopen2'") is 1L &&
            Convert.ToDecimal(Query("SELECT opening_experience FROM account_adventures WHERE account='advopen2'"), CultureInfo.InvariantCulture) == 50m &&
            Position(gamma.Id) == 0L,
            "已有账号总量更高时开启取较大者，不降低等级或重置已有进度");

        var again = repository.OpenAdventureLedger(now.AddHours(1));
        Check(!again.Ran && again.Accounts == 0 && again.Opening == 0 &&
            Query("SELECT revision FROM account_adventures WHERE account='advopen'") is 0L &&
            Convert.ToDecimal(Query("SELECT opening_experience FROM account_adventures WHERE account='advopen'"), CultureInfo.InvariantCulture) == opening &&
            Convert.ToInt64(Query("SELECT count(*) FROM adventure_ledger_meta")) == 1,
            "开启标记阻止再次补发：重复启动不改变任何账号总量");

        object? Query(string sql) => LedgerQuery(path, sql);
        void Execute(string sql) => _ = LedgerQuery(path, sql);
        long Position(uint characterId) => Convert.ToInt64(LedgerQuery(path,
            $"SELECT position_experience FROM character_adventure_credits WHERE character_id={characterId}"));
        long Baseline(uint characterId) => Convert.ToInt64(LedgerQuery(path,
            $"SELECT baseline_experience FROM character_adventure_credits WHERE character_id={characterId}"));
    }

    // 开启是单事务：任一账号写入失败时 marker、账号行与里程行都不留下。
    static void Rollback(string path, CharacterGrowthCatalog growth, AdventureCatalog adventure, DateTimeOffset now)
    {
        new AuthenticationService(new AuthenticationRepository(path)).Create("advfail", "fixture-password");
        var owners = new AccountOwners();
        using var owner = owners.Acquire("advfail", () => { });
        var legacy = new CharacterRepository(path);
        var role = legacy.Create(owner, Character("OpenFail"));
        Execute($"UPDATE characters SET level=10,experience={checked(growth.GetLevelStartExperience(10) + 700)} WHERE id={role.Id}");
        var repository = new CharacterRepository(path, null, adventure, growth);
        Execute("CREATE TRIGGER fail_opening BEFORE INSERT ON account_adventures BEGIN SELECT RAISE(ABORT,'opening failure'); END");
        Reject<SqliteException>(() => repository.OpenAdventureLedger(now), "开启写入晚期失败整笔回滚");
        Check(Query("SELECT count(*) FROM account_adventures") is 0L &&
            Query("SELECT count(*) FROM adventure_ledger_meta") is 0L &&
            Query("SELECT count(*) FROM character_adventure_credits") is 0L,
            "失败后不留下账号总量、开启标记或角色里程基线");
        Execute("DROP TRIGGER fail_opening");
        var committed = repository.OpenAdventureLedger(now);
        Check(committed.Ran && Query("SELECT count(*) FROM adventure_ledger_meta") is 1L &&
            Query("SELECT count(*) FROM character_adventure_credits") is 1L,
            "移除故障后同一数据库可正常完成一次性开启");

        object? Query(string sql) => LedgerQuery(path, sql);
        void Execute(string sql) => _ = LedgerQuery(path, sql);
    }

    // GM改等级属于排除来源：直升跨越40级不追溯历史；40级以上只按有效位置差额记排除额，账号总量保持不变。
    static void Gm(string path, CharacterGrowthCatalog growth, AdventureCatalog adventure, DateTimeOffset now)
    {
        new AuthenticationService(new AuthenticationRepository(path)).Create("advgm", "fixture-password");
        var owners = new AccountOwners();
        using var owner = owners.Acquire("advgm", () => { });
        var repository = new CharacterRepository(path, null, adventure, growth);
        repository.InitializeAdventure(owner, adventure, now);
        var role = repository.Create(owner, Character("GmHero"));
        ulong level50 = growth.GetLevelStartExperience(50);
        var edited = repository.UpdateProgress(owner, new(CharacterProgressField.Level, "advgm", "GmHero", 50, 0));
        // 1→50 属于直升跨越40级：历史经验作废，基准落在改写后的位置，排除额与账号版本都不变。
        Check(edited.Level == 50 && Query("SELECT level FROM account_adventures WHERE account='advgm'") is 1L &&
            Convert.ToInt64(Query("SELECT experience FROM account_adventures WHERE account='advgm'")) == 0L &&
            Convert.ToDecimal(Query("SELECT excluded_experience FROM account_adventures WHERE account='advgm'"), CultureInfo.InvariantCulture) == 0m &&
            Convert.ToDecimal(Query("SELECT opening_experience FROM account_adventures WHERE account='advgm'"), CultureInfo.InvariantCulture) == 0m &&
            Query("SELECT revision FROM account_adventures WHERE account='advgm'") is 0L &&
            Position(role.Id) == checked((long)level50) && Baseline(role.Id) == checked((long)level50),
            "GM直升跨越40级只改写里程与基准，不追溯历史或改动账号总量");
        ulong level60 = growth.GetLevelStartExperience(60);
        var raised = repository.UpdateProgress(owner, new(CharacterProgressField.Level, "advgm", "GmHero", 60, 0));
        // 40级以上改写按有效位置差额记入排除额。
        Check(raised.Level == 60 &&
            Convert.ToDecimal(Query("SELECT excluded_experience FROM account_adventures WHERE account='advgm'"), CultureInfo.InvariantCulture) == level60 - level50 &&
            Query("SELECT level FROM account_adventures WHERE account='advgm'") is 1L &&
            Query("SELECT revision FROM account_adventures WHERE account='advgm'") is 1L &&
            Position(role.Id) == checked((long)level60) && Baseline(role.Id) == checked((long)level60),
            "40级以上GM升级只按有效位置差额记录排除额，账号总量不变");
        ulong level10 = growth.GetLevelStartExperience(10);
        var reset = repository.UpdateProgress(owner, new(CharacterProgressField.Level, "advgm", "GmHero", 10, 0));
        Check(reset.Level == 10 &&
            Convert.ToDecimal(Query("SELECT excluded_experience FROM account_adventures WHERE account='advgm'"), CultureInfo.InvariantCulture) == level60 - level50 &&
            Query("SELECT level FROM account_adventures WHERE account='advgm'") is 1L &&
            Convert.ToInt64(Query("SELECT experience FROM account_adventures WHERE account='advgm'")) == 0L &&
            Query("SELECT revision FROM account_adventures WHERE account='advgm'") is 1L &&
            Position(role.Id) == checked((long)level10) && Baseline(role.Id) == checked((long)level10),
            "GM降回40级以下重新建立待追溯基准，已记排除额与账号总量保持");

        object? Query(string sql) => LedgerQuery(path, sql);
        long Position(uint characterId) => Convert.ToInt64(LedgerQuery(path,
            $"SELECT position_experience FROM character_adventure_credits WHERE character_id={characterId} AND account='advgm'"));
        long Baseline(uint characterId) => Convert.ToInt64(LedgerQuery(path,
            $"SELECT baseline_experience FROM character_adventure_credits WHERE character_id={characterId} AND account='advgm'"));
    }

    // 删除角色不减少账号总量：净贡献折进开启经验后，里程行随角色清除。
    static void Deletion(string path, CharacterGrowthCatalog growth, AdventureCatalog adventure, DateTimeOffset now)
    {
        new AuthenticationService(new AuthenticationRepository(path)).Create("advdel", "fixture-password");
        var owners = new AccountOwners();
        using var owner = owners.Acquire("advdel", () => { });
        var repository = new CharacterRepository(path, null, adventure, growth);
        repository.InitializeAdventure(owner, adventure, now);
        var role = repository.Create(owner, Character("DelHero"));
        ulong cap = growth.GetLevelStartExperience(CharacterGrowthCatalog.MaximumSupportedLevel);
        ulong position = checked(cap + 10000);
        // 模拟已到40级、入口线以上还有未结算里程的导入角色：基准在入口线，里程多出10000。
        Execute($"UPDATE characters SET level=95 WHERE id={role.Id}");
        Execute($"UPDATE character_adventure_credits SET baseline_experience={cap},position_experience={position} WHERE character_id={role.Id}");
        var target = repository.LoadDeletionTarget(owner, role.Slot);
        repository.Delete(owner, target);
        decimal contribution = AdventureCredit.Value(position, cap, adventure.ExperienceRate) - cap;
        Check(Query("SELECT count(*) FROM characters") is 0L &&
            Query("SELECT count(*) FROM character_adventure_credits") is 0L &&
            Query("SELECT level FROM account_adventures WHERE account='advdel'") is 1L &&
            Convert.ToInt64(Query("SELECT experience FROM account_adventures WHERE account='advdel'")) == 0L &&
            Convert.ToDecimal(Query("SELECT opening_experience FROM account_adventures WHERE account='advdel'"), CultureInfo.InvariantCulture) == contribution &&
            Query("SELECT revision FROM account_adventures WHERE account='advdel'") is 1L,
            "删除角色把里程净贡献折进开启经验，账号总量不减少且里程行随角色清除");
        // 40级以下角色只有待追溯自然经验，删除时随角色作废，不能凭空折进账号总量。
        var pending = repository.Create(owner, Character("DelPending"));
        ulong pendingPosition = checked(growth.GetLevelStartExperience(20) + 5000);
        Execute($"UPDATE characters SET level=20 WHERE id={pending.Id}");
        Execute($"UPDATE character_adventure_credits SET baseline_experience=0,position_experience={pendingPosition} WHERE character_id={pending.Id}");
        var pendingTarget = repository.LoadDeletionTarget(owner, pending.Slot);
        repository.Delete(owner, pendingTarget);
        Check(Query("SELECT count(*) FROM characters") is 0L &&
            Query("SELECT count(*) FROM character_adventure_credits") is 0L &&
            Convert.ToDecimal(Query("SELECT opening_experience FROM account_adventures WHERE account='advdel'"), CultureInfo.InvariantCulture) == contribution &&
            Query("SELECT revision FROM account_adventures WHERE account='advdel'") is 1L,
            "删除40级以下角色不把待追溯自然经验折进开启经验，账号总量保持不变");

        object? Query(string sql) => LedgerQuery(path, sql);
        void Execute(string sql) => _ = LedgerQuery(path, sql);
    }

    static NewCharacter Character(string name) => new(name, Spawn, Spawn)
    {
        InitialLearning = Learning(0),
        DungeonState = CharacterDungeonState.NewOrdinary
    };

    static CharacterLearningState Learning(ulong experience) =>
        new(experience, Array.AsReadOnly(new[] { new CharacterSkillTree(0, 3, 4, []), new CharacterSkillTree(1, 8, 5, []) }));

    static void Check(bool condition, string message) => TestCheck.That(condition, message);

    static object? LedgerQuery(string path, string sql)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Pooling = false, ForeignKeys = true
        }.ToString());
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }
}
