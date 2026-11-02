using System.Text.Json;
using System.Reflection;
using System.Runtime.ExceptionServices;
using DfoServer.Game.Accounts;
using DfoServer.Game.Auth;
using DfoServer.Game.BattlePass;
using DfoServer.Game.Characters;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Towns;
using DfoServer.Network.Parsers;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class BattlePassMissionTests
{
    static void NativeShopVisitPersistence()
    {
        string root = Path.Combine(Path.GetTempPath(), "S5A9-pass-shop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        bool succeeded = false;
        try
        {
            string path = Path.Combine(root, "game.db");
            const string account = "bp-shop-core", season = "core-shop", marker = "BP shop source late";
            var now = Start.AddDays(58).AddHours(1);
            var policy = new BattlePassMissionPolicy(season, Start, Catalog(shopVisit: true));
            var repository = new CharacterRepository(path); repository.ConfigureBattlePassMissions(policy);
            new AuthenticationService(new AuthenticationRepository(path)).Create(account, "fixture-password");
            var owners = new AccountOwners();
            using var owner = owners.Acquire(account, () => { });
            var point = new Location(1, 0, 10, 10);
            var role = repository.Create(owner, new NewCharacter("PassShop", point, null) { GrowType = 33, Level = 95 });
            var towns = new TownCatalog(new Dictionary<(byte, byte), TownArea>(), point, point,
                new TownDock(0, [], []), [], new Dictionary<uint, uint[]>());
            var clock = new MissionClock(now);
            var session = new GameSession(repository, towns, owner, timeProvider: clock);
            session.Select(role.Slot); session.MarkReady();
            var selected = session.Selected ?? throw new InvalidOperationException("选角后缺少权威角色快照");
            var initial = Read(repository, owner, now);
            That(Find(initial, 0, 11) is { Count: 0, Ready: false } && initial.Season.TotalExperience == 0,
                "访问前原日任务为0且没有XP");
            var first = BattlePassRequests.Mission(Convert.FromHexString("1CF71A00000000000000000000000B000100000000000000"));
            var second = BattlePassRequests.Mission(Convert.FromHexString("1CF71A00000000000000000000000B000200000000000000"));
            BattlePassMissionSessionResult Report(BattlePassMissionRequest request) => session.ClaimBattlePassMission(now,
                request.RawActionCode, request.RowKey, request.RawCategoryCode, request.RowIndex);
            string before = Snapshot();
            That(!repository.CommitBattlePassMission(owner, selected with { Revision = selected.Revision - 1 }, initial.Season.Revision,
                    now, 0, 11, 1, 0, clock).Accepted &&
                !repository.CommitBattlePassMission(owner, selected, initial.Season.Revision + 1,
                    now, 0, 11, 1, 0, clock).Accepted && Snapshot() == before,
                "旧角色版本和旧季版本均拒绝且不写入");
            WaitAcrossWriter(Start.AddDays(59).AddMinutes(-1), Start.AddDays(59), "跨日");
            WaitAcrossWriter(policy.End.AddMinutes(-1), policy.End, "季末");
            WaitAcrossWriter(now, now.AddSeconds(-1), "时钟回退");
            That(Snapshot() == before, "三种writer边界均不产生来源或进度");
            Sql($"""
                CREATE TRIGGER fail_shop_visit AFTER UPDATE OF revision ON account_battle_pass_seasons
                WHEN NEW.account='{account}' AND NEW.season_key='{season}'
                BEGIN
                  SELECT CASE WHEN NEW.revision=1 AND NEW.total_experience=0
                    AND (SELECT count(*) FROM account_battle_pass_source_events WHERE account=NEW.account AND source_kind='native_shop_visit')=1
                    AND (SELECT count(*) FROM account_battle_pass_mission_progress WHERE account=NEW.account AND category=0 AND mission_id=11 AND current_count=1)=1
                  THEN RAISE(ABORT,'{marker}') ELSE RAISE(ABORT,'BP shop prerequisites missing') END;
                END;
                """);
            bool late = false;
            try { Report(first); }
            catch (SqliteException error) when (Exact(error, marker)) { late = true; }
            That(late && Snapshot() == before, "来源和进度已写后的精确故障回滚完整状态");
            Sql("DROP TRIGGER fail_shop_visit");
            var one = Report(first);
            That(one is { Accepted: true, State.Season.TotalExperience: 0, State.Season.Revision: 1 } &&
                Find(one.State!, 0, 11) is { Count: 1, Ready: true, Completed: false },
                "原生action0报文只记录一次进度，不发XP");
            string once = Snapshot();
            That(Report(second).Accepted && Snapshot() == once, "原生第二次报告不重复来源、进度或版本");
            var restored = new CharacterRepository(path); restored.ConfigureBattlePassMissions(policy);
            var reconnected = new GameSession(restored, towns, owner, timeProvider: clock);
            reconnected.Select(role.Slot); reconnected.MarkReady();
            That(reconnected.ReadBattlePassMissionState(now)!.State.ToPayload().SequenceEqual(one.State!.ToPayload()) &&
                reconnected.ClaimBattlePassMission(now, second.RawActionCode, second.RowKey, second.RawCategoryCode, second.RowIndex).Accepted && Snapshot() == once,
                "重建Repository和Session恢复任务，重复访问保持状态");
            var claimed = reconnected.ClaimBattlePassMission(now, 1, 11, 0, 0);
            That(claimed is { Accepted: true, State.Season.TotalExperience: 100, State.Season.Revision: 2 } &&
                Find(claimed.State!, 0, 11) is { Completed: true }, "独立action1领取一次100XP");
            string afterClaim = Snapshot();
            That(!reconnected.ClaimBattlePassMission(now, 1, 11, 0, 0).Accepted && Snapshot() == afterClaim,
                "领取重放不重复XP或来源");
            var tomorrow = now.AddDays(1); clock.Now = tomorrow;
            var next = reconnected.ClaimBattlePassMission(tomorrow, first.RawActionCode, first.RowKey, first.RawCategoryCode, first.RowIndex);
            That(next is { Accepted: true, State.Season.TotalExperience: 100, State.Season.Revision: 3 } &&
                Find(next.State!, 0, 11) is { Count: 1, Ready: true }, "新日创建独立来源并保留已领XP");
            string afterNext = Snapshot();
            That(!reconnected.ClaimBattlePassMission(tomorrow, 0, 11, 1, 1).Accepted &&
                !reconnected.ClaimBattlePassMission(tomorrow, 0, 11, 0, 0).Accepted && Snapshot() == afterNext,
                "aux非零和报告数零不能修改状态");
            reconnected.End(); session.End();
            using var replacement = owners.Acquire(account, () => { });
            Reject<InvalidOperationException>(() => repository.CommitBattlePassMission(owner, selected, next.State!.Season.Revision,
                tomorrow, 0, 11, 1, 0, clock), "旧账号owner拒绝商城访问报告");
            That(Snapshot() == afterNext, "旧owner不能修改来源或资产");
            succeeded = true;

            void WaitAcrossWriter(DateTimeOffset sampled, DateTimeOffset released, string label)
            {
                var heldClock = new WriterMissionClock(sampled);
                var gate = (SemaphoreSlim)(typeof(SqliteWriteGate).GetField("Gate", BindingFlags.Static | BindingFlags.NonPublic)!
                    .GetValue(null) ?? throw new InvalidOperationException("原writer闸门缺失"));
                var waiters = typeof(SemaphoreSlim).GetField("m_waitCount", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? typeof(SemaphoreSlim).GetField("_waitCount", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new InvalidOperationException("运行时缺少writer等待计数");
                BattlePassMissionCommit? result = null; Exception? failure = null;
                using var completed = new ManualResetEventSlim();
                var worker = new Thread(() =>
                {
                    try { result = repository.CommitBattlePassMission(owner, selected, initial.Season.Revision, sampled, 0, 11, 1, 0, heldClock); }
                    catch (Exception error) { failure = error; }
                    finally { completed.Set(); }
                }) { IsBackground = true, Name = "BP-shop-clock" };
                bool started = false;
                try
                {
                    using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, ForeignKeys = true }.ToString());
                    db.Open(); using var write = db.BeginWrite("BattlePassMissionTests.ShopWriterHold"); using var tx = write.Transaction;
                    That(gate.CurrentCount == 0 && SqliteWriteGate.ActiveWriters == 1, label + "：原writer已持有");
                    worker.Start(); started = true;
                    bool queued = SpinWait.SpinUntil(() => Convert.ToInt32(waiters.GetValue(gate)) >= 1 || completed.IsSet, TimeSpan.FromSeconds(5));
                    That(queued && !completed.IsSet && Convert.ToInt32(waiters.GetValue(gate)) >= 1, label + "：访问报告确实等待writer");
                    heldClock.Set(released);
                }
                finally
                {
                    if (started && (!completed.Wait(TimeSpan.FromSeconds(10)) || !worker.Join(TimeSpan.FromSeconds(5))))
                        throw new TimeoutException("释放writer后商城访问线程未退出");
                }
                if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
                That(result is { Accepted: false } && Snapshot() == before, label + "：writer内重采拒绝，完整状态不变");
            }

            void Sql(string sql)
            {
                using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
                db.Open(); using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
            }
            string Snapshot()
            {
                using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
                db.Open();
                return JsonSerializer.Serialize(new[] { "characters", "account_wallets", "account_battle_pass_seasons",
                    "account_battle_pass_source_events", "account_battle_pass_mission_progress", "account_battle_pass_mission_claims" }
                    .Select(table => { using var command = db.CreateCommand(); command.CommandText = "SELECT * FROM " + table + " ORDER BY 1";
                        using var reader = command.ExecuteReader(); var rows = new List<object?[]>();
                        while (reader.Read()) { var row = new object?[reader.FieldCount];
                            for (int i = 0; i < row.Length; i++) row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                            rows.Add(row); } return rows; }).ToArray());
            }
        }
        finally
        {
            if (succeeded) TestFiles.DeleteDirectory(root, recursive: true);
            else Console.Error.WriteLine($"战令商城回归失败，隔离现场保留于：{root}");
        }
    }
}
