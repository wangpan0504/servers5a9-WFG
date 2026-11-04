using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Events;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.OnlineEvents;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Dungeons;
using DfoServer.Network.Builders;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class DailyAttendanceTests
{
    internal static DailyAttendanceCatalog Catalog { get; } = new(
        Enumerable.Range(0, 28).Select(index => new DailyAttendanceReward(index, index + 1, 50001, 2)).ToImmutableArray(),
        [new(0, 7, 50001, 3), new(1, 14, 50001, 4), new(2, 21, 50001, 5)]);

    public static void RunCore()
    {
        int before = Count;
        var map = new DungeonMap("boss", new(0, 0), 1, "map/attendance-fixture.map");
        var maze = new DungeonMaze(0, 1, 1, "fixture", [map.Position], [map.Position], [map], []);
        var entry = new DungeonEntryPlan(1, 0, 9001, 0, 255, 0, maze, map, map);
        var definition = new DungeonDefinition(9001, "dungeon/attendance-fixture.dgn", 40, 45, [], []);
        That(DailyAttendanceService.IsRecommended(entry with { RecommendedQuestClear = false }, definition, 45) &&
            !DailyAttendanceService.IsRecommended(entry with { RecommendedQuestClear = true }, definition, 60),
            "每位队员按本人等级判断推荐，不能继承队长资格标记");
        That(DailyAttendanceService.IsRecommended(entry with { QuestId = 3146 }, definition, 60) &&
            DailyAttendanceService.IsRecommended(entry, definition with { Path = "dungeon/harlem/fixture.dgn" }, 60),
            "复用当前95推荐规则的剧情及哈林区域资格");
        foreach (var excluded in new[] { entry with { IsTutorial = true }, entry with { IsPractice = true }, entry with { IsQuestReplay = true } })
            That(!DailyAttendanceService.IsRecommended(excluded, definition, 45), "教程、练习与任务重放不签到");
        var delayed = new DelayedEventStore();
        var events = new ServerEventService(delayed);
        events.Read(2370);
        delayed.Schedule = new(2370, 1, OverrideActive: true);
        events.Read(2370);
        That(events.IsDailyAttendanceCurrent(2, false) && !events.IsDailyAttendanceCurrent(1, true),
            "延迟返回的旧只读活动状态不能重新放行已关闭版本");
        using var f = new Fixture();
        var first = f.Read();
        That(first == new DailyAttendanceSnapshot(20261103, 0, 0, 0) && f.Number("SELECT count(*) FROM account_daily_attendance_days") == 0,
            "首次读取不补行或发奖");
        var once = f.Plan(false);
        f.Apply(once);
        That(f.Read() == first with { Clears = 1 } && f.Number("SELECT count(*) FROM mailbox_messages") == 0,
            "第一次推荐通关只推进当天次数");
        Reject<InvalidOperationException>(() => f.Apply(once), "过期快照不能重复推进");
        var twice = f.Plan(false);
        string snapshot = f.Snapshot();
        f.Sql("CREATE TRIGGER fail_attendance AFTER INSERT ON account_daily_attendance_claims BEGIN SELECT RAISE(ABORT,'attendance rollback'); END");
        Reject<SqliteException>(() => f.Apply(twice), "真实邮件、附件及UID写入后末段故障");
        That(f.Snapshot() == snapshot && f.Read().Clears == 1, "末段故障回滚次数、邮件、实例、领取事实和序列");
        f.Sql("DROP TRIGGER fail_attendance");
        uint mail = f.Apply(twice).MailId;
        var mailEntry = f.Repository.LoadMailbox(f.Owner, f.Character.Id, f.Now.ToUnixTimeSeconds()).Entries.Single();
        That(mail > 0 && f.Read() is { Clears: 2, Total: 1 } && mailEntry.System && mailEntry.Expires is null &&
            mailEntry.Attachments is [var attachment] && attachment.Item.Uid > 0 && attachment.Item.Core.Count == 2 &&
            attachment.Item.Core.Extension121 == 42 && attachment.Item.Core.ExpireTime == f.Now.ToUnixTimeSeconds() + 864000,
            "签到与完整永久系统邮件共事务，真实UID、数量、期限及metadata保留");
        var second = f.Repository.Create(f.Owner, new("AttendanceB", f.Character.Position, null));
        That(f.Repository.ReadDailyAttendance(f.Owner, 20261103) == f.Read(), "换角共享账号签到");
        f.Character = second;
        f.Now = f.Now.AddDays(1);
        That(f.Read() is { Clears: 0, Total: 1 }, "06时切日仅重置次数，累计签到保持");
        for (int day = 1; day < 28; day++)
        {
            f.Apply(f.Plan(false));
            f.Apply(f.Plan(false));
            if (day + 1 is 7 or 14 or 21)
            {
                int index = (day + 1) / 7 - 1;
                byte[] ready = DailyAttendancePackets.State(f.Read(), Catalog);
                That(BitConverter.ToUInt32(ready, 9 + index * 4) == 1, "累计档位达到才显示可领1");
                var plan = f.Plan(true);
                var claim = f.Repository.ClaimDailyAttendance(f.Owner, f.Character, plan);
                snapshot = f.Snapshot();
                var duplicate = f.Repository.ClaimDailyAttendance(f.Owner, f.Character, plan);
                That(claim.MailId > 0 && duplicate.MailId == 0 && f.Snapshot() == snapshot &&
                    (claim.Snapshot.Claimed & (1 << index)) != 0, "累计奖励每档只落一封，重复旧计划零写");
            }
            f.Now = f.Now.AddDays(1);
        }
        That(f.Read() is { Total: 28, Claimed: 7 } && f.Number("SELECT count(*) FROM mailbox_messages") == 31,
            "28次每日及7/14/21三档累计奖励完整发放");
        byte[] expected = [0x42, 9, 0, 0, 0, 28, 0, 0, 0, 2, 0, 0, 0, 2, 0, 0, 0, 2, 0, 0, 0,
            0, 0, 0, 0, 3, 0, 0, 0, 4, 0, 0, 0, 5, 0, 0, 0];
        Bytes(DailyAttendancePackets.State(f.Read(), Catalog), expected, "独立编码37B状态、已领2及保留尾");
        snapshot = f.Snapshot();
        f.Apply(f.Plan(false));
        That(f.Snapshot() == snapshot, "28次上限不再推进或循环发奖");
        f.Repository.ChangeMailState(f.Owner, mailEntry.RecipientId, [mail], 0, f.Now.ToUnixTimeSeconds(), force: true);
        That(f.Read().Total == 28, "邮件操作不清账号资格");
        var boundary = DateTimeOffset.Parse("2026-11-03T22:00:00Z");
        That(PcRoomTimePointDay.At(boundary.AddTicks(-1)) == 20261103 && PcRoomTimePointDay.At(boundary) == 20261104,
            "日签到按UTC+8的06时跨界");
        // 真实文件库有外部writer时，状态查询及重复旧计划不申请writer。
        using (var db = f.Open())
        using (var tx = db.BeginTransaction(deferred: false))
        {
            var read = Task.Run(f.Read);
            bool complete;
            try { complete = read.Wait(TimeSpan.FromSeconds(2)); }
            finally { tx.Rollback(); }
            That(complete && read.GetAwaiter().GetResult().Total == 28, "外部writer不阻塞纯状态读取");
        }
        f.Sql("DROP TABLE account_daily_attendance_claims;DROP TABLE account_daily_attendance_days;PRAGMA user_version=194");
        snapshot = f.Snapshot();
        f.Repository = new(f.Path);
        That(f.Number("PRAGMA user_version") == SqliteMigrations.CurrentVersion && f.Snapshot() == snapshot,
            "194升级195仅新增两空表并保持原角色、邮箱及资产");
        var disabled = f.Plan(false);
        f.Sql("UPDATE server_events SET override_active=0,revision=2 WHERE event_id=2370");
        snapshot = f.Snapshot();
        That(f.Apply(disabled).MailId == 0 && f.Snapshot() == snapshot, "准备后关闭活动不推进、不发奖");
        f.Sql("UPDATE server_events SET override_active=1,revision=3 WHERE event_id=2370");
        var owner = f.Owner;
        using var replacement = f.Owners.Acquire(owner.Account, () => { });
        Reject<InvalidOperationException>(() => f.Repository.ReadDailyAttendance(owner, 20261103), "旧generation不能读写签到状态");
        That(f.Number("SELECT count(*) FROM pragma_foreign_key_check") == 0, "签到与邮箱资产外键保持");
        Console.WriteLine($"PASS {Count - before} 日签到规则、wire、存储、升级及回滚断言");
    }

    sealed class Fixture : IDisposable
    {
        readonly string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "S5A9-attendance2370-" + Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(root, "game.db");
        public AccountOwners Owners { get; } = new();
        public AccountOwners.Lease Owner { get; }
        public CharacterRepository Repository { get; set; }
        public Character Character { get; set; }
        public DateTimeOffset Now { get; set; } = DateTimeOffset.Parse("2026-11-03T04:00:00Z");
        public Fixture()
        {
            Owner = Owners.Acquire("attendance-owner", () => { });
            Repository = new(Path);
            Character = Repository.Create(Owner, new("AttendanceA", new(1, 0, 100, 100), null));
            Sql("INSERT INTO server_events(event_id,revision,override_active) VALUES(2370,1,1)");
        }
        public DailyAttendanceSnapshot Read() => Repository.ReadDailyAttendance(Owner, PcRoomTimePointDay.At(Now));
        public DailyAttendancePlan Plan(bool claim)
        {
            var before = Read();
            var reward = claim ? Catalog.Accumulated.FirstOrDefault(row => row.RequiredDays <= before.Total && (before.Claimed & (1 << row.Index)) == 0)
                : before.Total < 28 && before.Clears == 1 ? Catalog.Daily[before.Total] : null;
            ImmutableArray<InventoryItem> attachments = reward is null ? [] :
                [new(0, new(InventoryListType.Main, 121), new(50001, ItemKind.Material, reward.Count)
                    { Extension121 = 42, ExpireTime = checked((int)Now.ToUnixTimeSeconds() + 864000) })];
            return new(before, Catalog, Number("SELECT revision FROM server_events WHERE event_id=2370"), Now, reward, attachments);
        }
        public DailyAttendanceCommit Apply(DailyAttendancePlan plan)
        {
            using var db = Open();
            using var tx = db.BeginTransaction();
            var result = CharacterRepository.ApplyDailyAttendance(db, tx, Owner.Account, Character, plan, false);
            tx.Commit();
            return result;
        }
        public SqliteConnection Open()
        {
            var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path, Pooling = false, ForeignKeys = true }.ToString());
            db.Open(); return db;
        }
        public void Sql(string sql) { using var db = Open(); using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
        public long Number(string sql) { using var db = Open(); using var command = db.CreateCommand(); command.CommandText = sql; return (long)command.ExecuteScalar()!; }
        public string Snapshot()
        {
            using var db = Open(); using var command = db.CreateCommand();
            command.CommandText = "SELECT name FROM sqlite_schema WHERE type='table' ORDER BY name";
            List<string> tables = [];
            using (var reader = command.ExecuteReader()) while (reader.Read()) tables.Add(reader.GetString(0));
            List<object> rows = [];
            foreach (string table in tables)
            {
                command.CommandText = "SELECT * FROM [" + table + "] ORDER BY rowid";
                using var reader = command.ExecuteReader();
                while (reader.Read()) rows.Add(new { Table = table, Values = Enumerable.Range(0, reader.FieldCount).Select(reader.GetValue).ToArray() });
            }
            return JsonSerializer.Serialize(rows);
        }
        public void Dispose() { Owner.Dispose(); TestFiles.DeleteDirectory(root, true); }
    }

    sealed class DelayedEventStore : IServerEventStore
    {
        public ServerEventSchedule Schedule { get; set; } = new(2370, 2, OverrideActive: false);
        public ServerEventSchedule Read(ushort eventId) => Schedule;
        public ServerEventSchedule Save(ServerEventSchedule expected, ServerEventSchedule desired) => throw new NotSupportedException();
    }
}
