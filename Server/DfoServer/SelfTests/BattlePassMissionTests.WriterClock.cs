using System.Reflection;
using System.Runtime.ExceptionServices;
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
    public static void RunWriterClockCore()
    {
        int before = Count;
        WriterClockPersistence();
        Console.WriteLine($"PASS BattlePassMissionTests writer clock ({Count - before} checks)");
    }

    static void WriterClockPersistence()
    {
        var day = Start.AddDays(59).AddMinutes(-1);
        var week = Start.AddDays(63).AddMinutes(-1);
        Scenario("day", Start, 15, day, day.AddMinutes(1), 12, 0, false);
        Scenario("week", Start, 15, week, week.AddMinutes(1), 38, 1, false);
        Scenario("season", Start, 9, week, week.AddMinutes(1), 38, 1, false);
        Scenario("same", Start, 15, day, day.AddSeconds(30), 12, 0, true);
        Scenario("rollback", Start, 15, day, day.AddSeconds(-1), 12, 0, false);
        // 真实系统时钟的当前周对齐到合成目录第8周，保持该周真实远征任务38。
        var systemNow = TimeProvider.System.GetUtcNow().AddSeconds(-5);
        var local = systemNow.ToOffset(TimeSpan.FromHours(8));
        var today = new DateTimeOffset(local.Year, local.Month, local.Day, 6, 0, 0, local.Offset);
        if (today > local) today = today.AddDays(-1);
        int sinceThursday = ((int)today.DayOfWeek - (int)DayOfWeek.Thursday + 7) % 7;
        var systemStart = today.AddDays(-sinceThursday - 56);
        Scenario("system", systemStart, 15, systemNow, null, 38, 1, true);

        static void Scenario(string kind, DateTimeOffset start, int weeks, DateTimeOffset sampledAt,
            DateTimeOffset? changedAt, ushort key, uint category, bool expectedAccepted)
        {
            string root = Path.Combine(Path.GetTempPath(), "S5A9-pass-clock-" + kind + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            bool succeeded = false;
            try
            {
                string path = Path.Combine(root, "game.db"), account = "bp-clock-" + kind;
                var policy = new BattlePassMissionPolicy("clock-" + kind, start, Catalog(weeks, completion: true));
                if (kind == "season") That(policy.End == changedAt, "季末场景使用目录真实结束边界");
                var adventure = AdventureCoreTests.SyntheticCatalog();
                var service = new AdventureExpeditionService(adventure, AdventureExpeditionTests.SyntheticCatalog());
                var repository = new CharacterRepository(path); repository.ConfigureBattlePassMissions(policy);
                new AuthenticationService(new AuthenticationRepository(path)).Create(account, "fixture-password");
                var owners = new AccountOwners(); using var owner = owners.Acquire(account, () => { });
                var point = new Location(1, 0, 10, 10);
                var role = repository.Create(owner, new NewCharacter("Cl" + kind, point, null) { GrowType = 33, Level = 95 });
                repository.InitializeAdventure(owner, adventure, sampledAt);
                var running = repository.ReadAdventure(owner, adventure, sampledAt);
                for (int i = 0; i < 3; i++)
                {
                    var at = sampledAt.AddSeconds(i);
                    if (i > 0)
                        running = repository.CommitAdventureExpedition(owner,
                            service.Finish(running, 1, false, at).Plan! with { ActorId = role.Id }, adventure, at);
                    var plan = service.Start(running, 1, 21600, [(0, 0, 1)], at).Plan! with { ActorId = role.Id };
                    running = repository.CommitAdventureExpedition(owner, plan, adventure, at);
                }
                var towns = new TownCatalog(new Dictionary<(byte, byte), TownArea>(), point, point,
                    new TownDock(0, [], []), [], new Dictionary<uint, uint[]>());
                var controlled = new WriterMissionClock(sampledAt);
                TimeProvider clock = changedAt is null ? TimeProvider.System : controlled;
                var session = new GameSession(repository, towns, owner, timeProvider: clock);
                session.Select(role.Slot); session.MarkReady();
                var selected = session.Selected;
                var state = Read(repository, owner, sampledAt);
                var slot = Find(state, checked((byte)category), key);
                That(slot is { Ready: true, Completed: false } && state.Season is { TotalExperience: 0, Revision: 3 } &&
                    Number("SELECT count(*) FROM account_battle_pass_source_events") == 3 &&
                    Number("SELECT count(*) FROM account_battle_pass_mission_claims") == 0 &&
                    Number("SELECT count(*) FROM account_battle_pass_mission_progress WHERE mission_id IN(54,55)") == 0,
                    kind + "：三个真实远征GUID建立Ready，零XP、claim及完成派生，无直SQL预种");
                string before = Snapshot();
                var gate = (SemaphoreSlim)(typeof(SqliteWriteGate).GetField("Gate", BindingFlags.Static | BindingFlags.NonPublic)!
                    .GetValue(null) ?? throw new InvalidOperationException("原writer闸门缺失"));
                var waiters = typeof(SemaphoreSlim).GetField("m_waitCount", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? typeof(SemaphoreSlim).GetField("_waitCount", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new InvalidOperationException("运行时缺少可观测的原writer等待计数");
                BattlePassMissionSessionResult? result = null; Exception? failure = null;
                using var completed = new ManualResetEventSlim();
                var worker = new Thread(() =>
                {
                    try { result = session.ClaimBattlePassMission(sampledAt, 1, key, category, uint.MaxValue); }
                    catch (Exception error) { failure = error; }
                    finally { completed.Set(); }
                }) { IsBackground = true, Name = "BP-clock-" + kind };
                bool started = false;
                try
                {
                    using var db = Open();
                    using var write = db.BeginWrite("BattlePassMissionTests.WriterClockHold");
                    using var tx = write.Transaction;
                    That(gate.CurrentCount == 0 && SqliteWriteGate.ActiveWriters == 1, kind + "：真实原writer已持有");
                    worker.Start(); started = true;
                    bool queued = SpinWait.SpinUntil(() => Convert.ToInt32(waiters.GetValue(gate)) >= 1 || completed.IsSet,
                        TimeSpan.FromSeconds(5));
                    That(queued && !completed.IsSet && Convert.ToInt32(waiters.GetValue(gate)) >= 1,
                        kind + "：真实领取线程已在原SemaphoreSlim写入闸门排队");
                    if (changedAt is { } after) controlled.Set(after);
                    That(!completed.IsSet && (changedAt is null || clock.GetUtcNow() == changedAt),
                        kind + "：实际排队后才推进/回退时钟，尚未释放writer");
                }
                finally
                {
                    if (started && (!completed.Wait(TimeSpan.FromSeconds(10)) || !worker.Join(TimeSpan.FromSeconds(5))))
                        throw new TimeoutException("释放原writer后领取线程未在截止内退出");
                }
                if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
                That(result is not null && result.Accepted == expectedAccepted && ReferenceEquals(session.Selected, selected),
                    kind + "：领取结果符合writer内有效期且保原角色资产引用");
                int derived = category == 0 ? 54 : 55;
                long xp = category == 0 ? 100 : 1000;
                if (expectedAccepted)
                {
                    long committedAt = Number("SELECT claimed_at FROM account_battle_pass_mission_claims");
                    That(result is { Receipt: not null, State.Season.Revision: 4 } &&
                        result.State.Season.TotalExperience == xp && Number("SELECT count(*) FROM account_battle_pass_mission_claims") == 1 &&
                        Number("SELECT current_count FROM account_battle_pass_mission_progress WHERE mission_id=" + derived) == 1 &&
                        (changedAt is { } at ? committedAt == at.ToUnixTimeSeconds() :
                            committedAt >= sampledAt.ToUnixTimeSeconds() && committedAt <= clock.GetUtcNow().ToUnixTimeSeconds()),
                        kind + "：同周期一次XP、唯一claim、一次派生及季版本，claim时间使用writer内时刻");
                    string committed = Snapshot();
                    That(!session.ClaimBattlePassMission(clock.GetUtcNow(), 1, key, category, 0).Accepted && Snapshot() == committed,
                        kind + "：同会话重复领取不改原事实");
                }
                else
                    That(result is { Receipt: null } && Snapshot() == before,
                        kind + "：跨周期/季末/时钟回退拒绝，完整数据库事实保持原样");
                string beforeRestore = Snapshot();
                var recovered = new CharacterRepository(path); recovered.ConfigureBattlePassMissions(policy);
                var restored = Read(recovered, owner, clock.GetUtcNow());
                That(restored.Season.TotalExperience == (expectedAccepted ? xp : 0) && Snapshot() == beforeRestore &&
                    Query("PRAGMA integrity_check").Single().Single() is "ok" && Query("PRAGMA foreign_key_check").Length == 0,
                    kind + "：新Repository权威恢复与SQLite完整性保持，无额外写入");
                session.End(); succeeded = true;

                SqliteConnection Open()
                {
                    var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, ForeignKeys = true }.ToString());
                    db.Open(); return db;
                }
                object?[][] Query(string sql)
                {
                    using var db = Open(); using var command = db.CreateCommand(); command.CommandText = sql;
                    using var reader = command.ExecuteReader(); var rows = new List<object?[]>();
                    while (reader.Read())
                    {
                        var row = new object?[reader.FieldCount];
                        for (int i = 0; i < row.Length; i++) row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                        rows.Add(row);
                    }
                    return rows.ToArray();
                }
                long Number(string sql) => Convert.ToInt64(Query(sql).Single().Single());
                string Snapshot() => JsonSerializer.Serialize(new[] {
                    "SELECT * FROM characters ORDER BY id", "SELECT * FROM character_inventory_items ORDER BY item_uid",
                    "SELECT * FROM character_inventory_slots ORDER BY character_id,list_type,slot_index",
                    "SELECT * FROM inventory_avatar_details ORDER BY item_uid", "SELECT * FROM inventory_creature_details ORDER BY item_uid",
                    "SELECT * FROM account_wallets ORDER BY account", "SELECT * FROM account_currency_balances ORDER BY account,currency_kind",
                    "SELECT * FROM inventory_audit_log ORDER BY audit_id", "SELECT * FROM currency_audit_log ORDER BY audit_id",
                    "SELECT * FROM epic_piece_audit_log ORDER BY audit_id", "SELECT * FROM account_epic_books ORDER BY account",
                    "SELECT * FROM account_adventures ORDER BY account", "SELECT * FROM account_adventure_expeditions ORDER BY account,area",
                    "SELECT * FROM account_adventure_expedition_members ORDER BY 1,2,3", "SELECT * FROM account_adventure_receipts ORDER BY 1,2",
                    "SELECT * FROM battle_pass_season_rules ORDER BY season_key", "SELECT * FROM account_battle_pass_seasons ORDER BY account,season_key",
                    "SELECT * FROM account_battle_pass_source_events ORDER BY account,source_kind,source_id",
                    "SELECT * FROM account_battle_pass_mission_progress ORDER BY account,season_key,category,period_key,mission_id",
                    "SELECT * FROM account_battle_pass_mission_claims ORDER BY account,season_key,category,period_key,mission_id",
                    "SELECT * FROM account_battle_pass_claims ORDER BY account,season_key,level,track", "SELECT * FROM sqlite_sequence ORDER BY name"
                }.Select(Query).ToArray());
            }
            finally
            {
                if (succeeded) TestFiles.DeleteDirectory(root, recursive: true);
                else Console.Error.WriteLine($"战令writer时钟回归失败，隔离现场保留于：{root}");
            }
        }
    }

    sealed class WriterMissionClock(DateTimeOffset now) : TimeProvider
    {
        long ticks = now.UtcTicks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);
        public void Set(DateTimeOffset value) => Interlocked.Exchange(ref ticks, value.UtcTicks);
    }
}
