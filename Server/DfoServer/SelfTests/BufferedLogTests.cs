using static DfoServer.Testing.TestCheck;
using System.Text;
using System.Text.Json;
using DfoServer.Infrastructure;
using DfoServer.Testing;

namespace DfoServer;

internal static class BufferedLogTests
{
    public static async Task RunAsync()
    {
        int checks = TestCheck.Count;
        int opens = 0, failures = 0;
        var stream = new ProbeStream();
        using (var file = new BufferedLogFile(() => { opens++; return stream; }, _ => failures++, TimeSpan.FromMinutes(1)))
        {
            TestCheck.That(opens == 0, "unused log does not open a file");
            for (int i = 0; i < 1000; i++) file.Append("短记录\n");
            TestCheck.That(opens == 1 && stream.Length == 0, "small records share one handle and buffer");
            file.Flush();
            TestCheck.That(Encoding.UTF8.GetString(stream.ToArray()) == string.Concat(Enumerable.Repeat("短记录\n", 1000)), "explicit flush preserves UTF8 records without BOM");
            file.Append("退出尾部\n");
        }
        TestCheck.That(stream.Closed && failures == 0 && Encoding.UTF8.GetString(stream.ToArray()).EndsWith("退出尾部\n"), "dispose flushes tail and closes handle");

        var timerStream = new ProbeStream();
        using (var file = new BufferedLogFile(() => timerStream, _ => failures++, TimeSpan.FromMilliseconds(25)))
        {
            file.Append("timer\n");
            await timerStream.Flushed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            TestCheck.That(timerStream.ToArray().AsSpan().SequenceEqual("timer\n"u8), "low volume is flushed without another append");
        }

        var largeStream = new ProbeStream();
        using (var file = new BufferedLogFile(() => largeStream, _ => failures++, TimeSpan.FromMinutes(1)))
        {
            string record = new('x', 8192);
            for (int i = 0; i < 128; i++) file.Append(record);
            TestCheck.That(largeStream.Length >= 1024 * 1024 - 32768, "full buffers write incrementally without retaining a log queue");
        }
        TestCheck.That(largeStream.ToArray().Length == 1024 * 1024, "large log flushed completely");

        foreach (bool timerFailure in new[] { false, true })
        {
            int reports = 0, attempts = 0;
            var failed = new ProbeStream { FailFlush = true };
            var reported = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var file = new BufferedLogFile(() => { attempts++; return failed; }, _ =>
            {
                Interlocked.Increment(ref reports);
                reported.TrySetResult();
            }, timerFailure ? TimeSpan.FromMilliseconds(25) : TimeSpan.FromMinutes(1));
            file.Append("failed flush\n");
            if (!timerFailure) Reject<IOException>(file.Flush);
            await reported.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Reject<IOException>(() => file.Append("not retried\n"));
            Reject<IOException>(file.Flush);
            Reject<IOException>(file.Dispose);
            file.Dispose();
            TestCheck.That(reports == 1 && attempts == 1 && failed.Closed, "I/O failure is latched, reported once, and closes the file");
            Reject<ObjectDisposedException>(() => file.Append("after close"));
        }

        int denied = 0;
        var deniedFile = new BufferedLogFile(() => throw new UnauthorizedAccessException("synthetic"), _ => denied++, TimeSpan.FromMinutes(1));
        Reject<UnauthorizedAccessException>(() => deniedFile.Append("denied"));
        Reject<UnauthorizedAccessException>(deniedFile.Dispose);
        TestCheck.That(denied == 1, "open failure reported once");

        var writeFailure = new ProbeStream { FailWrite = true };
        int writeReports = 0;
        var failedWriter = new BufferedLogFile(() => writeFailure, _ => writeReports++, TimeSpan.FromMinutes(1));
        Reject<IOException>(() => failedWriter.Append(new string('x', 65536)));
        Reject<IOException>(failedWriter.Dispose);
        TestCheck.That(writeFailure.Closed && writeReports == 1, "full-buffer write failure closes and reports once");
        var closeFailure = new ProbeStream { FailFlush = true };
        int closeReports = 0;
        var failedClose = new BufferedLogFile(() => closeFailure, _ => closeReports++, TimeSpan.FromMinutes(1));
        failedClose.Append("tail");
        Reject<IOException>(failedClose.Dispose);
        failedClose.Dispose();
        TestCheck.That(closeFailure.Closed && closeReports == 1, "shutdown flush failure is surfaced once");

        // 失败通知可在另一线程访问写入器；不得持有文件锁调用外部回调。
        bool reentered = false;
        BufferedLogFile? reentrant = null;
        reentrant = new(() => throw new IOException("synthetic"), _ =>
            reentered = Task.Run(() =>
            {
                try { reentrant!.Dispose(); }
                catch (IOException) { }
            }).Wait(TimeSpan.FromSeconds(3)), TimeSpan.FromMinutes(1));
        Reject<IOException>(() => reentrant.Append("reentry"));
        TestCheck.That(reentered, "failure callback runs outside the file lock");
        reentrant.Dispose();

        string root = Path.Combine(Path.GetTempPath(), "buffered-log-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            string path = Path.Combine(root, "server.jsonl");
            File.WriteAllText(path, "{\"seed\":true}\n", new UTF8Encoding(false));
            using (var log = new ServerLog(root))
            {
                await Task.WhenAll(Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
                {
                    for (int i = 0; i < 80; i++) log.Write("并发", new { worker, i });
                })));
                log.Flush();
                using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
                TestCheck.That(reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length == 641, "flush exposes complete concurrent records to a live reader");
                log.Write("tail", new { Complete = true });
            }
            string[] lines = File.ReadAllLines(path);
            TestCheck.That(lines.Length == 642 && lines[0] == "{\"seed\":true}", "existing log is appended and close flushes the last event");
            var keys = new HashSet<(int, int)>();
            foreach (string line in lines.Skip(1).Take(640))
            {
                using var json = JsonDocument.Parse(line);
                var detail = json.RootElement.GetProperty("Detail");
                TestCheck.That(keys.Add((detail.GetProperty("worker").GetInt32(), detail.GetProperty("i").GetInt32())), "concurrent JSON records are complete and unique");
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        Console.WriteLine($"PASS {TestCheck.Count - checks} buffered log assertions");
    }

    sealed class ProbeStream : MemoryStream
    {
        public bool FailFlush { get; init; }
        public bool FailWrite { get; init; }
        public bool Closed { get; private set; }
        public TaskCompletionSource Flushed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override void Flush()
        {
            if (FailFlush) throw new IOException("synthetic flush failure");
            base.Flush();
            Flushed.TrySetResult();
        }
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (FailWrite) throw new IOException("synthetic write failure");
            base.Write(buffer, offset, count);
        }
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (FailWrite) throw new IOException("synthetic write failure");
            base.Write(buffer);
        }
        protected override void Dispose(bool disposing)
        {
            Closed = true;
            base.Dispose(disposing);
        }
    }
}
