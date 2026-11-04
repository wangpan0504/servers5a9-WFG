using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Events;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.OnlineEvents;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class WeeklyAttendanceTests
{
    internal static WeeklyAttendanceCatalog Catalog() => new(9, 4, 20, 2,
        Enumerable.Range(1, 9).Select(i => new WeeklyAttendanceReward(i, 50001, 1)).ToImmutableArray(),
        [new(2, 50001, 1), new(5, 50001, 1), new(8, 50001, 1)]);

    internal static void RunCore()
    {
        var catalog = Catalog();
        catalog.Validate();
        var at = new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.FromHours(8));
        var season = WeeklyAttendanceSeason.Create(at, 9);
        That(DateTimeOffset.FromUnixTimeSeconds(season.StartsAt).ToOffset(TimeSpan.FromHours(8)) ==
            new DateTimeOffset(2026, 10, 29, 6, 0, 0, TimeSpan.FromHours(8)) && season.Week(at) == 0 &&
            season.Week(at.AddSeconds(-1)) == -1 && season.Week(DateTimeOffset.FromUnixTimeSeconds(season.EndsAt)) == -1,
            "周四06边界与真实激活时刻固定，九周末不自动续期");
        var state = WeeklyAttendanceState.Parse(null, catalog);
        state.Clears[0] = 3;
        That(state.Claim(catalog, season, at, true, false).IsEmpty, "不足四次通关不签到");
        state.Clears[0] = 4;
        That(state.Claim(catalog, season, at, true, false).Length == 1 && state.Signed == 1 && state.Clears[0] == 0,
            "普通签到原子消耗四次并置本周位");
        That(state.Claim(catalog, season, at, true, false).IsEmpty, "第一周重复请求不能补签未来周");
        var thirdWeek = DateTimeOffset.FromUnixTimeSeconds(season.StartsAt).AddDays(14);
        state.Clears[2] = 44;
        var regular = state.Claim(catalog, season, thirdWeek, true, false);
        var catchup = state.Claim(catalog, season, thirdWeek, true, false);
        That(regular.Single().Threshold == 2 && catchup.Single().Threshold == 3 && state.Signed == 7 &&
            state.Catchups == 1 && state.Clears[2] == 20, "先签当周，再补最早缺周；奖励按累计次数顺序发放");
        That(state.Claim(catalog, season, thirdWeek, true, false).IsEmpty, "没有缺周时不能继续消耗补签次数");
        That(state.Claim(catalog, season, thirdWeek, false, true).Single().Threshold == 2 && state.Bonus == 4 &&
            state.Claim(catalog, season, thirdWeek, false, true).IsEmpty, "累计2次奖励独立领取且不可重发");
        var snapshot = state.Snapshot(catalog, 2);
        byte[] expected = new byte[459];
        expected[4] = 3; expected[8] = 4;
        expected[0x19c] = 1; expected[0x19d] = 2; expected[0x19e] = 3;
        expected[0x1b4] = 20; expected[0x1bc] = 1; expected[0x1c0] = 1; expected[0x1c4] = 3;
        Bytes(WeeklyAttendancePackets.State(snapshot), expected, "1096完整459字节与独立偏移预期一致，其余字节保零");
        Bytes(WeeklyAttendancePackets.Ack(null), [0, 1], "1115失败两字节");
        Bytes(WeeklyAttendancePackets.Ack(new(snapshot, [])), [1, 1, 0, 0, 0], "合法无新奖返回result1");
        Bytes(WeeklyAttendancePackets.Ack(new(snapshot, [1])), [1, 0, 0, 0, 0], "真实新邮件返回result0");
        That(WeeklyAttendanceRequests.Sign(new byte[13]) && !WeeklyAttendanceRequests.Sign(new byte[12]), "签到严格13字节opaque正文");
        byte[] request = [0xa0, 8, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        That(WeeklyAttendanceRequests.Bonus(request), "689累计奖励按2208和三个零DWORD认领");
        request[^1] = 1;
        That(!WeeklyAttendanceRequests.Bonus(request), "拒绝累计请求非零未知字段");
        Reject<WeeklyAttendanceRejectedException>(() => state.Claim(catalog, season, at, true, false));
        Reject<InvalidDataException>(() => WeeklyAttendanceState.Parse("{\"Clears\":[1]}", catalog));
        Store(catalog, at);
    }

    static void Store(WeeklyAttendanceCatalog catalog, DateTimeOffset at)
    {
        string root = Path.Combine(Path.GetTempPath(), "S5A9-weekly-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "game.db");
        try
        {
            var repository = new CharacterRepository(path);
            var events = new ServerEventService(new ServerEventRepository(path));
            That(events.Read(2208) is { Active: true, Schedule.Revision: 0 }, "周签到未配置默认开启且不写开关行");
            using var owner = new AccountOwners().Acquire("weekly-owner", () => { });
            var character = repository.Create(owner, new("WeeklyA", new(1, 0, 100, 100), null));
            var season = repository.ActivateWeeklyAttendance(owner, character, at, catalog);
            var state = WeeklyAttendanceState.Parse(null, catalog);
            state.Clears[0] = 4;
            Sql("INSERT INTO account_weekly_attendance VALUES($account,$start,$state)", state.Serialize());
            ImmutableArray<InventoryItem> attachment = [new(0, new(InventoryListType.Main, 0), new(50001, ItemKind.Material, 1))];
            var first = repository.CommitWeeklyAttendance(owner, character, season, at, catalog, true, false, [attachment]);
            That(first.MailIds.Length == 1 && first.Snapshot.Total == 1 && Count() == 1, "签到进度和真实邮件UID同事务创建");
            var replay = repository.CommitWeeklyAttendance(owner, character, season, at, catalog, true, false, []);
            That(replay.MailIds.IsEmpty && Count() == 1 && repository.List(owner).Single().Revision == character.Revision,
                "重复领取零写且不增加角色revision");
            var other = repository.Create(owner, new("WeeklyB", character.Position, null));
            That(repository.ReadWeeklyAttendance(owner, season, catalog).Signed == 1, "换角共享账号签到进度");
            var secondWeek = DateTimeOffset.FromUnixTimeSeconds(season.StartsAt).AddDays(7);
            state = repository.ReadWeeklyAttendance(owner, season, catalog);
            state.Clears[1] = 4;
            Sql("UPDATE account_weekly_attendance SET state=$state WHERE account=$account AND season_start=$start", state.Serialize());
            Sql("CREATE TRIGGER weekly_fail BEFORE UPDATE ON account_weekly_attendance BEGIN SELECT RAISE(ABORT,'weekly late failure'); END", "");
            Reject<SqliteException>(() => repository.CommitWeeklyAttendance(owner, other, season, secondWeek, catalog, true, false, [attachment]));
            That(Count() == 1 && repository.ReadWeeklyAttendance(owner, season, catalog).Signed == 1,
                "真实邮件创建后晚账本故障回滚全部邮件、UID与进度");
            Sql("DROP TRIGGER weekly_fail", "");
            var retry = repository.CommitWeeklyAttendance(owner, other, season, secondWeek, catalog, true, false, [attachment]);
            That(retry.MailIds.Length == 1 && retry.Snapshot.Total == 2 && Count() == 2, "同请求重试只投递一次且收件人为当前角色");
            var bonus = repository.CommitWeeklyAttendance(owner, other, season, secondWeek, catalog, false, true, [attachment]);
            That(bonus.Snapshot.BonusMask == 4 && Count() == 3 && new CharacterRepository(path)
                .ReadWeeklyAttendance(owner, season, catalog).Bonus == 4, "累计奖励与重开仓储恢复保持");
            events.Override(2208, false);
            Reject<WeeklyAttendanceRejectedException>(() => repository.CommitWeeklyAttendance(owner, other, season, secondWeek, catalog, true, false, []));
            That(repository.ReadWeeklyAttendanceSeason() == season && Count() == 3, "关闭保留固定赛季与原奖励事实");
            // 仅回退本测试的两张专用表，已有角色和邮件保持。
            Sql("DROP TABLE account_battle_pass_deluxe_orders; DROP TABLE account_weekly_attendance; DROP TABLE weekly_attendance_season; PRAGMA user_version=195", "");
            _ = new CharacterRepository(path);
            That(Count() == 3 && repository.ReadWeeklyAttendanceSeason() is null, "195升级当前版本保留既有角色邮件，周签到196步骤不变且新赛季保持未激活");

            long Count()
            {
                using var db = Open(); using var command = db.CreateCommand();
                command.CommandText = "SELECT count(*) FROM mailbox_messages WHERE delivery_key LIKE 'weekly2208:%'";
                return (long)command.ExecuteScalar()!;
            }
            SqliteConnection Open()
            {
                var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
                db.Open(); return db;
            }
            void Sql(string sql, string json)
            {
                using var db = Open(); using var command = db.CreateCommand();
                command.CommandText = sql;
                command.Parameters.AddWithValue("$account", owner.Account);
                command.Parameters.AddWithValue("$start", season.StartsAt);
                command.Parameters.AddWithValue("$state", json);
                command.ExecuteNonQuery();
            }
        }
        finally { TestFiles.DeleteDirectory(root, true); }
    }
}
