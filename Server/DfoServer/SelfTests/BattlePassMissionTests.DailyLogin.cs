using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Auth;
using DfoServer.Game.BattlePass;
using DfoServer.Game.Characters;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class BattlePassMissionTests
{
    public static void RunDailyLoginCore()
    {
        int before = Count;
        DailyLoginPersistence();
        Console.WriteLine($"PASS BattlePassMissionTests daily login ({Count - before} checks)");
    }

    static void DailyLoginPersistence()
    {
        string root = Path.Combine(Path.GetTempPath(), "S5A9-pass-login-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        bool succeeded = false;
        try
        {
            string path = Path.Combine(root, "game.db");
            const string account = "bp-login-core", season = "core-login", late = "bp-login-late";
            const string marker = "BP login whole transaction reached";
            var now = Start.AddHours(1);
            var policy = new BattlePassMissionPolicy(season, Start, Catalog(dailyLogin: true));
            var adventure = AdventureCoreTests.SyntheticCatalog();
            var repository = new CharacterRepository(path);
            var auth = new AuthenticationService(new AuthenticationRepository(path));
            auth.Create(account, "fixture-password"); auth.Create(late, "fixture-password");
            var owners = new AccountOwners();
            using var owner = owners.Acquire(account, () => { });
            using var lateOwner = owners.Acquire(late, () => { });
            repository.InitializeAdventure(lateOwner, adventure, now);
            repository.ConfigureBattlePassMissions(policy);
            var role = repository.Create(owner, new NewCharacter("LoginCore", new Location(1, 0, 10, 10), null)
                { GrowType = 33, Level = 95 });
            repository.InitializeAdventure(owner, adventure, now);
            var one = Read(repository, owner, now);
            That(Find(one, 1, 43).Count == 1 && Find(one, 2, 56).Count == 1 &&
                one.Season is { Revision: 1, TotalExperience: 0, PassType: 0 },
                "首次真实登录同时计周与季原定义，不自动XP或高级资格");
            repository.InitializeAdventure(lateOwner, adventure, now);
            var lateState = Read(repository, lateOwner, now);
            That(Find(lateState, 1, 43).Count == 1 && Find(lateState, 2, 56).Count == 1 &&
                repository.ReadAdventure(lateOwner, adventure, now).State is { LoginStreakDays: 1, Revision: 0 },
                "同日冒险团已初始化再配置战令，本次真实登录只补日source且不改冒险团版本");
            string once = Snapshot();
            repository.InitializeAdventure(owner, adventure, now.AddMinutes(1));
            repository.InitializeAdventure(lateOwner, adventure, now.AddMinutes(1));
            That(Snapshot() == once, "同日重复真实登录不改来源、进度、资产与版本");
            using (var blocker = Open())
            using (var transaction = blocker.BeginTransaction(deferred: false))
            {
                repository.InitializeAdventure(owner, adventure, now.AddMinutes(2));
                That(Snapshot() == once, "已有当日source时外部writer不妨碍只读登录检查");
            }
            string beforeFault = Snapshot();
            Sql($"""
                CREATE TRIGGER fail_daily_login AFTER UPDATE OF revision ON account_battle_pass_seasons
                WHEN NEW.account='{account}' AND NEW.season_key='{season}' AND NEW.revision=2
                BEGIN
                  SELECT CASE WHEN NEW.total_experience=0
                    AND (SELECT last_login_day FROM account_adventures WHERE account=NEW.account)=20261030
                    AND (SELECT login_streak_days FROM account_adventures WHERE account=NEW.account)=2
                    AND (SELECT count(*) FROM account_battle_pass_source_events WHERE account=NEW.account AND source_kind='daily_login')=2
                    AND (SELECT count(*) FROM account_battle_pass_mission_progress WHERE account=NEW.account AND current_count=2)=2
                  THEN RAISE(ABORT,'{marker}') ELSE RAISE(ABORT,'BP login prerequisite missing') END;
                END;
                """);
            bool reached = false;
            try { repository.InitializeAdventure(owner, adventure, now.AddDays(1)); }
            catch (SqliteException error) when (Exact(error, marker)) { reached = true; }
            That(reached && Snapshot() == beforeFault,
                "日事实、两任务、source与季版本均已写后的准确晚故障整笔回滚");
            Sql("DROP TRIGGER fail_daily_login");
            repository.InitializeAdventure(owner, adventure, now.AddDays(1));
            var two = Read(repository, owner, now.AddDays(1));
            That(Find(two, 1, 43).Count == 2 && Find(two, 2, 56).Count == 2 && two.Season.Revision == 2 &&
                repository.ReadAdventure(owner, adventure, now.AddDays(1)).State.LoginStreakDays == 2,
                "同owner重试原日一次成功，冒险团和战令共同恢复");
            repository.InitializeAdventure(owner, adventure, now.AddDays(3));
            var three = Read(repository, owner, now.AddDays(3));
            That(Find(three, 1, 43) is { Count: 3, Ready: true, Completed: false } &&
                Find(three, 2, 56).Count == 3 && three.Season.TotalExperience == 0 &&
                repository.ReadAdventure(owner, adventure, now.AddDays(3)).State.LoginStreakDays == 1,
                "跳过日不补历史，登录季累加与冒险团连续日归1各保原语义");
            var claim = repository.CommitBattlePassMission(owner, role, three.Season.Revision, now.AddDays(3), 1, 43, 1, 0, new MissionClock(now.AddDays(3)));
            That(claim.Accepted && claim.State!.Season.TotalExperience == 1000 &&
                Find(claim.State!, 1, 43) is { Completed: true, Ready: false }, "周登录真实达标后通过唯一任务claim领取1000XP");
            string afterClaim = Snapshot();
            That(!repository.CommitBattlePassMission(owner, role, claim.State!.Season.Revision, now.AddDays(3), 1, 43, 1, 3, new MissionClock(now.AddDays(3))).Accepted &&
                Snapshot() == afterClaim, "重放周登录XPclaim零变化");
            repository.InitializeAdventure(owner, adventure, now.AddDays(7));
            var nextWeek = Read(repository, owner, now.AddDays(7));
            That(Find(nextWeek, 1, 43) is { Count: 1, Completed: false } && Find(nextWeek, 2, 56).Count == 4 &&
                nextWeek.Season.TotalExperience == 1000, "周四新周期只给新周一次，季累计与已领XP完整保留");
            string beforeRollbackClock = Snapshot();
            repository.InitializeAdventure(owner, adventure, now.AddDays(6));
            That(Snapshot() == beforeRollbackClock, "时钟回拨不产生旧日source或倒退冒险团状态");
            var restored = new CharacterRepository(path); restored.ConfigureBattlePassMissions(policy);
            var recovered = Read(restored, owner, now.AddDays(7));
            That(recovered.ToPayload().SequenceEqual(nextWeek.ToPayload()) && recovered.Season.TotalExperience == nextWeek.Season.TotalExperience &&
                recovered.Season.PassType == nextWeek.Season.PassType && recovered.Season.Revision == nextWeek.Season.Revision &&
                recovered.Season.Claims.SetEquals(nextWeek.Season.Claims),
                "新Repository恢复同季XP、版本及完整26槽计数与领取状态");
            for (int day = 8; day <= 55; day++) repository.InitializeAdventure(owner, adventure, now.AddDays(day));
            var capped = Read(repository, owner, now.AddDays(55));
            That(Find(capped, 2, 56) is { Count: 50, Ready: true, Completed: false } && capped.Season.TotalExperience == 1000,
                "季登录目标50封顶且只准备待领，不把后续登录自动发经验");
            long sourceCount = (long)Query("SELECT count(*) FROM account_battle_pass_source_events WHERE account=$p0", account).Single().Single()!;
            repository.InitializeAdventure(owner, adventure, policy.End);
            That((long)Query("SELECT count(*) FROM account_battle_pass_source_events WHERE account=$p0", account).Single().Single()! == sourceCount &&
                !Read(repository, owner, policy.End).Active, "活动结束后的真实冒险团登录不新增本季来源或虚构任务槽");
            That(Query("PRAGMA integrity_check").Single().Single() is "ok" && Query("PRAGMA foreign_key_check").Length == 0,
                "真实登录及晚故障重试后隔离库完整性和外键保持");
            succeeded = true;

            SqliteConnection Open()
            {
                var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, ForeignKeys = true }.ToString());
                db.Open(); return db;
            }
            void Sql(string sql)
            {
                using var db = Open(); using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
            }
            object?[][] Query(string sql, params object[] values)
            {
                using var db = Open(); using var command = db.CreateCommand(); command.CommandText = sql;
                for (int i = 0; i < values.Length; i++) command.Parameters.AddWithValue("$p" + i, values[i]);
                using var reader = command.ExecuteReader(); var rows = new List<object?[]>();
                while (reader.Read())
                {
                    var row = new object?[reader.FieldCount];
                    for (int i = 0; i < row.Length; i++) row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                    rows.Add(row);
                }
                return rows.ToArray();
            }
            string Snapshot() => JsonSerializer.Serialize(new[] {
                "SELECT * FROM characters ORDER BY id", "SELECT * FROM character_inventory_items ORDER BY item_uid",
                "SELECT * FROM character_inventory_slots ORDER BY character_id,list_type,slot_index",
                "SELECT * FROM account_wallets ORDER BY account", "SELECT * FROM inventory_audit_log ORDER BY audit_id",
                "SELECT * FROM currency_audit_log ORDER BY audit_id", "SELECT * FROM account_adventures ORDER BY account",
                "SELECT * FROM account_battle_pass_seasons ORDER BY account,season_key",
                "SELECT * FROM account_battle_pass_source_events ORDER BY account,source_kind,source_id",
                "SELECT * FROM account_battle_pass_mission_progress ORDER BY account,season_key,category,period_key,mission_id",
                "SELECT * FROM account_battle_pass_mission_claims ORDER BY account,season_key,category,period_key,mission_id",
                "SELECT * FROM account_battle_pass_claims ORDER BY account,season_key,level,track", "SELECT * FROM sqlite_sequence ORDER BY name"
            }.Select(sql => Query(sql)).ToArray());
        }
        finally
        {
            if (succeeded) TestFiles.DeleteDirectory(root, recursive: true);
            else Console.Error.WriteLine($"战令登录回归失败，隔离现场保留于：{root}");
        }
    }
}
