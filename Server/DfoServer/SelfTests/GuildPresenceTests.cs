using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class GuildPresenceTests
{
    sealed class Clock : TimeProvider
    {
        public long Unix { get; set; } = 2_000;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(Unix);
    }

    public static void RunCore()
    {
        int before = Count;
        string root = Path.Combine(Path.GetTempPath(), "guild-presence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "game.db");
            var repository = new CharacterRepository(path);
            var owners = new AccountOwners();
            using var owner = owners.Acquire("guild-presence", () => { });
            using var foreign = owners.Acquire("guild-presence-other", () => { });
            var room = new Location(38, 1, 450, 234, 0);
            var first = repository.Create(owner, new("PresenceOne", room, room));
            var second = repository.Create(foreign, new("PresenceTwo", room, room));
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = path, Pooling = false, ForeignKeys = true }.ToString());
            db.Open();
            void Sql(string text)
            {
                using var command = db.CreateCommand();
                command.CommandText = text;
                command.ExecuteNonQuery();
            }
            long Seen(ushort id)
            {
                using var command = db.CreateCommand();
                command.CommandText = "SELECT last_login_unix FROM guild_members WHERE character_id=$id";
                command.Parameters.AddWithValue("$id", id);
                return Convert.ToInt64(command.ExecuteScalar());
            }
            Sql($"""
                INSERT INTO guilds(guild_id,name,address,master_character_id,created_at_unix)
                VALUES(1,'观察公会','presence',{first.Id},1000);
                INSERT INTO guild_members(guild_id,character_id,joined_at_unix,last_login_unix)
                VALUES(1,{first.Id},1000,0),(1,{second.Id},1000,0);
                """);
            repository.ObserveGuildMemberOnline(owner, second.Id, 2_000);
            That(Seen(second.Id) == 0, "在线观察不能借同公会身份更新其他账号的成员记录");
            repository.ObserveGuildMemberOnline(owner, first.Id, 2_000);
            repository.ObserveGuildMemberOnline(owner, first.Id, 1_500);
            That(Seen(first.Id) == 2_000 && repository.LoadCharacter(owner, first.Slot).Revision == first.Revision,
                "观测单调且只更新成员时间，不推进角色资产revision");
            Sql($"UPDATE guild_members SET joined_at_unix=4000 WHERE character_id={first.Id}");
            repository.ObserveGuildMemberOnline(owner, first.Id, 3_000);
            That(Seen(first.Id) == 2_000, "晚到的旧入会观察不能覆盖新关系的时间");
            Sql($"UPDATE guild_members SET joined_at_unix=1000 WHERE character_id={first.Id}");

            var towns = new TownCatalog(new Dictionary<(byte, byte), TownArea>(), room, room,
                new TownDock(0, [], []), [], new Dictionary<uint, uint[]>());
            var clock = new Clock { Unix = 5_000 };
            var session = new GameSession(repository, towns, owner, timeProvider: clock);
            session.Select(first.Slot);
            That(Seen(first.Id) == 2_000, "选角尚未就绪不虚构已在线观察");
            session.MarkReady();
            That(Seen(first.Id) == 5_000, "自然入城就绪记录实际观察时刻");
            clock.Unix = 6_000;
            session.ReturnToRoster();
            clock.Unix = 7_000;
            session.ReturnToRoster();
            That(Seen(first.Id) == 6_000, "返回选角记录最后在线；未选中角色时重复清理不延长历史");
            session.Select(first.Slot);
            clock.Unix = 8_000;
            session.MarkReady();
            using var replacement = owners.Acquire(owner.Account, () => { });
            clock.Unix = 9_000;
            session.End();
            That(Seen(first.Id) == 8_000, "旧generation断开仅清理自身，不能写新连接的在线历史");
            Reject<InvalidOperationException>(() => repository.ObserveGuildMemberOnline(owner, first.Id, 9_000),
                "旧owner不能绕过会话入口写在线时间");
            var current = new GameSession(repository, towns, replacement, timeProvider: clock);
            current.Select(first.Slot);
            clock.Unix = 10_000;
            current.MarkReady();
            clock.Unix = 11_000;
            current.End();
            clock.Unix = 12_000;
            current.End();
            That(Seen(first.Id) == 11_000, "当前连接断开记录观察；重复End不延长离线起点");

            Sql("CREATE TRIGGER presence_abort BEFORE UPDATE OF last_login_unix ON guild_members BEGIN SELECT RAISE(ABORT,'presence fault'); END");
            int failures = 0;
            var failed = new GameSession(repository, towns, replacement, timeProvider: clock)
            {
                GuildPresenceFailed = _ => failures++
            };
            failed.Select(first.Slot);
            failed.MarkReady();
            That(failed.Ready && failed.LastGuildPresenceFailure?.Error is SqliteException && failures == 1,
                "辅助时间保存失败仍完成就绪，并保留错误与宿主诊断回调");
            clock.Unix = 13_000;
            failed.GuildPresenceFailed = _ => { failures++; throw new IOException("diagnostic fault"); };
            failed.ReturnToRoster();
            That(failed.Selected is null && !failed.Ready && failures == 2 && Seen(first.Id) == 11_000 &&
                failed.LastGuildPresenceFailure?.Error is AggregateException,
                "辅助保存及诊断失败仍可返回选角，SQL回滚不污染历史且两项错误均可检查");
            failed.End();
            Sql("DROP TRIGGER presence_abort");
            repository.ObserveGuildMemberOnline(replacement, first.Id, 14_000);
            That(Seen(first.Id) == 14_000 && Seen(second.Id) == 0,
                "故障恢复后当前owner可继续观察，未观察成员仍保留未知0");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
        Console.WriteLine($"PASS {Count - before} 公会在线观察、会话生命周期及失败隔离断言");
    }
}
