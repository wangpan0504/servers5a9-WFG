using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Adventure;
using DfoServer.Game.Auth;
using DfoServer.Game.BattlePass;
using DfoServer.Game.Characters;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class BattlePassMissionTests
{
    public static void RunCompletionCore()
    {
        int before = Count;
        CompletionPersistence();
        Console.WriteLine($"PASS BattlePassMissionTests completion ({Count - before} checks)");
    }

    static void CompletionPersistence()
    {
        string root = Path.Combine(Path.GetTempPath(), "S5A9-pass-completion-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        bool succeeded = false;
        try
        {
            string path = Path.Combine(root, "game.db");
            const string account = "bp-completion-core", season = "core-completion";
            const string marker = "BP completion and original XPclaim all written";
            var now = Start.AddDays(58).AddHours(1);
            var policy = new BattlePassMissionPolicy(season, Start, Catalog(completion: true));
            var adventure = AdventureCoreTests.SyntheticCatalog();
            var service = new AdventureExpeditionService(adventure, AdventureExpeditionTests.SyntheticCatalog());
            var repository = new CharacterRepository(path); repository.ConfigureBattlePassMissions(policy);
            new AuthenticationService(new AuthenticationRepository(path)).Create(account, "fixture-password");
            var owners = new AccountOwners(); using var owner = owners.Acquire(account, () => { });
            var point = new Location(1, 0, 10, 10);
            var role = repository.Create(owner, new NewCharacter("CompleteCore", point, null) { GrowType = 33, Level = 95 });
            repository.InitializeAdventure(owner, adventure, now);
            var towns = new TownCatalog(new Dictionary<(byte, byte), TownArea>(), point, point,
                new TownDock(0, [], []), [], new Dictionary<uint, uint[]>());
            var clock = new MissionClock(now);
            var session = new GameSession(repository, towns, owner, timeProvider: clock);
            session.Select(role.Slot); session.MarkReady();
            // ReadBattlePassMissions首次合法建季是只读入口的正常懒初始化，必须先建好零状态再核拒绝不变。
            var initial = Read(repository, owner, now);
            That(initial.Season is { TotalExperience: 0, Revision: 0, PassType: 0 } &&
                initial.Slots.All(row => row.Count == 0 && !row.Ready && !row.Completed),
                "正常读取先初始化零XP、零进度及零claim赛季，未制造完成事实");
            string beforePremature = Snapshot();
            That(!session.ClaimBattlePassMission(now, 1, 12, 0, 0).Accepted && Snapshot() == beforePremature,
                "未达日任务拒绝不派生完成事实");
            var running = repository.CommitAdventureExpedition(owner,
                Prepare(repository.ReadAdventure(owner, adventure, now), now), adventure, now);
            for (int i = 1; i <= 2; i++)
            {
                var at = now.AddSeconds(i);
                running = repository.CommitAdventureExpedition(owner,
                    service.Finish(running, 1, false, at).Plan! with { ActorId = role.Id }, adventure, at);
                running = repository.CommitAdventureExpedition(owner, Prepare(running, at), adventure, at);
            }
            var ready = Read(repository, owner, now);
            That(Find(ready, 0, 12).Ready && Find(ready, 1, 38).Ready &&
                Find(ready, 2, 54).Count == 0 && Find(ready, 2, 55).Count == 0 && ready.Season.TotalExperience == 0,
                "三个真实远征GUID使日周达标但未领XP不计季54或55");
            var selected = session.Selected;
            string beforeFault = Snapshot();
            Sql($"""
                CREATE TRIGGER fail_completion AFTER INSERT ON account_battle_pass_mission_progress
                WHEN NEW.account='{account}' AND NEW.mission_id=54 AND NEW.current_count=1
                BEGIN
                  SELECT CASE WHEN NEW.category=2
                    AND (SELECT total_experience FROM account_battle_pass_seasons WHERE account=NEW.account AND season_key=NEW.season_key)=100
                    AND (SELECT revision FROM account_battle_pass_seasons WHERE account=NEW.account AND season_key=NEW.season_key)=4
                    AND (SELECT count(*) FROM account_battle_pass_mission_claims WHERE account=NEW.account AND mission_id=12 AND experience=100 AND resulting_season_revision=4)=1
                    AND (SELECT count(*) FROM account_battle_pass_source_events WHERE account=NEW.account)=3
                    AND (SELECT current_count FROM account_battle_pass_mission_progress WHERE account=NEW.account AND mission_id=54)=1
                  THEN RAISE(ABORT,'{marker}') ELSE RAISE(ABORT,'BP completion prerequisites missing') END;
                END;
                """);
            bool reached = false;
            try { session.ClaimBattlePassMission(now, 1, 12, 0, 0); }
            catch (SqliteException error) when (Exact(error, marker)) { reached = true; }
            That(reached && Snapshot() == beforeFault && ReferenceEquals(session.Selected, selected),
                "原XP、唯一claim、季revision和季54均已写后的准确late故障整笔回滚");
            Sql("DROP TRIGGER fail_completion");
            var daily = session.ClaimBattlePassMission(now, 1, 12, 0, uint.MaxValue);
            That(daily.Accepted && daily.State!.Season is { TotalExperience: 100, Revision: 4 } &&
                Find(daily.State!, 2, 54) is { Count: 1, Ready: false, Completed: false } &&
                Find(daily.State!, 2, 55).Count == 0,
                "同Session重试日XPclaim只推进54一次，不自动XP或另加季revision");
            string afterDaily = Snapshot();
            That(!session.ClaimBattlePassMission(now, 1, 12, 0, 3).Accepted && Snapshot() == afterDaily,
                "日claim重放不重复累计54或发XP");
            Sql($"""
                CREATE TRIGGER fail_weekly_completion AFTER INSERT ON account_battle_pass_mission_progress
                WHEN NEW.account='{account}' AND NEW.mission_id=55 AND NEW.current_count=1
                BEGIN
                  SELECT CASE WHEN NEW.category=2
                    AND (SELECT total_experience FROM account_battle_pass_seasons WHERE account=NEW.account AND season_key=NEW.season_key)=1100
                    AND (SELECT revision FROM account_battle_pass_seasons WHERE account=NEW.account AND season_key=NEW.season_key)=5
                    AND (SELECT count(*) FROM account_battle_pass_mission_claims WHERE account=NEW.account)=2
                    AND (SELECT current_count FROM account_battle_pass_mission_progress WHERE account=NEW.account AND mission_id=54)=1
                    AND (SELECT current_count FROM account_battle_pass_mission_progress WHERE account=NEW.account AND mission_id=55)=1
                  THEN RAISE(ABORT,'{marker}') ELSE RAISE(ABORT,'BP weekly completion prerequisites missing') END;
                END;
                """);
            bool weeklyReached = false;
            try { session.ClaimBattlePassMission(now, 1, 38, 1, 0); }
            catch (SqliteException error) when (Exact(error, marker)) { weeklyReached = true; }
            That(weeklyReached && Snapshot() == afterDaily && ReferenceEquals(session.Selected, selected),
                "周XP、唯一claim及季55均已写后的准确late故障整笔回滚");
            Sql("DROP TRIGGER fail_weekly_completion");
            var weekly = session.ClaimBattlePassMission(now, 1, 38, 1, 0);
            That(weekly.Accepted && weekly.State!.Season is { TotalExperience: 1100, Revision: 5 } &&
                Find(weekly.State!, 2, 54).Count == 1 && Find(weekly.State!, 2, 55) is { Count: 1, Ready: false, Completed: false },
                "真实周38首领1000XP只推进55一次，54保持且无多余revision");
            string afterWeekly = Snapshot();
            That(!session.ClaimBattlePassMission(now, 1, 38, 1, 2).Accepted && Snapshot() == afterWeekly,
                "周claim重放不重复累计55或发XP");
            var restoredRepository = new CharacterRepository(path); restoredRepository.ConfigureBattlePassMissions(policy);
            var restoredSession = new GameSession(restoredRepository, towns, owner, timeProvider: new MissionClock(now));
            restoredSession.Select(role.Slot); restoredSession.MarkReady();
            var restored = restoredSession.ReadBattlePassMissionState(now)!.State;
            That(restored.ToPayload().SequenceEqual(weekly.State!.ToPayload()) && restored.Season.TotalExperience == 1100 &&
                restored.Season.Revision == 5 && ReferenceEquals(session.Selected, selected),
                "新Repository和Session恢复两完成计数及唯一claim，不换原角色资产快照");
            var tomorrow = Start.AddDays(59).AddHours(1);
            clock.Now = tomorrow;
            running = repository.ReadAdventure(owner, adventure, tomorrow);
            running = repository.CommitAdventureExpedition(owner,
                service.Finish(running, 1, false, tomorrow).Plan! with { ActorId = role.Id }, adventure, tomorrow);
            repository.CommitAdventureExpedition(owner, Prepare(running, tomorrow), adventure, tomorrow);
            var next = session.ClaimBattlePassMission(tomorrow, 1, 12, 0, 0);
            That(next.Accepted && Find(next.State!, 2, 54).Count == 2 && Find(next.State!, 2, 55).Count == 1 &&
                next.State!.Season is { TotalExperience: 1200, Revision: 7 },
                "次日同定义首次XPclaim是独立日事实，季累计54保留不重置");
            // 仅定向Core在隔离库预置阈值边界，不冒真实150次或30次任务来源。
            Sql("UPDATE account_battle_pass_mission_progress SET current_count=149 WHERE account=$p0 AND mission_id=54", account);
            Sql("UPDATE account_battle_pass_mission_progress SET current_count=30 WHERE account=$p0 AND mission_id=55", account);
            // 开始下一真实有日12的周8日58的独立账号，固定高级资格拒绝及目标封顶。
            CappedAndPremium();
            // 季任务55自身领取不递归增加54，季54自身领取也不改55。
            var seasonal55 = session.ClaimBattlePassMission(tomorrow, 1, 55, 2, 0);
            That(seasonal55.Accepted && Find(seasonal55.State!, 2, 54).Count == 149 &&
                Find(seasonal55.State!, 2, 55) is { Count: 30, Completed: true },
                "赛季55领取1600XP不递归累计日54");
            Sql("UPDATE account_battle_pass_mission_progress SET current_count=150 WHERE account=$p0 AND mission_id=54", account);
            var seasonal54 = session.ClaimBattlePassMission(tomorrow, 1, 54, 2, 17);
            That(seasonal54.Accepted && Find(seasonal54.State!, 2, 54) is { Count: 150, Completed: true } &&
                Find(seasonal54.State!, 2, 55).Count == 30,
                "赛季54领取自身1600XP不递归累计周55");
            string afterSeason = Snapshot();
            clock.Now = policy.End;
            That(!session.ClaimBattlePassMission(policy.End, 1, 12, 0, 0).Accepted && Snapshot() == afterSeason,
                "季结束拒绝XPclaim及任何派生完成累计");
            That(Query("PRAGMA integrity_check").Single().Single() is "ok" && Query("PRAGMA foreign_key_check").Length == 0,
                "派生事务、晚故障和重建后真实SQLite完整性及外键保持");
            restoredSession.End(); session.End(); succeeded = true;

            void CappedAndPremium()
            {
                const string cappedAccount = "bp-completion-cap";
                var at = Start.AddDays(14).AddHours(1);
                new AuthenticationService(new AuthenticationRepository(path)).Create(cappedAccount, "fixture-password");
                using var cappedOwner = owners.Acquire(cappedAccount, () => { });
                var hero = repository.Create(cappedOwner, new NewCharacter("CompleteCap", point, null) { GrowType = 33, Level = 95 });
                repository.InitializeAdventure(cappedOwner, adventure, at);
                var plan = service.Start(repository.ReadAdventure(cappedOwner, adventure, at), 1, 21600, [(0, 0, 1)], at).Plan! with { ActorId = hero.Id };
                repository.CommitAdventureExpedition(cappedOwner, plan, adventure, at);
                var premiumClock = new MissionClock(at);
                var premiumSession = new GameSession(repository, towns, cappedOwner, timeProvider: premiumClock);
                premiumSession.Select(hero.Slot); premiumSession.MarkReady();
                string beforePremium = Snapshot();
                That(!premiumSession.ClaimBattlePassMission(at, 1, 12, 0, 3).Accepted && Snapshot() == beforePremium,
                    "高级日槽真实达标但未购买资格时，拒绝且不累计54");
                // 隔离Core受信资格和149阈值夹具，真实XPclaim/去重仍走Session及Repository。
                Sql("UPDATE account_battle_pass_seasons SET pass_type=1,revision=revision+1 WHERE account=$p0 AND season_key=$p1", cappedAccount, season);
                Sql("INSERT INTO account_battle_pass_mission_progress VALUES($p0,$p1,2,$p2,54,149)", cappedAccount, season, Start.ToUnixTimeSeconds());
                var cap = premiumSession.ClaimBattlePassMission(at, 1, 12, 0, 3);
                That(cap.Accepted && cap.State!.Season.TotalExperience == 300 &&
                    Find(cap.State!, 2, 54) is { Count: 150, Ready: true, Completed: false },
                    "高级日首领原300XP使149→150封顶，仅Ready不自动领取季1600XP");
                string capped = Snapshot();
                That(!premiumSession.ClaimBattlePassMission(at, 1, 12, 0, 0).Accepted && Snapshot() == capped,
                    "达上限后原claim重放不再累计54");
                var later = now;
                premiumClock.Now = later;
                var capRunning = repository.ReadAdventure(cappedOwner, adventure, later);
                for (int i = 0; i < 3; i++)
                {
                    var tick = later.AddSeconds(i);
                    capRunning = repository.CommitAdventureExpedition(cappedOwner,
                        service.Finish(capRunning, 1, false, tick).Plan! with { ActorId = hero.Id }, adventure, tick);
                    var nextPlan = service.Start(capRunning, 1, 21600, [(0, 0, 1)], tick).Plan! with { ActorId = hero.Id };
                    capRunning = repository.CommitAdventureExpedition(cappedOwner, nextPlan, adventure, tick);
                }
                var laterDaily = premiumSession.ClaimBattlePassMission(later, 1, 12, 0, 1);
                That(laterDaily.Accepted && laterDaily.State!.Season.TotalExperience == 400 &&
                    Find(laterDaily.State!, 2, 54) is { Count: 150, Ready: true, Completed: false },
                    "后续独立日claim仍可领原100XP而季54保持150不越界");
                Sql("INSERT INTO account_battle_pass_mission_progress VALUES($p0,$p1,2,$p2,55,30)", cappedAccount, season, Start.ToUnixTimeSeconds());
                var laterWeekly = premiumSession.ClaimBattlePassMission(later, 1, 38, 1, 2);
                That(laterWeekly.Accepted && laterWeekly.State!.Season.TotalExperience == 1400 &&
                    Find(laterWeekly.State!, 2, 55) is { Count: 30, Ready: true, Completed: false },
                    "后续独立周claim仍可领原1000XP而季55保持30不越界");
                premiumSession.End();
            }
            AdventureExpeditionPlan Prepare(AdventureSnapshot snapshot, DateTimeOffset at) =>
                (service.Start(snapshot, 1, 21600, [(0, 0, 1)], at).Plan ?? throw new InvalidOperationException("真实远征规划前提未满足"))
                with { ActorId = role.Id };
            SqliteConnection Open()
            {
                var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, ForeignKeys = true }.ToString());
                db.Open(); return db;
            }
            void Sql(string sql, params object[] values)
            {
                using var db = Open(); using var command = db.CreateCommand(); command.CommandText = sql;
                for (int i = 0; i < values.Length; i++) command.Parameters.AddWithValue("$p" + i, values[i]);
                command.ExecuteNonQuery();
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
                "SELECT * FROM account_adventure_expeditions ORDER BY account,area",
                "SELECT * FROM account_adventure_expedition_members ORDER BY 1,2,3", "SELECT * FROM account_adventure_receipts ORDER BY 1,2",
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
            else Console.Error.WriteLine($"战令完成来源回归失败，隔离现场保留于：{root}");
        }
    }
}
