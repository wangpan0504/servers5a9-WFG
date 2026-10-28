using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class EvildomWarGuideSchemaTests
{
    public static void RunCore()
    {
        int before = Count;
        string root = Path.Combine(Path.GetTempPath(), "S5A9-war-guide-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (bool conflict in new[] { false, true })
            {
                string path = Path.Combine(root, conflict + ".db");
                _ = new CharacterRepository(path);
                That(Scalar("SELECT count(*) FROM account_evildom_war_guides") is 0L, "新建库建立空引导绑定表");
                Sql("DROP TABLE account_evildom_war_guides; PRAGMA user_version=116");
                if (conflict)
                {
                    Sql("CREATE TABLE account_evildom_war_guides(account TEXT)");
                    Reject<InvalidDataException>(() => new CharacterRepository(path), "拒绝同名缺约束的引导绑定表");
                    That(Scalar("PRAGMA user_version") is 116L &&
                        Scalar("SELECT count(*) FROM pragma_table_info('account_evildom_war_guides')") is 1L,
                        "迁移冲突完整回滚并保留原结构与版本");
                    continue;
                }
                _ = new CharacterRepository(path);
                That(Scalar("PRAGMA user_version") is long version && version == SqliteMigrations.CurrentVersion,
                    "116旧库升级到引导绑定版本");
                Sql("CREATE TRIGGER guide_reopen_probe BEFORE INSERT ON account_evildom_war_guides BEGIN SELECT RAISE(ABORT,'probe'); END");
                _ = new CharacterRepository(path);
                That(Scalar("SELECT count(*) FROM sqlite_schema WHERE name='guide_reopen_probe'") is 1L,
                    "重开库保留原绑定表和触发器");
                Sql($"PRAGMA user_version={SqliteMigrations.CurrentVersion + 1}");
                Reject<InvalidDataException>(() => new CharacterRepository(path), "引导迁移拒绝未来版本");

                void Sql(string sql) { using var db = Open(); using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
                object? Scalar(string sql) { using var db = Open(); using var command = db.CreateCommand(); command.CommandText = sql; return command.ExecuteScalar(); }
                SqliteConnection Open() { var db = new SqliteConnection("Data Source=" + path + ";Pooling=False"); db.Open(); return db; }
            }
        }
        finally { Directory.Delete(root, true); }
        Console.WriteLine($"PASS evildom war guide schema {Count - before}");
    }
}
