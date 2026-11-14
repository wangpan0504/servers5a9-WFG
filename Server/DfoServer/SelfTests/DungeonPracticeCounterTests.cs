using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Time;
using DfoServer.Network.Builders;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class DungeonPracticeCounterTests
{
    sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.Parse("2030-05-01T20:59:59Z");
        public int Reads;
        public int CrossOnRead = int.MaxValue;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.CreateCustomTimeZone("practice-test-09", TimeSpan.FromHours(9), "test", "test");
        public override DateTimeOffset GetUtcNow() => ++Reads >= CrossOnRead ? Now.AddSeconds(1) : Now;
    }

    public static void RunCore()
    {
        int before = Count;
        var clock = new Clock();
        int day = ServiceDay.Number(clock.Now, clock.LocalTimeZone);
        That(ServiceDay.Number(clock.Now.AddSeconds(1), clock.LocalTimeZone) == day + 1 &&
            ServiceDay.Number(clock.Now.AddSeconds(1), TimeZoneInfo.Utc) == day,
            "练习跟随本机06:00，不强制东八区或UTC分日");
        var empty = CharacterDungeonState.NewOrdinary;
        Reject<ArgumentOutOfRangeException>(() => new DungeonPracticeAdmission(0, clock), "非法服务日不能构造入场许可");
        var state = empty with { PracticeDay = day, PracticeUsed = 3 };
        That(DungeonPracticeAdmission.Prepare(state, clock) is null, "同角色共用三次耗尽");
        That(DungeonPracticeAdmission.Used(state, day - 1) == 3 && DungeonPracticeAdmission.Used(state, day + 1) == 0,
            "回拨不补次，新服务日才恢复");
        Bytes(DungeonPackets.SelectionRejected(DungeonSelectionError.MemberEntryLimit), [0,173,0,0], "练习上限沿已证173和单人slot0");
        string root = Path.Combine(Path.GetTempPath(), "practice-counter-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "game.db");
            var repository = new CharacterRepository(path);
            var owners = new AccountOwners();
            using var owner = owners.Acquire("practice-counter", () => { });
            var character = repository.Create(owner, new("PracCount", new(1,0,10,10), null) { DungeonState = empty });
            using var sql = new SqliteConnection("Data Source=" + path); sql.Open();
            void Execute(string text) { using var command = sql.CreateCommand(); command.CommandText = text; command.ExecuteNonQuery(); }
            DungeonEntryCharge Charge(Character current, DungeonPracticeAdmission admission, int dungeon = 291100308) =>
                new(null, 0, new(current.Id, 0, current.DungeonState!.LastAttempt + 1, Guid.NewGuid(), 1), 1)
                { DungeonId = dungeon, Practice = admission };
            var admission = DungeonPracticeAdmission.Prepare(character.DungeonState!, clock)!;
            Execute("CREATE TRIGGER fail_practice_entry AFTER UPDATE OF practice_used ON character_dungeon_state BEGIN SELECT RAISE(ABORT,'practice rollback'); END");
            Reject<SqliteException>(() => repository.CommitDungeonEntry(owner, character, 300, Charge(character, admission)), "练习次数晚期写入失败");
            var rolled = repository.LoadCharacter(owner, character.Slot);
            That(rolled.Revision == character.Revision && rolled.DungeonState == character.DungeonState,
                "次数失败与revision、attempt、key同事务回滚");
            Execute("DROP TRIGGER fail_practice_entry");
            // 先规划、再推进时钟，模拟准备/SQL等待跨界；真正提交前必须拒绝整笔旧日计划。
            clock.Now = clock.Now.AddSeconds(1);
            Reject<DungeonPracticeRejectedException>(() => repository.CommitDungeonEntry(owner, character, 300, Charge(character, admission)), "旧服务日准备不得提交");
            That(repository.LoadCharacter(owner, character.Slot).DungeonState == character.DungeonState, "跨界拒绝不扣次或attempt");
            clock.Now = clock.Now.AddSeconds(-1); clock.Reads = 0; clock.CrossOnRead = 4;
            admission = DungeonPracticeAdmission.Prepare(character.DungeonState!, clock)!;
            Reject<DungeonPracticeRejectedException>(() => repository.CommitDungeonEntry(owner, character, 300, Charge(character, admission)), "入场写入后、Commit前跨日也回滚");
            rolled = repository.LoadCharacter(owner, character.Slot);
            That(rolled.Revision == character.Revision && rolled.DungeonState == character.DungeonState,
                "最终日界复验失败不能留下扣次和预留");
            clock.CrossOnRead = int.MaxValue;
            for (byte i = 1; i <= 3; i++)
            {
                var expected = character;
                admission = DungeonPracticeAdmission.Prepare(expected.DungeonState!, clock)!;
                var commit = repository.CommitDungeonEntry(owner, expected, checked((uint)(300+i)), Charge(expected, admission, i == 1 ? 291100308 : 7176+i));
                character = commit.Character;
                That(character.DungeonState is { } current && current.PracticeDay == day && current.PracticeUsed == i &&
                    current.LastAttempt == i && commit.Practice == new DungeonPracticeTransition(day, (byte)(i-1), i),
                    "跨DGN原入场事务共享本角色计数并返回实际过渡");
                Reject<InvalidOperationException>(() => repository.CommitDungeonEntry(owner, expected, 400, Charge(expected, admission)), "旧revision重放不重复提交");
            }
            admission = new(day, clock);
            Reject<DungeonPracticeRejectedException>(() => repository.CommitDungeonEntry(owner, character, 400, Charge(character, admission)), "第四次仓储复验拒绝，不只依赖UI");
            var ordinary = repository.ReserveDungeonEntry(owner, character, 400).Character;
            That(ordinary.DungeonState!.PracticeUsed == 3 && ordinary.DungeonState.LastAttempt == 4, "普通预留不扣练习");
            character = repository.LoadCharacter(owner, character.Slot);
            That(character.DungeonState!.PracticeUsed == 3, "重新读取持久上限，不因重连补额度");
            var copied = repository.CopyCharacter(owner, new(character.Name, character.Id, character.Revision,
                "PracticeCopy", character.Position, character.Position));
            That(copied.DungeonState == character.DungeonState, "角色复制的显式字段名单保留练习日戳和次数");
            var another = repository.Create(owner, new("PracOther", new(1,0,10,10), null) { DungeonState = empty });
            That(another.DungeonState!.PracticeUsed == 0, "同账号另一角色额度隔离");
            var reset = repository.ResetPracticeDay(owner, character, day + 1);
            That(reset.DungeonState!.PracticeDay == day + 1 && reset.DungeonState.PracticeUsed == 0 &&
                reset.DungeonState.LastAttempt == character.DungeonState.LastAttempt && reset.Revision == character.Revision + 1,
                "新日仅清练习次数，保留尝试号和其它owner字段");
            That(ReferenceEquals(repository.ResetPracticeDay(owner, reset, day + 1), reset) &&
                ReferenceEquals(repository.ResetPracticeDay(owner, reset, day), reset), "重复日切与回拨不重补、不倒退水印");
            Reject<InvalidOperationException>(() => repository.ResetPracticeDay(owner, character, day + 2), "旧revision不能覆盖新服务日");
            clock.Now = clock.Now.AddSeconds(1);
            var sharedAdmission = DungeonPracticeAdmission.Prepare(reset.DungeonState!, clock)!;
            bool Attempt()
            {
                try { repository.CommitDungeonEntry(owner, reset, 500, Charge(reset, sharedAdmission)); return true; }
                catch (InvalidOperationException) { return false; }
            }
            var raced = Task.WhenAll(Task.Run(Attempt), Task.Run(Attempt)).GetAwaiter().GetResult();
            var winner = repository.LoadCharacter(owner, reset.Slot);
            That(raced.Count(value => value) == 1 && winner.DungeonState!.PracticeUsed == 1 &&
                winner.DungeonState.LastAttempt == reset.DungeonState.LastAttempt + 1,
                "同快照并发入场只有一次实际提交，另一个旧revision不重扣");
            using var nextOwner = owners.Acquire("practice-counter", () => { });
            Reject<InvalidOperationException>(() => repository.ResetPracticeDay(owner, winner, day + 2), "旧generation不能日切写入");
            That(repository.LoadCharacter(nextOwner, winner.Slot).DungeonState == winner.DungeonState,
                "接管后保留当前权威次数，旧owner没有写入");
            VerifyUpgrade(root);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
        Console.WriteLine($"PASS {Count-before} 练习次数、事务、日界及迁移断言");
    }

    static void VerifyUpgrade(string root)
    {
        foreach (string scenario in new[] { "ordinary", "conflict", "late-failure" })
        {
            string path = Path.Combine(root, "upgrade-" + scenario + ".db");
            _ = new CharacterRepository(path);
            using var db = new SqliteConnection("Data Source=" + path); db.Open();
            void Execute(string sql) { using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
            long Value(string sql) { using var command = db.CreateCommand(); command.CommandText = sql; return Convert.ToInt64(command.ExecuteScalar()); }
            Execute("ALTER TABLE character_dungeon_state DROP COLUMN practice_used; ALTER TABLE character_dungeon_state DROP COLUMN practice_day; PRAGMA user_version=211;");
            if (scenario == "conflict") Execute("ALTER TABLE character_dungeon_state ADD COLUMN practice_used INTEGER NOT NULL DEFAULT 9;");
            if (scenario == "late-failure") Execute("ALTER TABLE accounts DROP COLUMN disabled;");
            if (scenario != "ordinary")
            {
                Reject<Exception>(() => new CharacterRepository(path), "未知列或迁移后期失败必须拒绝");
                That(Value("PRAGMA user_version") == 211 && Value("SELECT count(*) FROM pragma_table_info('character_dungeon_state') WHERE name='practice_day'") == 0,
                    "失败迁移整笔回滚，不能留下新日戳或推进版本");
            }
            else
            {
                _ = new CharacterRepository(path);
                That(Value("PRAGMA user_version") == SqliteMigrations.CurrentVersion, "旧库升级到当前连续版本");
                _ = new CharacterRepository(path);
                That(Value("SELECT count(*) FROM pragma_table_info('character_dungeon_state') WHERE name IN ('practice_day','practice_used')") == 2,
                    "重开库保留已验证两列，不重复追加");
            }
        }
    }
}
