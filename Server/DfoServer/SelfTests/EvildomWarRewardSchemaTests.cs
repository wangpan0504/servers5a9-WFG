using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class EvildomWarRewardSchemaTests
{
    public static void RunCore()
    {
        int before = Count;
        string root = Path.Combine(Path.GetTempPath(), "S5A9-war-reward-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (var (table, previous) in new[] { ("character_evildom_war_rewards", 114), ("character_evildom_war_hidden_rewards", 117),
                ("character_dungeon_incount_extra", 118) })
            foreach (bool conflict in new[] { false, true })
            {
                string path = Path.Combine(root, table + conflict + ".db");
                _ = new CharacterRepository(path);
                That(Scalar(path, $"SELECT count(*) FROM {table}") is 0L, "新建库有空奖励回执表，不伪造历史奖励");
                Sql(path, $"DROP TABLE {table}; PRAGMA user_version={previous}");
                if (conflict)
                {
                    Sql(path, $"CREATE TABLE {table}(character_id INTEGER)");
                    Reject<InvalidDataException>(() => new CharacterRepository(path), "升级拒绝同名但缺失约束的奖励表");
                    That(Scalar(path, "PRAGMA user_version") is long old && old == previous &&
                        Scalar(path, $"SELECT count(*) FROM pragma_table_info('{table}')") is 1L,
                        "结构冲突整体回滚，不推进库版本或重建未知表");
                    continue;
                }
                _ = new CharacterRepository(path);
                That(Scalar(path, "PRAGMA user_version") is long version && version == SqliteMigrations.CurrentVersion &&
                    Scalar(path, $"SELECT count(*) FROM {table}") is 0L, "旧版本升级只新增完整空表");
                Sql(path, $"CREATE TRIGGER reward_reopen_probe BEFORE INSERT ON {table} BEGIN SELECT RAISE(ABORT,'probe'); END");
                _ = new CharacterRepository(path);
                That(Scalar(path, "SELECT count(*) FROM sqlite_schema WHERE name='reward_reopen_probe'") is 1L,
                    "重开库不重建奖励表或删除存量触发器");
                Sql(path, $"PRAGMA user_version={SqliteMigrations.CurrentVersion + 1}");
                Reject<InvalidDataException>(() => new CharacterRepository(path), "拒绝较新版本，不执行逆向迁移");
                That(Scalar(path, "PRAGMA user_version") is long future && future == SqliteMigrations.CurrentVersion + 1,
                    "拒绝未来版本不改变库版本");
            }
        }
        finally { TestFiles.DeleteDirectory(root, true); }
        Console.WriteLine($"PASS evildom war reward schema {Count - before}");
    }

    static void Sql(string path, string sql) { using var db = Open(path); using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
    static object? Scalar(string path, string sql) { using var db = Open(path); using var command = db.CreateCommand(); command.CommandText = sql; return command.ExecuteScalar(); }
    static SqliteConnection Open(string path) { var db = new SqliteConnection("Data Source=" + path + ";Pooling=False"); db.Open(); return db; }
}
