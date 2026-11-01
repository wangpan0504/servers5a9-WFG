using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal static class SqliteDatabaseBootstrap
{
    private static readonly object Gate = new();
    // 正式服务与自测进程复用连接：每次击杀、存档都会短开连接，非池化时每次都要重新打开文件、建 WAL 映射并重解析整套结构。
    // 离线工具（准备角色、导入等）保持非池化，备份或替换数据库文件时不会被池里的句柄占住。
    // 自测开池后，删除、复制或直接读取临时库文件之前必须先 ReleaseRuntimePool（测试侧统一经 TestFiles）。
    static volatile bool runtimePooling;

    public static void EnableRuntimePooling() => runtimePooling = true;

    // 停服前、自测删改库文件前释放池内句柄，让最后一个连接完成检查点；之后备份、复制或删除 game.db 拿到的是完整主库文件。
    // 正在使用的连接不受影响，归还时直接关闭而不再入池。
    public static void ReleaseRuntimePool()
    {
        if (runtimePooling) SqliteConnection.ClearAllPools();
    }

    public static string Initialize(string path, string? schemaPath = null)
    {
        path = Path.GetFullPath(path);
        // 命令超时与连接上的 busy_timeout 一致：Microsoft.Data.Sqlite 按该值重试 SQLITE_BUSY，
        // 因此连接串与 PRAGMA 必须同步，否则先到的 5 秒会盖掉新的忙等上限。
        string connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path, Pooling = runtimePooling, ForeignKeys = true, DefaultTimeout = 30
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
                    using var write = db.BeginWrite("SqliteDatabaseBootstrap.Initialize");
                    using var tx = write.Transaction;
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
                    // 池化时失败连接回到池里仍占着文件，先释放才能删掉本次新建的残缺库。
                    if (runtimePooling) SqliteConnection.ClearAllPools();
                    foreach (string suffix in new[] { "", "-wal", "-shm" })
                        File.Delete(path + suffix);
                }
                throw;
            }
        }
        return connectionString;
    }

    // 单文件发布只产出 exe；目录部署的程序集是 exe 旁边独立的 DfoServer.dll。
    static bool IsSingleFileBundle =>
        !File.Exists(Path.Combine(AppContext.BaseDirectory, typeof(SqliteDatabaseBootstrap).Assembly.GetName().Name + ".dll"));

    static string ReadSchema(string? explicitPath)
    {
        string path = explicitPath ?? Path.Combine(AppContext.BaseDirectory, "Sqlite", "game_schema.sql");
        // 显式 schema 路径不能因文件缺失而退回默认结构；目录发布保留外部 SQL。
        // 单文件发行包不读外置 Sqlite\：安装目录里上一版目录式部署留下的旧结构不得覆盖内置结构。
        if (explicitPath is not null || (!IsSingleFileBundle && File.Exists(path)))
            return File.ReadAllText(path);
        using var stream = typeof(SqliteDatabaseBootstrap).Assembly.GetManifestResourceStream("DfoServer.Sqlite.game_schema.sql")
            ?? throw new InvalidDataException("程序缺少内置数据库结构");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
