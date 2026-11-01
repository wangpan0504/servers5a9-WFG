using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class GuildDungeonSchemaTests
{
    public static void RunCore()
    {
        int before = Count;
        string root = Path.Combine(Path.GetTempPath(), "guild-dungeon-schema-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (bool collision in new[] { false, true })
            {
                string path = Path.Combine(root, collision + ".db");
                _ = new CharacterRepository(path);
                using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False"); db.Open();
                void Sql(string sql) { using var cmd = db.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }
                long Number(string sql) { using var cmd = db.CreateCommand(); cmd.CommandText = sql; return Convert.ToInt64(cmd.ExecuteScalar()); }
                That(Number("SELECT count(*) FROM guild_dungeon_progress") == 0 &&
                    Number("SELECT count(*) FROM guild_dungeon_clear_receipts") == 0,
                    "新库创建空公会净化与实例回执，不补历史清图");
                Sql("DROP TABLE guild_dungeon_effective_progress; DROP TABLE guild_dungeon_reward_receipts; DROP TABLE guild_ranking_months; DROP TABLE guild_ranking_party_clears; DROP TABLE guild_hongbao_claims; DROP TABLE guild_hongbao_records; DROP TABLE guild_dungeon_clear_receipts; DROP TABLE guild_dungeon_progress; PRAGMA user_version=135");
                if (collision)
                {
                    Sql("CREATE TABLE guild_dungeon_clear_receipts(instance_id TEXT)");
                    Reject<InvalidDataException>(() => new CharacterRepository(path), "升级不覆盖同名损坏回执表");
                    That(Number("PRAGMA user_version") == 135 &&
                        Number("SELECT count(*) FROM sqlite_schema WHERE name='guild_dungeon_progress'") == 0,
                        "第二张表冲突使整笔迁移回滚，保留原版本");
                    continue;
                }
                _ = new CharacterRepository(path);
                That(Number("PRAGMA user_version") == SqliteMigrations.CurrentVersion,
                    "真实135库升级到公会进度结构");
                Sql("CREATE TRIGGER guild_reopen_probe BEFORE INSERT ON guild_dungeon_progress BEGIN SELECT RAISE(ABORT,'probe'); END");
                _ = new CharacterRepository(path);
                That(Number("SELECT count(*) FROM sqlite_schema WHERE name='guild_reopen_probe'") == 1,
                    "重开数据库保留业务表与触发器");
                Sql($"PRAGMA user_version={SqliteMigrations.CurrentVersion + 1}");
                Reject<InvalidDataException>(() => new CharacterRepository(path), "拒绝未来版本，不向下改写公会库");
            }
        }
        finally { TestFiles.DeleteDirectory(root, true); }
        Console.WriteLine($"PASS guild dungeon schema assertions {Count - before}");
    }
}
