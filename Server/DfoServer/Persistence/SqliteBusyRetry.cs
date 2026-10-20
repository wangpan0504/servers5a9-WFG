using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

// SQLite 忙锁（SQLITE_BUSY=5 / SQLITE_LOCKED=6）表示写锁正被其他连接占用，不是连接或业务故障。
// 只对这两类错误做有界重试；约束冲突、损坏等其它 SqliteException 仍按原语义向上抛出。
internal static class SqliteBusyRetry
{
    const int SqliteBusy = 5;
    const int SqliteLocked = 6;

    // 各次重试前的等待（毫秒），合计不超过约 375ms；抖动避免同时撞锁的连接在固定间隔上再次互撞。
    internal static readonly int[] DelaysMilliseconds = [25, 50, 100, 200];

    // 单次业务包含首次尝试与全部重试，用于日志记录实际尝试次数。
    public static int MaxAttempts => DelaysMilliseconds.Length + 1;

    public static bool IsBusy(Exception error) =>
        error is SqliteException sqlite && sqlite.SqliteErrorCode is SqliteBusy or SqliteLocked;

    // 重试耗尽后仍抛出最后一次忙锁异常，由调用方决定记录并跳过当前请求。
    public static async Task<T> ExecuteAsync<T>(Func<Task<T>> work, CancellationToken stop)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return await work();
            }
            catch (SqliteException error) when (IsBusy(error) && attempt < MaxAttempts)
            {
                int delay = DelaysMilliseconds[attempt - 1];
                await Task.Delay(delay + Random.Shared.Next(delay / 2 + 1), stop);
            }
        }
    }

    // 分发层入口：忙锁重试耗尽时只回调一次并返回 false（调用方记录后跳过当前包，会话保持）；
    // 其它数据库错误不进入该分支，保持原有的向上抛出与断线语义。
    public static async Task<(bool Completed, T Value)> TryExecuteAsync<T>(Func<Task<T>> work, CancellationToken stop,
        Action<SqliteException, int>? exhausted = null)
    {
        try
        {
            return (true, await ExecuteAsync(work, stop));
        }
        catch (SqliteException error) when (IsBusy(error))
        {
            exhausted?.Invoke(error, MaxAttempts);
            return (false, default!);
        }
    }
}
