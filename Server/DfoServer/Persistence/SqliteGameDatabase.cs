using Microsoft.Data.Sqlite;
namespace DfoServer.Persistence;

internal sealed class SqliteGameDatabase
{
    readonly string connectionString;
    public SqliteGameDatabase(string path)
    {
        connectionString = SqliteDatabaseBootstrap.Initialize(path);
    }

    public SqliteConnection Open()
    {
        var db = new SqliteConnection(connectionString);
        try
        {
            db.Open();
            using var command = db.CreateCommand();
            // 忙等只吸收其他连接的瞬时写锁；同连接内层另开写事务仍按并发规范禁止。
            // WAL 下 synchronous=NORMAL 已能保证崩溃一致性，同时减少每次提交的刷盘等待。
            // 池化复用时连接级设置会残留，ignore_check_constraints 也在此复位，避免异常路径把放宽的约束带给下一位使用者。
            command.CommandText = "PRAGMA busy_timeout=30000;PRAGMA synchronous=NORMAL;PRAGMA foreign_keys=ON;PRAGMA ignore_check_constraints=0";
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
