using System.Diagnostics;
using DfoServer.Persistence;
using DfoServer.Testing;
using Microsoft.Data.Sqlite;

namespace DfoServer;

// 数据库忙锁（SQLITE_BUSY=5 / SQLITE_LOCKED=6）不得把玩家连接踢下线：
// 覆盖连接级等待设置、真实忙锁下的重试恢复、重试上限，以及分发层“记录后跳过本包”的边界。
internal static class SqliteBusyRetryTests
{
    public static async Task RunAsync()
    {
        int before = TestCheck.Count;
        string root = Path.Combine(Path.GetTempPath(), "s5a9-db-busy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "game.db");
            var database = new SqliteGameDatabase(path);
            using (var connection = database.Open())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "PRAGMA busy_timeout";
                TestCheck.That(Convert.ToInt32(command.ExecuteScalar()) == 30000, "连接忙等上限为 30000ms");
                command.CommandText = "PRAGMA synchronous";
                TestCheck.That(Convert.ToInt32(command.ExecuteScalar()) == 1, "WAL 下连接使用 synchronous=NORMAL");
                command.CommandText = "PRAGMA foreign_keys";
                TestCheck.That(Convert.ToInt32(command.ExecuteScalar()) == 1, "忙等设置不关闭外键");
                command.CommandText = "PRAGMA journal_mode";
                TestCheck.That(string.Equals(Convert.ToString(command.ExecuteScalar()), "wal", StringComparison.OrdinalIgnoreCase),
                    "忙等设置不改变 WAL 日志模式");
                command.CommandText = "CREATE TABLE busy_probe(value INTEGER PRIMARY KEY)";
                command.ExecuteNonQuery();
            }

            // 真实忙锁：A 用 BEGIN IMMEDIATE 持有写锁，B 的命令超时取 1 秒、busy_timeout 取 0，
            // 让每次尝试在可观测的有界时间内失败，由重试梯度而不是 SQLite 忙等决定恢复时机。
            using var writer = Open(path, timeoutSeconds: 30);
            using var writeLock = writer.BeginTransaction(deferred: false);
            using (var seed = writer.CreateCommand())
            {
                seed.Transaction = writeLock;
                seed.CommandText = "INSERT INTO busy_probe(value) VALUES(1)";
                seed.ExecuteNonQuery();
            }
            using var blocked = Open(path, timeoutSeconds: 1);
            using (var pragma = blocked.CreateCommand())
            {
                pragma.CommandText = "PRAGMA busy_timeout=0";
                pragma.ExecuteNonQuery();
            }

            bool sawBusy = false;
            try
            {
                using var plain = blocked.CreateCommand();
                plain.CommandText = "INSERT INTO busy_probe(value) VALUES(2)";
                plain.ExecuteNonQuery();
            }
            catch (SqliteException error)
            {
                sawBusy = SqliteBusyRetry.IsBusy(error);
            }
            TestCheck.That(sawBusy, "同一写锁下没有重试的写直接抛 SQLITE_BUSY（旧路径据此终结连接）");

            int attempts = 0;
            using var retrying = new ManualResetEventSlim();
            var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task release = Task.Run(() =>
            {
                // 等重试真正进入第二次尝试再释放写锁；固定睡眠不能证明先后关系。
                TestCheck.That(retrying.Wait(TimeSpan.FromSeconds(20)), "重试路径已进入第二次尝试");
                writeLock.Commit();
                committed.SetResult();
            });
            var (completed, _) = await SqliteBusyRetry.TryExecuteAsync(async () =>
            {
                int attempt = Interlocked.Increment(ref attempts);
                if (attempt == 2)
                {
                    retrying.Set();
                    await committed.Task;
                }
                using var write = blocked.CreateCommand();
                write.CommandText = "INSERT INTO busy_probe(value) VALUES(2)";
                write.ExecuteNonQuery();
                return true;
            }, CancellationToken.None);
            await release.WaitAsync(TimeSpan.FromSeconds(20));
            TestCheck.That(completed && attempts >= 2, "真实忙锁下新重试路径等 A 提交后成功写入，而不是抛 SQLITE_BUSY");

            // 重试上限：耗尽时以忙锁异常结束，不是无限等待。
            int exhaustedAttempts = 0;
            var watch = Stopwatch.StartNew();
            bool bounded = false;
            try
            {
                await SqliteBusyRetry.ExecuteAsync<bool>(async () =>
                {
                    Interlocked.Increment(ref exhaustedAttempts);
                    await Task.Yield();
                    throw new SqliteException("SQLite Error 5: 'database is locked'.", 5);
                }, CancellationToken.None);
            }
            catch (SqliteException error)
            {
                watch.Stop();
                bounded = SqliteBusyRetry.IsBusy(error) && exhaustedAttempts == SqliteBusyRetry.MaxAttempts &&
                    watch.Elapsed < TimeSpan.FromSeconds(5);
            }
            TestCheck.That(bounded, "忙锁重试共 5 次尝试后以忙锁异常结束，耗时仍有界");

            // 分发层边界：忙锁耗尽只回调一次并返回“本包未完成”，会话循环据此跳过该包继续读下一帧。
            int reports = 0, reportedAttempts = 0;
            var (skipped, _) = await SqliteBusyRetry.TryExecuteAsync<bool>(async () =>
            {
                await Task.Yield();
                throw new SqliteException("SQLite Error 6: 'database table is locked'.", 6);
            }, CancellationToken.None, (error, count) =>
            {
                reports++;
                reportedAttempts = count;
                TestCheck.That(SqliteBusyRetry.IsBusy(error), "耗尽回调收到忙锁异常");
            });
            TestCheck.That(!skipped && reports == 1 && reportedAttempts == SqliteBusyRetry.MaxAttempts,
                "分发层忙锁耗尽只记录一次并跳过本包，不把异常抛给会话循环");

            int lockedAttempts = 0;
            var (recovered, value) = await SqliteBusyRetry.TryExecuteAsync<bool>(async () =>
            {
                await Task.Yield();
                if (Interlocked.Increment(ref lockedAttempts) < 3)
                    throw new SqliteException("SQLite Error 6: 'database table is locked'.", 6);
                return true;
            }, CancellationToken.None);
            TestCheck.That(recovered && value && lockedAttempts == 3, "SQLITE_LOCKED 与 SQLITE_BUSY 一样按梯度重试后恢复");

            // 非忙锁数据库错误不重试、不吞掉：约束错误必须继续可见，保持原有断线语义。
            int constraintAttempts = 0;
            bool propagated = false;
            try
            {
                await SqliteBusyRetry.TryExecuteAsync<bool>(async () =>
                {
                    Interlocked.Increment(ref constraintAttempts);
                    await Task.Yield();
                    throw new SqliteException("SQLite Error 19: 'UNIQUE constraint failed'.", 19);
                }, CancellationToken.None, (_, _) => { });
            }
            catch (SqliteException error)
            {
                propagated = error.SqliteErrorCode == 19;
            }
            TestCheck.That(propagated && constraintAttempts == 1, "非忙锁数据库错误只尝试一次并继续向上抛出");
        }
        finally { Directory.Delete(root, recursive: true); }
        Console.WriteLine($"PASS {TestCheck.Count - before} 数据库忙锁重试、连接设置与丢弃边界断言");
    }

    static SqliteConnection Open(string path, int timeoutSeconds)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Pooling = false, DefaultTimeout = timeoutSeconds
        }.ToString());
        connection.Open();
        return connection;
    }
}
