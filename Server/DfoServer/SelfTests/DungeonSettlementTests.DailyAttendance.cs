using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Events;
using DfoServer.Game.Settings;
using DfoServer.GameWorld.OnlineEvents;
using DfoServer.Persistence;
using DfoServer.Testing;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class DungeonSettlementTests
{
    internal static void RunDailyAttendanceResources(TcpTestResources resources)
    {
        var catalog = DailyAttendanceCatalog.Load(resources.Archive, resources.ItemResources.Items);
        That(catalog.Daily.Length == 28 && catalog.Daily[0] is { ItemId: 490013043, Count: 2 } &&
            catalog.Accumulated.Select(row => row.RequiredDays).SequenceEqual(new[] { 7, 14, 21 }) &&
            catalog.Accumulated.Select(row => row.ItemId).SequenceEqual(new[] { 490013064, 490013065, 490013066 }),
            "本版真实PVF签到28次及三档累计奖励");
        string root = Path.Combine(Path.GetTempPath(), "S5A9-attendance-settlement-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "game.db");
        try
        {
            var repository = new CharacterRepository(path);
            using var owner = new AccountOwners().Acquire("attendance-clear", () => { });
            var role = repository.Create(owner, Fixture("AttendClear"));
            var events = new ServerEventService(new ServerEventRepository(path));
            var service = new DailyAttendanceService(repository, catalog, resources.ItemResources.Items, events, new GameDebugSettings());
            That(service.Prepare(owner, false) is { ActivityRevision: 0 } &&
                Number("SELECT count(*) FROM server_events WHERE event_id=2370") == 0,
                "无配置默认开启，准备签到不补写开关记录");
            for (int clear = 0; clear < 2; clear++)
            {
                var (run, authority) = CreateRun(role);
                run.AcceptConfirmedDeath(authority.Death(run.Snapshot.Identity, 20));
                var prepared = run.PrepareSettlement(run.ClearFact!, new byte[] { 1, 2, 3 }, Reward).Pending!;
                prepared.FreezeDailyAttendance(service.Prepare(owner, false));
                if (clear == 1)
                {
                    Sql("CREATE TRIGGER fail_daily_clear AFTER INSERT ON account_daily_attendance_claims BEGIN SELECT RAISE(ABORT,'late attendance failure'); END");
                    Reject<SqliteException>(() => repository.CommitDungeonSettlement(owner, role, run, prepared, resources.Growth),
                        "真实结算经验、receipt及邮件写入后签到末段故障");
                    That(SkillRepositoryTests.Equal(repository.List(owner).Single(), role) &&
                        service.Read(owner) is { Clears: 1, Total: 0 } && Number("SELECT count(*) FROM mailbox_messages") == 0 &&
                        Number("SELECT count(*) FROM character_dungeon_settlement_receipts") == 1,
                        "签到故障使整笔通关经验、revision、receipt和邮件原子回滚");
                    Sql("DROP TRIGGER fail_daily_clear");
                }
                var result = repository.CommitDungeonSettlement(owner, role, run, prepared, resources.Growth);
                var replay = repository.CommitDungeonSettlement(owner, role, run, prepared, resources.Growth);
                That(!result.Duplicate && replay.Duplicate && service.Read(owner).Clears == clear + 1,
                    "原结算回执去重签到次数，同报告只发一次");
                role = result.Character;
            }
            var mail = repository.LoadMailbox(owner, role.Id, DateTimeOffset.UtcNow.ToUnixTimeSeconds()).Entries.Single();
            That(service.Read(owner) is { Total: 1, Clears: 2 } && mail.Attachments is [var item] &&
                item.Item.Core.ItemId == 490013043 && item.Item.Core.Count == 2 && item.Item.Uid > 0,
                "真实每日奖励从当前PVF按完整实例进入系统邮件");
            That(new CharacterRepository(path).ReadDailyAttendance(owner, service.Read(owner).Day) == service.Read(owner),
                "新仓储重开恢复签到事实和原物理邮件");
        }
        finally { TestFiles.DeleteDirectory(root, true); }

        void Sql(string sql)
        {
            using var db = new SqliteConnection("Data Source=" + path); db.Open();
            using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
        }
        long Number(string sql)
        {
            using var db = new SqliteConnection("Data Source=" + path); db.Open();
            using var command = db.CreateCommand(); command.CommandText = sql; return (long)command.ExecuteScalar()!;
        }
    }
}
