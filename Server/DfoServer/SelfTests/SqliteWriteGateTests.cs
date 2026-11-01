using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using DfoServer.Persistence;
using DfoServer.Testing;
using Microsoft.Data.Sqlite;

namespace DfoServer;

// 进程内单写者闸门（写入准入）与慢库诊断：
// 并发写必须全部成功且严格串行，读在写事务持有期间不被阻塞，未提交事务回滚后闸门必须归还，
// 超过 1000ms 的写事务产生字段完整的 db_slow 事件。
internal static class SqliteWriteGateTests
{
    public static async Task RunAsync()
    {
        int before = TestCheck.Count;
        string root = Path.Combine(Path.GetTempPath(), "s5a9-db-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var events = new ConcurrentQueue<(string Kind, string Json)>();
        var previousSink = SqliteDbDiagnostics.Sink;
        try
        {
            string path = Path.Combine(root, "game.db");
            var database = new SqliteGameDatabase(path);
            SqliteDbDiagnostics.Sink = (kind, detail) => events.Enqueue((kind, JsonSerializer.Serialize(detail)));
            using (var db = database.Open())
            using (var write = db.BeginWrite("SqliteWriteGateTests.Schema"))
            using (var command = db.CreateCommand())
            {
                command.Transaction = write.Transaction;
                command.CommandText = "CREATE TABLE gate_probe(slot INTEGER PRIMARY KEY, value INTEGER NOT NULL)";
                command.ExecuteNonQuery();
                write.Transaction.Commit();
            }

            // ① 并发写：16 个任务各写不同行，全部成功、无 SQLITE_BUSY、耗时受控。
            int running = 0, maxRunning = 0, committed = 0;
            var failures = new ConcurrentQueue<Exception>();
            SqliteWriteGate.ResetPeak();
            using (var start = new ManualResetEventSlim())
            {
                var writers = Enumerable.Range(0, 16).Select(slot => Task.Run(() =>
                {
                    try
                    {
                        start.Wait();
                        using var db = database.Open();
                        using var write = db.BeginWrite("SqliteWriteGateTests.ConcurrentWrite");
                        int now = Interlocked.Increment(ref running);
                        int high;
                        while (now > (high = Volatile.Read(ref maxRunning)) &&
                            Interlocked.CompareExchange(ref maxRunning, now, high) != high) { }
                        using var command = db.CreateCommand();
                        command.Transaction = write.Transaction;
                        command.CommandText = "INSERT INTO gate_probe(slot,value) VALUES($slot,$value)";
                        command.Parameters.AddWithValue("$slot", slot);
                        command.Parameters.AddWithValue("$value", slot * 10);
                        command.ExecuteNonQuery();
                        write.Transaction.Commit();
                        Interlocked.Decrement(ref running);
                        Interlocked.Increment(ref committed);
                    }
                    catch (Exception error) { failures.Enqueue(error); }
                })).ToArray();
                var watch = Stopwatch.StartNew();
                start.Set();
                await Task.WhenAll(writers).WaitAsync(TimeSpan.FromSeconds(30));
                watch.Stop();
                TestCheck.That(failures.IsEmpty && committed == 16,
                    "16 个并发写任务全部提交，无 SQLITE_BUSY：" + (failures.IsEmpty ? "" : failures.First().Message));
                TestCheck.That(maxRunning == 1 && SqliteWriteGate.PeakWriters == 1 && running == 0,
                    $"并发写严格串行：事务内最大并发 {maxRunning}，闸门峰值 {SqliteWriteGate.PeakWriters}");
                TestCheck.That(watch.Elapsed < TimeSpan.FromSeconds(10), $"16 次串行写总耗时受控（{watch.Elapsed.TotalMilliseconds:F0}ms）");
            }
            using (var db = database.Open())
            using (var command = db.CreateCommand())
            {
                command.CommandText = "SELECT count(*) FROM gate_probe";
                TestCheck.That(Convert.ToInt32(command.ExecuteScalar()) == 16, "并发写落库 16 行，没有丢写或重写");
            }

            // ② 读并发：写事务持有闸门期间，只读事务不被闸门阻塞。
            using (var entered = new ManualResetEventSlim())
            using (var release = new ManualResetEventSlim())
            {
                var holder = Task.Run(() =>
                {
                    using var db = database.Open();
                    using var write = db.BeginWrite("SqliteWriteGateTests.HoldGate");
                    using var command = db.CreateCommand();
                    command.Transaction = write.Transaction;
                    command.CommandText = "UPDATE gate_probe SET value=value+1 WHERE slot=0";
                    command.ExecuteNonQuery();
                    entered.Set();
                    release.Wait(TimeSpan.FromSeconds(10));
                    write.Transaction.Commit();
                });
                TestCheck.That(entered.Wait(TimeSpan.FromSeconds(10)), "写事务已持有闸门");
                var watch = Stopwatch.StartNew();
                long count;
                using (var db = database.Open())
                using (var read = db.BeginRead())
                using (var command = db.CreateCommand())
                {
                    command.Transaction = read;
                    command.CommandText = "SELECT count(*) FROM gate_probe";
                    count = Convert.ToInt64(command.ExecuteScalar());
                }
                watch.Stop();
                TestCheck.That(count == 16 && watch.Elapsed < TimeSpan.FromMilliseconds(1000) &&
                    SqliteWriteGate.ActiveWriters == 1 && !SqliteWriteGate.HeldByCurrentThread,
                    $"写闸门持有期间读仍并发完成（{watch.Elapsed.TotalMilliseconds:F1}ms，在读 {count} 行）");
                release.Set();
                await holder.WaitAsync(TimeSpan.FromSeconds(10));
            }

            // ③ 同一线程重入不互等：外层持有写事务时再进写入口不应阻塞（内层是 deferred 只读快照）。
            using (var outerDb = database.Open())
            using (var outer = outerDb.BeginWrite("SqliteWriteGateTests.ReentrantOuter"))
            {
                var watch = Stopwatch.StartNew();
                using var innerDb = database.Open();
                using var inner = innerDb.BeginWrite("SqliteWriteGateTests.ReentrantInner", deferred: true);
                using var command = innerDb.CreateCommand();
                command.Transaction = inner.Transaction;
                command.CommandText = "SELECT count(*) FROM gate_probe";
                long visible = Convert.ToInt64(command.ExecuteScalar());
                watch.Stop();
                TestCheck.That(visible == 16 && watch.Elapsed < TimeSpan.FromMilliseconds(1000) &&
                    SqliteWriteGate.ActiveWriters == 1,
                    $"同一线程重入写入口不等待自身写锁（{watch.Elapsed.TotalMilliseconds:F1}ms）");
            }
            TestCheck.That(SqliteWriteGate.ActiveWriters == 0, "外层事务释放后闸门归还");

            // ④ 未提交的写事务随 using 回滚，闸门立即归还且不留半成品。
            bool rolledBack = false;
            try
            {
                using var db = database.Open();
                using var write = db.BeginWrite("SqliteWriteGateTests.Rollback");
                using var command = db.CreateCommand();
                command.Transaction = write.Transaction;
                command.CommandText = "INSERT INTO gate_probe(slot,value) VALUES(99,99)";
                command.ExecuteNonQuery();
                throw new InvalidOperationException("模拟提交前的业务失败");
            }
            catch (InvalidOperationException) { rolledBack = true; }
            TestCheck.That(rolledBack && SqliteWriteGate.ActiveWriters == 0, "业务异常回滚后写闸门立即归还");
            using (var db = database.Open())
            using (var write = db.BeginWrite("SqliteWriteGateTests.AfterRollback"))
            using (var command = db.CreateCommand())
            {
                command.Transaction = write.Transaction;
                command.CommandText = "SELECT count(*) FROM gate_probe WHERE slot=99";
                TestCheck.That(Convert.ToInt32(command.ExecuteScalar()) == 0, "回滚的写事务不遗留行，后续写可立即进入");
            }

            // ⑤ db_slow：真实写事务超过 1000ms 时产生字段完整的事件（含 SQL 头），快的写不产生噪音。
            events.Clear();
            using (var db = database.Open())
            using (var write = db.BeginWrite("SqliteWriteGateTests.SlowTransaction",
                sql: "UPDATE gate_probe SET value=value WHERE slot=16"))
            {
                using (var command = db.CreateCommand())
                {
                    command.Transaction = write.Transaction;
                    command.CommandText = "UPDATE gate_probe SET value=value WHERE slot=16";
                    command.ExecuteNonQuery();
                }
                // 模拟写事务里的重活（大表扫描/刷盘等待）：闸门按"准入→释放"计这段耗时。
                Thread.Sleep((int)SqliteDbDiagnostics.SlowThresholdMilliseconds + 100);
                write.Transaction.Commit();
            }
            var slow = events.Where(item => item.Kind == "db_slow").Select(item => item.Json).ToArray();
            TestCheck.That(slow.Length == 1, $"超过阈值的写事务只记一条 db_slow（实际 {slow.Length}）");
            using (var document = JsonDocument.Parse(slow[0]))
            {
                var detail = document.RootElement;
                TestCheck.That(detail.TryGetProperty("Stage", out var stage) && stage.GetString() == "write_transaction" &&
                    detail.TryGetProperty("Action", out var action) && action.GetString() == "SqliteWriteGateTests.SlowTransaction" &&
                    detail.TryGetProperty("ElapsedMs", out var elapsed) && elapsed.GetInt64() >= SqliteDbDiagnostics.SlowThresholdMilliseconds &&
                    detail.TryGetProperty("SqlHead", out var head) && head.GetString() == "UPDATE gate_probe SET value=value WHERE slot=16" &&
                    detail.TryGetProperty("Connection", out _),
                    "db_slow 字段完整：Stage/Action/ElapsedMs/SqlHead/Connection");
            }
            events.Clear();
            using (var db = database.Open())
            using (var write = db.BeginWrite("SqliteWriteGateTests.FastTransaction"))
            using (var command = db.CreateCommand())
            {
                command.Transaction = write.Transaction;
                command.CommandText = "UPDATE gate_probe SET value=value WHERE slot=15";
                command.ExecuteNonQuery();
                write.Transaction.Commit();
            }
            TestCheck.That(events.IsEmpty, "毫秒级写事务不产生 db_slow");

            // ⑥ SQL 头截断与空值：只取首个非空行、压平换行、最多 80 字符。
            string longSql = "SELECT " + new string('a', 200) + "\nFROM gate_probe";
            TestCheck.That(SqliteDbDiagnostics.Head(longSql).Length == SqliteDbDiagnostics.SqlHeadLength &&
                SqliteDbDiagnostics.Head("  \n\t ").Length == 4,
                "SQL 头截断到 80 字符，空白 SQL 记为 none");
            TestCheck.That(SqliteDbDiagnostics.Head("SELECT 1\n  FROM gate_probe") == "SELECT 1 FROM gate_probe",
                "多行 SQL 压成单行头部");
        }
        finally
        {
            SqliteDbDiagnostics.Sink = previousSink;
            TestFiles.DeleteDirectory(root, recursive: true);
        }
        Console.WriteLine($"PASS {TestCheck.Count - before} 单写者闸门串行、读并发、回滚归还与慢库诊断断言");
    }
}
