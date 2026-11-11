using Microsoft.Data.Sqlite;
namespace DfoServer.Persistence;

internal sealed class SqliteGameDatabase
{
    readonly string connectionString;
    // 仅核心自测缩短命令忙等。0 表示无限等待，不能用来制造可观察的 SQLITE_BUSY。用完必须改回 null。
    internal static int? TestCommandTimeoutSeconds;
    public SqliteGameDatabase(string path)
    {
        connectionString = SqliteDatabaseBootstrap.Initialize(path);
    }

    public SqliteConnection Open() => Open(TestCommandTimeoutSeconds ?? 30, immediate: false);

    // 引导候战专用。busy_timeout 固定 0，不走 Microsoft.Data.Sqlite 按 CommandTimeout 秒睡眠的重试。
    // 用完必须把池化连接的忙等恢复成生产值，避免下一位借用者继承 0。
    internal ImmediateConnection OpenImmediate() => new(Open(0, immediate: true), (TestCommandTimeoutSeconds ?? 30) * 1000);

    SqliteConnection Open(int timeoutSeconds, bool immediate)
    {
        var db = new SqliteConnection(connectionString);
        try
        {
            db.Open();
            if (immediate)
            {
                // 0 在 CommandTimeout 里表示无限等待，所以这里不经过命令，直接改 sqlite 忙等。
                SQLitePCL.raw.sqlite3_busy_timeout(db.Handle, 0);
                return db;
            }
            db.DefaultTimeout = timeoutSeconds;
            using var command = db.CreateCommand();
            // 忙等只吸收其他连接的瞬时写锁；同连接内层另开写事务仍按并发规范禁止。
            // WAL 下 synchronous=NORMAL 已能保证崩溃一致性，同时减少每次提交的刷盘等待。
            // 池化复用时连接级设置会残留，ignore_check_constraints 也在此复位，避免异常路径把放宽的约束带给下一位使用者。
            // Microsoft.Data.Sqlite 执行命令时会用 CommandTimeout 覆盖 busy_timeout，所以两处一起改。
            command.CommandText = "PRAGMA busy_timeout=" + timeoutSeconds * 1000 + ";PRAGMA synchronous=NORMAL;PRAGMA foreign_keys=ON;PRAGMA ignore_check_constraints=0";
            command.ExecuteNonQuery();
            return db;
        }
        catch
        {
            db.Dispose();
            throw;
        }
    }
}

internal sealed class ImmediateConnection(SqliteConnection connection, int restoreBusyMilliseconds) : IDisposable
{
    internal SqliteConnection Connection => connection;
    bool disposed;
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { SQLitePCL.raw.sqlite3_busy_timeout(connection.Handle, restoreBusyMilliseconds); }
        catch (ObjectDisposedException) { }
        finally { connection.Dispose(); }
    }
}
