using DfoServer.Game.Accounts;
using DfoServer.Game.Guild;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using DfoServer.Network.Builders;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class GuildAttendanceTests
{
    public static void RunCore()
    {
        int before = Count;
        long boundary = new DateTimeOffset(2026, 10, 26, 6, 0, 0, TimeSpan.FromHours(8)).ToUnixTimeSeconds();
        That(GuildAttendanceDay.Start(boundary - 1) == boundary - 86400 &&
            GuildAttendanceDay.Start(boundary) == boundary && GuildAttendanceDay.Start(boundary + 86399) == boundary &&
            GuildAttendanceDay.Start(boundary + 86400) == boundary + 86400,
            "业务日在UTC+8六点精确切换，非UTC午夜或滚动24小时");
        byte[] names = GuildAttendancePackets.Names(new(1, boundary,
            [new(2, "出勤成员", 95, 1, 0, 4, boundary)]));
        using (var reader = new BinaryReader(new MemoryStream(names)))
            That(reader.ReadByte() == 1 && reader.ReadUInt16() == 1 &&
                GuildApplicationTests.ReadText(reader) == "出勤成员" && reader.BaseStream.Position == reader.BaseStream.Length,
                "746按真实消费者只写u16条数和名称，不附伪造角色资料");
        That(GuildAttendancePackets.Names(new(1, boundary, [])).SequenceEqual(new byte[] { 1, 0, 0 }),
            "真实空出勤名单通过零条数清除原生旧缓存");
        string root = Path.Combine(Path.GetTempPath(), "guild-attendance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "game.db");
            var repository = new CharacterRepository(path);
            var owners = new AccountOwners();
            using var owner = owners.Acquire("g-attendance", static () => { });
            using var foreign = owners.Acquire("g-attendance-other", static () => { });
            var room = new Location(38, 1, 450, 234, 0);
            var first = repository.Create(owner, new("AttendOne", room, room));
            var second = repository.Create(foreign, new("AttendTwo", room, room));
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = path, Pooling = false, ForeignKeys = true }.ToString());
            db.Open();
            void Sql(string sql) { using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
            long Number(string sql) { using var command = db.CreateCommand(); command.CommandText = sql; return Convert.ToInt64(command.ExecuteScalar()); }
            Sql($"""
                INSERT INTO guilds(guild_id,name,address,master_character_id,created_at_unix)
                VALUES(1,'出勤测试','attendance',{first.Id},1000);
                INSERT INTO guild_members(guild_id,character_id,grade,joined_at_unix)
                VALUES(1,{first.Id},0,1000),(1,{second.Id},0,1000);
                """);
            That(repository.ReadGuildAttendance(owner, first.Id, boundary) is { Members.Length: 0 } &&
                Number("SELECT count(*) FROM guild_attendance") == 0, "出勤查询不补写签到或奖励");
            That(!repository.ObserveGuildAttendance(owner, second.Id, boundary) &&
                Number("SELECT count(*) FROM guild_attendance") == 0, "同公会不能借其他账号角色记录出勤");
            That(repository.ObserveGuildAttendance(owner, first.Id, boundary) &&
                !repository.ObserveGuildAttendance(owner, first.Id, boundary + 1) &&
                Number("SELECT count(*) FROM guild_attendance") == 1, "同一会籍每日只保留首个真实观察");
            That(repository.ReadGuildAttendance(owner, first.Id, boundary) is
                { Members: [{ CharacterId: var id, Grade: 1, ObservedAtUnix: var observed }] } &&
                id == first.Id && observed == boundary, "名单读取真实角色、规范职位与首个观察时间");
            That(repository.ReadGuildAttendance(owner, first.Id, boundary - 1) is { Members.Length: 0 },
                "跨日读取不把未来观察提前展示");
            That(repository.ObserveGuildAttendance(owner, first.Id, boundary + 86400) &&
                Number("SELECT count(*) FROM guild_attendance") == 2, "下个业务日可新增独立出勤事实");
            Sql($"UPDATE guild_members SET joined_at_unix={boundary + 86410} WHERE character_id={first.Id}");
            That(!repository.ObserveGuildAttendance(owner, first.Id, boundary + 86405) &&
                repository.ReadGuildAttendance(owner, first.Id, boundary + 86420) is { Members.Length: 0 },
                "重入后旧会籍观察与晚到旧登录均不能污染当前出勤");
            That(repository.ObserveGuildAttendance(owner, first.Id, boundary + 86420) &&
                repository.ReadGuildAttendance(owner, first.Id, boundary + 86420) is { Members.Length: 1 },
                "新会籍以真实入城记录出勤，名单仍只显示当前会籍一次");
            That(repository.LoadCharacter(owner, first.Slot).Revision == first.Revision &&
                Number("SELECT contribution FROM guild_members WHERE character_id=" + first.Id) == 0 &&
                Number("SELECT public_funds FROM guilds") == 0,
                "出勤事实不虚构贡献奖励、不扣金币也不推进资产revision");
            var reopened = new CharacterRepository(path);
            That(reopened.ReadGuildAttendance(owner, first.Id, boundary + 86420) is { Members.Length: 1 },
                "重开库保留已观察出勤");
            using var replacement = owners.Acquire(owner.Account, static () => { });
            Reject<InvalidOperationException>(() => repository.ObserveGuildAttendance(owner, first.Id, boundary + 86421),
                "接管后旧owner不能继续记出勤");
            Sql("DELETE FROM guilds WHERE guild_id=1");
            That(Number("SELECT count(*) FROM guild_attendance") == 0 &&
                Number("SELECT count(*) FROM pragma_foreign_key_check") == 0, "解散级联清除出勤，无孤立记录");
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
        Console.WriteLine($"PASS {Count - before} 公会真实出勤、业务日与会籍隔离断言");
    }
}
