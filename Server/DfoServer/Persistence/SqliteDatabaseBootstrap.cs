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
                    string sql = ReadSchema(schemaPath);
                    using var tx = db.BeginTransaction();
                    // SQLite负责SQL语句边界，不能按分号拆开触发器的BEGIN/END块。
                    SqliteMigrations.Execute(db, tx, sql);
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

    static string ReadSchema(string? explicitPath)
    {
        string path = explicitPath ?? Path.Combine(AppContext.BaseDirectory, "Sqlite", "game_schema.sql");
        // 显式 schema 路径不能因文件缺失而退回默认结构；目录发布保留外部 SQL。
        if (explicitPath is not null || File.Exists(path))
            return File.ReadAllText(path);
        using var stream = typeof(SqliteDatabaseBootstrap).Assembly.GetManifestResourceStream("DfoServer.Sqlite.game_schema.sql")
            ?? throw new InvalidDataException("程序缺少内置数据库结构");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
