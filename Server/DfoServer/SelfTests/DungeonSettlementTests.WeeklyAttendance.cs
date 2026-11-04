using DfoServer.Game.Accounts;
using DfoServer.Game.Events;
using DfoServer.Persistence;
using DfoServer.Testing;
using DfoServer.Testing.Inventory;
using Microsoft.Data.Sqlite;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class DungeonSettlementTests
{
    internal static void RunWeeklyAttendanceResources(PvfArchive pvf)
    {
        string root = Path.Combine(Path.GetTempPath(), "S5A9-weekly-clear-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "game.db");
        try
        {
            var growth = SelfTestCatalogs.Growth(pvf);
            var repository = new CharacterRepository(path);
            var catalog = WeeklyAttendanceTests.Catalog();
            var clock = new DailyBoosterTestClock(DateTimeOffset.Parse("2026-11-03T04:00:00Z"));
            repository.ConfigureWeeklyAttendance(catalog, clock);
            new ServerEventService(new ServerEventRepository(path)).Override(2208, true);
            var owners = new AccountOwners();
            using var owner = owners.Acquire("weekly-clear", () => { });
            var initial = repository.Create(owner, Fixture("WeeklyClear"));
            var (run, authority) = CreateRun(initial);
            run.AcceptConfirmedDeath(authority.Death(run.Snapshot.Identity, 20));
            var prepared = run.PrepareSettlement(run.ClearFact!, [1, 2, 3], Reward).Pending!;
            int day = PcRoomTimePointDay.At(clock.GetUtcNow());
            prepared.FreezeDailyAttendance(new(repository.ReadDailyAttendance(owner, day), DailyAttendanceTests.Catalog,
                0, clock.GetUtcNow(), null, []));
            using (var db = Open())
            using (var command = db.CreateCommand())
            {
                command.CommandText = "CREATE TRIGGER weekly_clear_fail AFTER INSERT ON account_daily_attendance_days BEGIN SELECT RAISE(ABORT,'late clear'); END";
                command.ExecuteNonQuery();
            }
            Reject<SqliteException>(() => repository.CommitDungeonSettlement(owner, initial, run, prepared, growth, recommendedClear: true));
            That(repository.ReadWeeklyAttendanceSeason() is null && repository.List(owner).Single().Revision == initial.Revision &&
                repository.ReadDailyAttendance(owner, day).Clears == 0,
                "日签到末段失败回滚同笔通关的周赛季、日周计数和角色成长");
            using (var db = Open())
            using (var command = db.CreateCommand())
            {
                command.CommandText = "DROP TRIGGER weekly_clear_fail";
                command.ExecuteNonQuery();
            }
            var committed = repository.CommitDungeonSettlement(owner, initial, run, prepared, growth, recommendedClear: true);
            var season = repository.ReadWeeklyAttendanceSeason()!;
            That(repository.ReadWeeklyAttendance(owner, season, catalog).Clears[0] == 1 && !committed.Duplicate &&
                repository.ReadDailyAttendance(owner, day).Clears == 1,
                "真实原结算事务同时追加日周通关计数");
            var replay = repository.CommitDungeonSettlement(owner, initial, run, prepared, growth, recommendedClear: true);
            That(replay.Duplicate && repository.ReadWeeklyAttendance(owner, season, catalog).Clears[0] == 1 &&
                repository.ReadDailyAttendance(owner, day).Clears == 1,
                "原稳定结算回执恢复不重复增加日周签到计数");
            Reject<InvalidOperationException>(() => repository.CommitWeeklyAttendance(owner, initial, season, clock.GetUtcNow(),
                catalog, true, false, []), "旧角色revision不能领取或覆盖周进度");
            using var replacement = owners.Acquire("weekly-clear", () => { });
            Reject<InvalidOperationException>(() => repository.ReadWeeklyAttendance(owner, season, catalog), "旧账号generation拒绝访问周进度");
            That(repository.ReadWeeklyAttendance(replacement, season, catalog).Clears[0] == 1,
                "角色revision冲突与账号接管均保留原周计数");

            SqliteConnection Open()
            {
                var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
                db.Open(); return db;
            }
        }
        finally { TestFiles.DeleteDirectory(root, true); }
    }
}
