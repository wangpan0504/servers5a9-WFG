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
    static void Persistence()
    {
        string root = Path.Combine(Path.GetTempPath(), "S5A9-pass-mission-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        bool succeeded = false;
        try
        {
            string path = Path.Combine(root, "game.db");
            const string account = "bp-mission-core", season = "core-missions";
            const string sourceMarker = "BP Core source complete transaction reached";
            const string claimMarker = "BP Core task XP claim reached";
            var now = Start.AddDays(58).AddHours(1);
            var policy = new BattlePassMissionPolicy(season, Start, Catalog());
            var adventure = AdventureCoreTests.SyntheticCatalog();
            var service = new AdventureExpeditionService(adventure, AdventureExpeditionTests.SyntheticCatalog());
            var repository = new CharacterRepository(path);
            repository.ConfigureBattlePassMissions(policy);
            new AuthenticationService(new AuthenticationRepository(path)).Create(account, "fixture-password");
            var owners = new AccountOwners();
            using var owner = owners.Acquire(account, () => { });
            var point = new Location(1, 0, 10, 10);
            var role = repository.Create(owner, new NewCharacter("PassSource", point, null) { GrowType = 33, Level = 95 });
            repository.InitializeAdventure(owner, adventure, now);
            var towns = new TownCatalog(new Dictionary<(byte, byte), TownArea>(), point, point,
                new TownDock(0, [], []), [], new Dictionary<uint, uint[]>());
            var clock = new MissionClock(now);
            var session = new GameSession(repository, towns, owner, timeProvider: clock);
            session.Select(role.Slot); session.MarkReady();
            var initial = Read(repository, owner, now);
            That(initial.Active && initial.Season.TotalExperience == 0 && initial.Season.PassType == 0 &&
                initial.Slots.All(row => row.Count == 0 && !row.Ready && !row.Completed),
                "真实Repository创建零进度的26真key，未制造高级或任务经验");
            var original = Prepare(repository.ReadAdventure(owner, adventure, now), now);
            string beforeSource = Snapshot();
            Sql($"""
                CREATE TRIGGER fail_core_mission_source AFTER UPDATE OF revision ON account_battle_pass_seasons
                WHEN NEW.account='{account}' AND NEW.season_key='{season}'
                BEGIN
                  SELECT CASE WHEN NEW.revision=1 AND NEW.total_experience=0
                    AND (SELECT count(*) FROM account_adventure_expeditions WHERE account=NEW.account AND run_id='{original.After.Expeditions.Single().Id:N}')=1
                    AND (SELECT count(*) FROM account_adventure_expedition_members WHERE account=NEW.account)=1
                    AND (SELECT count(*) FROM account_adventure_receipts WHERE account=NEW.account AND action='expedition_start')=1
                    AND (SELECT count(*) FROM account_battle_pass_source_events WHERE account=NEW.account)=1
                    AND (SELECT count(*) FROM account_battle_pass_mission_progress WHERE account=NEW.account AND mission_id IN(12,38) AND current_count=1)=2
                  THEN RAISE(ABORT,'{sourceMarker}') ELSE RAISE(ABORT,'BP Core source prerequisites missing') END;
                END;
                """);
            bool sourceLate = false;
            try { repository.CommitAdventureExpedition(owner, original, adventure, now); }
            catch (SqliteException error) when (Exact(error, sourceMarker)) { sourceLate = true; }
            That(sourceLate && Snapshot() == beforeSource,
                "唯一late marker已见原run/member/receipt、真实GUID、两任务进度及季revision后整笔远征回滚");
            Sql("DROP TRIGGER fail_core_mission_source");
            var running = repository.CommitAdventureExpedition(owner, original, adventure, now);
            var one = Read(repository, owner, now);
            That(Find(one, 0, 12) is { Count: 1, Ready: true, Completed: false } &&
                Find(one, 1, 38) is { Count: 1, Ready: false, Completed: false } &&
                one.Season is { TotalExperience: 0, Revision: 1 }, "同一真实开始只计一次日周进度，日达标而周未达，不自动发XP");
            string once = Snapshot();
            Reject<InvalidOperationException>(() => repository.CommitAdventureExpedition(owner, original, adventure, now),
                "原远征旧计划与GUID不能重放");
            That(Snapshot() == once, "重放不改原远征、任务source/progress、资产和季版本");
            for (int i = 1; i <= 2; i++)
            {
                var at = now.AddSeconds(i);
                running = repository.CommitAdventureExpedition(owner,
                    service.Finish(running, 1, false, at).Plan! with { ActorId = role.Id }, adventure, at);
                running = repository.CommitAdventureExpedition(owner, Prepare(running, at), adventure, at);
            }
            var three = Read(repository, owner, now);
            That(Find(three, 0, 12).Count == 1 && Find(three, 1, 38) is { Count: 3, Ready: true } &&
                three.Season is { Revision: 3, TotalExperience: 0 } && Scalar("SELECT count(*) FROM account_battle_pass_source_events WHERE account=$p0", account) is 3L,
                "三个不同真实GUID累计周目标3，取消不计量、日目标封顶，始终不自动授予任务XP");
            var selected = session.Selected;
            string beforeClaim = Snapshot();
            Sql($"""
                CREATE TRIGGER fail_core_mission_claim AFTER INSERT ON account_battle_pass_mission_claims
                WHEN NEW.account='{account}' AND NEW.mission_id=12
                BEGIN
                  SELECT CASE WHEN NEW.experience=100 AND NEW.resulting_season_revision=4
                    AND (SELECT total_experience FROM account_battle_pass_seasons WHERE account=NEW.account AND season_key=NEW.season_key)=100
                    AND (SELECT revision FROM account_battle_pass_seasons WHERE account=NEW.account AND season_key=NEW.season_key)=4
                    AND (SELECT count(*) FROM account_battle_pass_mission_claims WHERE account=NEW.account)=1
                    AND (SELECT count(*) FROM account_battle_pass_source_events WHERE account=NEW.account)=3
                  THEN RAISE(ABORT,'{claimMarker}') ELSE RAISE(ABORT,'BP Core task claim prerequisites missing') END;
                END;
                """);
            bool claimLate = false;
            try { session.ClaimBattlePassMission(now, 1, 12, 0, 1); }
            catch (SqliteException error) when (Exact(error, claimMarker)) { claimLate = true; }
            That(claimLate && Snapshot() == beforeClaim && ReferenceEquals(session.Selected, selected),
                "XP和唯一claim均已写后的准确late故障回滚全部事实，Session保持原选择引用");
            Sql("DROP TRIGGER fail_core_mission_claim");
            var daily = session.ClaimBattlePassMission(now, 1, 12, 0, uint.MaxValue);
            That(daily is { Accepted: true, Receipt: not null, State.Season.TotalExperience: 100 } &&
                Find(daily.State!, 0, 12) is { Completed: true, Ready: false },
                "同Session重试原日任务一次成功，viewRow不决定权威任务或XP");
            string afterDaily = Snapshot();
            That(!session.ClaimBattlePassMission(now, 1, 12, 0, 0).Accepted && Snapshot() == afterDaily,
                "改变视图行重放同日claim仍拒，不能多发XP");
            var weekly = session.ClaimBattlePassMission(now, 1, 38, 1, 2);
            That(weekly is { Accepted: true, State.Season.TotalExperience: 1100, State.Season.Revision: 5 } &&
                ReferenceEquals(session.Selected, selected), "真实周任务只增加原1000XP和一次季revision，角色资产快照不换");
            var restoredRepository = new CharacterRepository(path); restoredRepository.ConfigureBattlePassMissions(policy);
            var restoredSession = new GameSession(restoredRepository, towns, owner, timeProvider: new MissionClock(now));
            restoredSession.Select(role.Slot); restoredSession.MarkReady();
            var restored = restoredSession.ReadBattlePassMissionState(now)!.State;
            That(restored.Season.TotalExperience == 1100 && restored.ToPayload().SequenceEqual(weekly.State!.ToPayload()) &&
                JsonSerializer.Serialize(restoredSession.Selected!.Inventory) == JsonSerializer.Serialize(selected!.Inventory),
                "新Repository/Session恢复同季XP、26槽、两唯一任务claim及完整库存");
            var tomorrow = Start.AddDays(59).AddHours(1);
            clock.Now = tomorrow;
            string beforeOld = Snapshot();
            Reject<InvalidOperationException>(() => repository.CommitAdventureExpedition(owner, original, adventure, tomorrow),
                "旧GUID跨日不能通过原事务重放");
            That(Snapshot() == beforeOld && Find(Read(repository, owner, tomorrow), 0, 12).Count == 0 &&
                Find(Read(repository, owner, tomorrow), 1, 38).Completed,
                "下日同定义是零进度新实例，旧周claim保留，旧source不会回填新日");
            running = repository.ReadAdventure(owner, adventure, tomorrow);
            running = repository.CommitAdventureExpedition(owner,
                service.Finish(running, 1, false, tomorrow).Plan! with { ActorId = role.Id }, adventure, tomorrow);
            repository.CommitAdventureExpedition(owner, Prepare(running, tomorrow), adventure, tomorrow);
            That(Find(Read(repository, owner, tomorrow), 0, 12) is { Count: 1, Ready: true, Completed: false } &&
                Find(Read(repository, owner, tomorrow), 1, 38) is { Count: 3, Completed: true },
                "新日真实新GUID推进新日实例，已领周任务不重新打开");
            That(session.ClaimBattlePassMission(tomorrow, 1, 12, 0, 0).Accepted && Read(repository, owner, tomorrow).Season.TotalExperience == 1200 &&
                Read(repository, owner, Start.AddDays(63)).Slots.Skip(4).Take(4).All(row => row.Count == 0 && !row.Completed),
                "新日相同定义可独立领100XP，周界只生成真实新周实例而不迁移旧claim");
            // 仅隔离夹具预置赛季任务达标；不宣称已实现该任务的真实producer。
            Sql("INSERT INTO account_battle_pass_mission_progress VALUES($p0,$p1,2,$p2,51,2000000000)", account, season, Start.ToUnixTimeSeconds());
            var seasonClaim = session.ClaimBattlePassMission(tomorrow, 1, 51, 2, uint.MaxValue);
            string afterSeason = Snapshot();
            That(seasonClaim.Accepted && !session.ClaimBattlePassMission(tomorrow, 1, 51, 2, 0).Accepted &&
                !session.ClaimBattlePassMission(tomorrow, 1, 70, 2, 0).Accepted && Snapshot() == afterSeason,
                "赛季按active key授权，viewRow不是18槽索引或去重键，存在于目录但未激活的key70拒绝");
            Reject<InvalidDataException>(() => new CharacterRepository(path).ConfigureBattlePassMissions(
                new BattlePassMissionPolicy(season, Start.AddDays(7), Catalog())), "重开相同seasonKey不能静默移动冻结起点");
            Premium();
            That(Scalar("PRAGMA integrity_check") is "ok" && Query("PRAGMA foreign_key_check").Length == 0,
                "任务源、XPclaim、回滚和恢复保持SQLite及全部外键完整");
            restoredSession.End(); session.End();
            succeeded = true;

            AdventureExpeditionPlan Prepare(AdventureSnapshot snapshot, DateTimeOffset at) =>
                (service.Start(snapshot, 1, 21600, [(0, 0, 1)], at).Plan ?? throw new InvalidOperationException("真实远征规划前提未满足"))
                with { ActorId = role.Id };
            void Premium()
            {
                const string premiumAccount = "bp-mission-premium";
                var at = Start.AddDays(14).AddHours(1);
                new AuthenticationService(new AuthenticationRepository(path)).Create(premiumAccount, "fixture-password");
                using var premiumOwner = owners.Acquire(premiumAccount, () => { });
                var hero = repository.Create(premiumOwner, new NewCharacter("PassPremium", point, null) { GrowType = 33, Level = 95 });
                repository.InitializeAdventure(premiumOwner, adventure, at);
                var expedition = service.Start(repository.ReadAdventure(premiumOwner, adventure, at), 1, 21600, [(0, 0, 1)], at).Plan! with { ActorId = hero.Id };
                repository.CommitAdventureExpedition(premiumOwner, expedition, adventure, at);
                var premiumSession = new GameSession(repository, towns, premiumOwner, timeProvider: new MissionClock(at));
                premiumSession.Select(hero.Slot); premiumSession.MarkReady();
                var locked = Read(repository, premiumOwner, at);
                That(Find(locked, 0, 12) is { Count: 1, Ready: false } && locked.Season.TotalExperience == 0 &&
                    !premiumSession.ClaimBattlePassMission(at, 1, 12, 0, 3).Accepted,
                    "高级资格前真实source照记，当日高级槽达标仍不得提前领经验");
                // 隔离库受信资格夹具，不增加产品购买或GM赋资格接口。
                Sql("UPDATE account_battle_pass_seasons SET pass_type=2,revision=revision+1 WHERE account=$p0 AND season_key=$p1", premiumAccount, season);
                That(!premiumSession.ClaimBattlePassMission(at, 1, 12, 0, 3).Accepted,
                    "未知非零type2不能冒已识别高级资格");
                Sql("UPDATE account_battle_pass_seasons SET pass_type=1,revision=revision+1 WHERE account=$p0 AND season_key=$p1", premiumAccount, season);
                var paid = premiumSession.ClaimBattlePassMission(at, 1, 12, 0, 3);
                That(paid is { Accepted: true, State.Season.TotalExperience: 300 } && Find(paid.State!, 0, 12) is { Count: 1, Completed: true },
                    "受信type1后保留原真实行为，只领一次资源3倍300XP");
                premiumSession.End();
            }
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
            object? Scalar(string sql, params object[] values) => Query(sql, values).Single().Single();
            string Snapshot() => JsonSerializer.Serialize(new[] {
                "SELECT * FROM characters ORDER BY id", "SELECT * FROM character_inventory_items ORDER BY item_uid",
                "SELECT * FROM character_inventory_slots ORDER BY character_id,list_type,slot_index", "SELECT * FROM inventory_avatar_details ORDER BY item_uid",
                "SELECT * FROM inventory_creature_details ORDER BY item_uid", "SELECT * FROM account_wallets ORDER BY account",
                "SELECT * FROM account_currency_balances ORDER BY account,currency_kind", "SELECT * FROM inventory_audit_log ORDER BY audit_id",
                "SELECT * FROM currency_audit_log ORDER BY audit_id", "SELECT * FROM epic_piece_audit_log ORDER BY audit_id", "SELECT * FROM account_epic_books ORDER BY account",
                "SELECT * FROM account_adventures ORDER BY account", "SELECT * FROM account_adventure_expeditions ORDER BY account,area",
                "SELECT * FROM account_adventure_expedition_members ORDER BY 1,2,3", "SELECT * FROM account_adventure_receipts ORDER BY 1,2",
                "SELECT * FROM battle_pass_season_rules ORDER BY season_key", "SELECT * FROM account_battle_pass_seasons ORDER BY account,season_key",
                "SELECT * FROM account_battle_pass_source_events ORDER BY account,source_kind,source_id",
                "SELECT * FROM account_battle_pass_mission_progress ORDER BY account,season_key,category,period_key,mission_id",
                "SELECT * FROM account_battle_pass_mission_claims ORDER BY account,season_key,category,period_key,mission_id",
                "SELECT * FROM account_battle_pass_claims ORDER BY account,season_key,level,track", "SELECT * FROM sqlite_sequence ORDER BY name"
            }.Select(sql => Query(sql)).ToArray());
        }
        finally
        {
            if (succeeded) TestFiles.DeleteDirectory(root, recursive: true);
            else Console.Error.WriteLine($"战令任务回归失败，隔离现场保留于：{root}");
        }
    }
    static BattlePassMissionState Read(CharacterRepository repository, AccountOwners.Lease owner, DateTimeOffset now) =>
        repository.ReadBattlePassMissions(owner, now) ?? throw new InvalidOperationException("战令任务未配置");
    static BattlePassMissionSlot Find(BattlePassMissionState state, byte category, int id) =>
        state.Slots.Single(row => row.Instance.Key.Category == category && row.Instance.Key.Id == id);
    static bool Exact(SqliteException error, string marker) => error.SqliteErrorCode == 19 && error.Message == $"SQLite Error 19: '{marker}'.";
    sealed class MissionClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
