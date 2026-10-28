using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class EvildomWarRecoverySchemaTests
{
    public static void RunCore()
    {
        int before = Count;
        string root = Path.Combine(Path.GetTempPath(), "S5A9-war-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (bool conflict in new[] { false, true })
            {
                string path = Path.Combine(root, conflict + ".db");
                _ = new CharacterRepository(path);
                That(Scalar("SELECT count(*) FROM character_evildom_war_seats") is 0L, "新建库建立空席位账本");
                Sql("DROP TABLE character_evildom_war_seats; DROP TABLE evildom_war_battles; PRAGMA user_version=115");
                if (conflict)
                {
                    Sql("CREATE TABLE character_evildom_war_seats(character_id INTEGER)");
                    Reject<InvalidDataException>(() => new CharacterRepository(path), "升级拒绝缺约束的同名席位表");
                    That(Scalar("PRAGMA user_version") is 115L &&
                        Scalar("SELECT count(*) FROM sqlite_schema WHERE name='evildom_war_battles'") is 0L,
                        "冲突时回滚整个恢复迁移及版本");
                    continue;
                }
                _ = new CharacterRepository(path);
                That(Scalar("PRAGMA user_version") is long version && version == SqliteMigrations.CurrentVersion,
                    "115升级到当前恢复版本");
                Sql("CREATE TRIGGER war_seat_reopen_probe BEFORE INSERT ON character_evildom_war_seats BEGIN SELECT RAISE(ABORT,'probe'); END");
                _ = new CharacterRepository(path);
                That(Scalar("SELECT count(*) FROM sqlite_schema WHERE name='war_seat_reopen_probe'") is 1L,
                    "重开保留已有席位表及其触发器");
                Sql($"PRAGMA user_version={SqliteMigrations.CurrentVersion + 1}");
                Reject<InvalidDataException>(() => new CharacterRepository(path), "恢复迁移拒绝未来版本");

                void Sql(string sql) { using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }
                object? Scalar(string sql) { using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = sql; return cmd.ExecuteScalar(); }
                SqliteConnection Open() { var db = new SqliteConnection("Data Source=" + path + ";Pooling=False"); db.Open(); return db; }
            }
        }
        finally { Directory.Delete(root, true); }
        Console.WriteLine($"PASS evildom war recovery schema {Count - before}");
    }
}
