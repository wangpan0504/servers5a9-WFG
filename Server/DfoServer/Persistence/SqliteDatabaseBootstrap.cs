using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal static class SqliteDatabaseBootstrap
{
    private static readonly object Gate = new();

    public static string Initialize(string path, string? schemaPath = null)
    {
        path = Path.GetFullPath(path);
        string connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path, Pooling = false, ForeignKeys = true, DefaultTimeout = 5
        }.ToString();
        lock (Gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            bool existed = File.Exists(path);
            try
            {
                using var db = new SqliteConnection(connectionString);
                db.Open();
                if (existed)
                {
                    SqliteMigrations.Apply(db);
                }
                else
                {
                    string sql = File.ReadAllText(schemaPath ?? Path.Combine(AppContext.BaseDirectory, "Sqlite", "game_schema.sql"));
                    using var tx = db.BeginTransaction();
                    // 基线只含独立建表和索引语句；逐条执行，保证中途 DDL 错误不会被批处理吞掉。
                    foreach (string statement in sql.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                        SqliteMigrations.Execute(db, tx, statement);
                    SqliteMigrations.MarkCurrent(db, tx);
                    SqliteMigrations.ValidateSchema(db, tx);
                    tx.Commit();
                }
                using var wal = db.CreateCommand();
                wal.CommandText = "PRAGMA journal_mode=WAL";
                wal.ExecuteScalar();
            }
            catch
            {
                // 只清理本次失败创建的文件，绝不覆盖或删除已有数据库。
                if (!existed)
                {
                    foreach (string suffix in new[] { "", "-wal", "-shm" })
                        File.Delete(path + suffix);
                }
                throw;
            }
        }
        return connectionString;
    }
}
