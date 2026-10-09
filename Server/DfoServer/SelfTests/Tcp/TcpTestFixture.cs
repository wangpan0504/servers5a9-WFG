using DfoServer.Game.Accounts;
using DfoServer.Development.Dungeons;
using Microsoft.Data.Sqlite;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using DfoServer.Game.Auth;
using DfoServer.Infrastructure;
using DfoServer.Persistence;

namespace DfoServer.Testing;

// 业务 TCP 共用只读资源，每组独占数据库、会话、日志和监听；进程入口与重启另用子进程。
internal sealed class TcpTestFixture : IAsyncDisposable
{
    public string Root { get; }
    public string Database => Path.Combine(Options.DataDirectory, "game.db");
    public ServerOptions Options { get; private set; }

    Process? process;
    Task? output, errors;
    Task? hosted;
    CancellationTokenSource? hostStop;
    readonly TcpTestResources? resources;
    readonly DfoServer.GameWorld.Quests.QuestCatalog? questCatalog;
    int cliSequence;
    readonly string pvfSource;
    string? previousPhase;
    public TcpTestFixture(ServerOptions source, string root, bool gm = false, TcpTestResources? resources = null,
        DfoServer.GameWorld.Quests.QuestCatalog? questCatalog = null)
    {
        this.resources = resources;
        this.questCatalog = questCatalog;
        Root = Path.GetFullPath(root);
        Directory.CreateDirectory(Root);
        pvfSource = source.PvfPath;
        TcpListener[] reservations = Enumerable.Range(0, gm ? 3 : 2).Select(_ => new TcpListener(IPAddress.Loopback, 0)).ToArray();
        try
        {
            foreach (var listener in reservations)
                listener.Start();
            int[] ports = reservations.Select(l => ((IPEndPoint)l.LocalEndpoint).Port).ToArray();
            Options = source with
            {
                ListenAddress = "127.0.0.1",
                AdvertisedAddress = "127.0.0.1",
                ChannelPort = ports[0],
                GamePort = ports[1],
                GmPort = gm ? ports[2] : 0,
                DataDirectory = Path.Combine(Root, "Data"),
                PvfPath = Path.Combine(Root, "Data", "Pvf", "Script.pvf"),
                LogDirectory = Path.Combine(Root, "Logs"),
                SessionTtlSeconds = 300
            };
        }
        finally
        {
            foreach (var listener in reservations)
                listener.Stop();
        }
    }

    ProcessStartInfo StartInfo(params string[] arguments)
    {
        PrepareRuntime();
        var info = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true
        };
        info.ArgumentList.Add(Path.Combine(Root, "DfoServer.dll"));
        info.WorkingDirectory = Root;
        foreach (string argument in new[] { "--server-ip", Options.AdvertisedAddress, "--listen-ip", Options.ListenAddress,
            "--channel-port", Options.ChannelPort.ToString(), "--game-port", Options.GamePort.ToString(),
            "--gm-port", Options.GmPort.ToString(), "--session-ttl", Options.SessionTtlSeconds.ToString() })
            info.ArgumentList.Add(argument);
        foreach (string argument in arguments)
            info.ArgumentList.Add(argument);
        if (Options.DevelopmentDungeonEntryProbe != DungeonEntryProbeMode.Disabled)
        {
            info.ArgumentList.Add("--dungeon-entry-probe");
            info.ArgumentList.Add(Options.DevelopmentDungeonEntryProbe.ToString());
        }
        return info;
    }

    public Task<string> CliAsync(string? input, params string[] arguments) => CliAsync(input, 0, arguments);
    public async Task<string> CliAsync(string? input, int expectedExitCode, params string[] arguments)
    {
        string prefix = Path.Combine(Root, "cli", (++cliSequence).ToString("D3"));
        Directory.CreateDirectory(Path.GetDirectoryName(prefix)!);
        var start = StartInfo(arguments);
        if (arguments.Length == 1 && arguments[0] == "--help")
        {
            start.ArgumentList.Clear();
            start.ArgumentList.Add(Path.Combine(Root, "DfoServer.dll"));
            start.ArgumentList.Add("--help");
        }
        using var child = Process.Start(start) ?? throw new IOException("CLI process did not start");
        var stdout = CopyOutputAsync(child.StandardOutput, prefix + ".stdout.txt");
        var stderr = CopyOutputAsync(child.StandardError, prefix + ".stderr.txt");
        try
        {
            if (input is not null)
                await child.StandardInput.WriteLineAsync(input);
            child.StandardInput.Close();
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync();
            }
            await Task.WhenAll(stdout, stderr);
        }
        TestCheck.That(child.ExitCode == expectedExitCode, "CLI exit: " + string.Join(' ', arguments) + ": " + ReadDiagnostic(prefix + ".stderr.txt"));
        if (new FileInfo(prefix + ".stdout.txt").Length > 64 * 1024)
            throw new InvalidDataException("CLI 输出超过 64 KiB，完整输出保存在 fixture 日志中");
        return File.ReadAllText(prefix + ".stdout.txt").Trim();
    }

    public async Task StartAsync(string phase = "initial", bool packetCapture = false, bool isolatedProcess = false)
    {
        if (process is not null || hosted is not null)
            throw new InvalidOperationException("Fixture already running");
        if (previousPhase is not null && Directory.Exists(Options.LogDirectory))
        {
            var archiveWait = Stopwatch.StartNew();
            while (true)
            {
                try
                {
                    Directory.Move(Options.LogDirectory, Path.Combine(Root, previousPhase));
                    break;
                }
                catch (IOException error) when (OperatingSystem.IsWindows() &&
                    (error.HResult & 0xFFFF) is 5 or 32 && archiveWait.Elapsed < TimeSpan.FromSeconds(5))
                {
                    // 原服务已完成退出；仅等待Windows短时访问/共享冲突，归档成功前不启动下一实例。
                    await Task.Delay(25);
                }
            }
        }
        previousPhase = phase;
        Directory.CreateDirectory(Options.LogDirectory);
        string ready = Path.Combine(Options.LogDirectory, "ready.json");
        if (File.Exists(ready))
            throw new InvalidOperationException("Fresh phase required");
        if (File.Exists(pvfSource) && !File.Exists(Options.PvfPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Options.PvfPath)!);
            LinkOrCopy(pvfSource, Options.PvfPath);
        }
        if (resources is not null && !isolatedProcess)
        {
            var quests = questCatalog ?? resources.Quests;
            hostStop = new();
            var options = Options with { PacketCaptureDirectory = packetCapture ? Options.LogDirectory : null };
            hosted = Task.Run(async () =>
            {
                Directory.CreateDirectory(options.DataDirectory);
                using var databaseLock = new FileStream(Path.Combine(options.DataDirectory, "server.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                using var log = new ServerLog(options.LogDirectory);
                await ServerRuntime.Create(options, resources.Archive, new CharacterRepository(Database, quests), resources.Towns,
                    resources.Characters, resources.Learning, resources.ItemResources, log, quests).RunAsync(hostStop.Token);
            });
        }
        else
        {
            if (questCatalog is not null) throw new InvalidOperationException("合成任务目录不能由真实子进程悄然替换");
            process = Process.Start(StartInfo(packetCapture ? ["--packet-capture"] : [])) ?? throw new IOException("Server process did not start");
            output = CopyOutputAsync(process.StandardOutput, Path.Combine(Options.LogDirectory, "stdout.txt"));
            errors = CopyOutputAsync(process.StandardError, Path.Combine(Options.LogDirectory, "stderr.txt"));
            process.StandardInput.Close();
        }
        try
        {
            var deadline = Stopwatch.StartNew();
            while (!File.Exists(ready))
            {
                if (hosted is { IsCompleted: true })
                {
                    await hosted;
                    throw new InvalidOperationException("Fixture stopped before ready: " + Root);
                }
                if (process is { HasExited: true })
                {
                    await errors!;
                    throw new InvalidOperationException("Fixture exited before ready: " + ReadDiagnostic(Path.Combine(Options.LogDirectory, "stderr.txt")));
                }
                if (deadline.Elapsed > TimeSpan.FromSeconds(45))
                    throw new TimeoutException("Fixture not ready: " + Root);
                await Task.Delay(50);
            }

            // ready.json 可能正处在 File.WriteAllText 的写入窗口。
            int pid = 0;
            while (pid == 0 && deadline.Elapsed < TimeSpan.FromSeconds(45))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(ready));
                    pid = doc.RootElement.GetProperty("Pid").GetInt32();
                }
                catch (Exception e) when (e is IOException or JsonException)
                {
                    await Task.Delay(20);
                }
            }
            TestCheck.That(pid == (process?.Id ?? Environment.ProcessId), "ready belongs to this test host");
        }
        catch
        {
            if (hosted is not null)
                await StopAsync();
            var child = process;
            process = null;
            if (child is not null)
                await FinishProcessAsync(child);
            throw;
        }
    }

    // 每个子进程使用独立的程序目录，按正式固定布局隔离 Data/Logs，不增加测试专用启动参数。
    void PrepareRuntime()
    {
        string source = Path.GetDirectoryName(typeof(TcpTestFixture).Assembly.Location)!;
        foreach (string file in Directory.EnumerateFiles(source))
        {
            string name = Path.GetFileName(file);
            if (name.EndsWith(".dll", StringComparison.Ordinal) || name is "DfoServer.exe" or "DfoServer.deps.json" or "DfoServer.runtimeconfig.json")
                LinkOrCopy(file, Path.Combine(Root, name));
        }
        foreach (string directory in new[] { "Resources", "runtimes", "Sqlite" })
        {
            string input = Path.Combine(source, directory);
            if (!Directory.Exists(input))
                continue;
            foreach (string file in Directory.EnumerateFiles(input, "*", SearchOption.AllDirectories))
            {
                string target = Path.Combine(Root, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                LinkOrCopy(file, target);
            }
        }
    }

    static void LinkOrCopy(string source, string target)
    {
        if (File.Exists(target))
            return;
        bool linked = OperatingSystem.IsWindows() ? CreateHardLinkW(target, source, IntPtr.Zero) : UnixLink(source, target) == 0;
        if (!linked)
            File.Copy(source, target);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateHardLinkW(string name, string existing, IntPtr security);
    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    static extern int UnixLink([MarshalAs(UnmanagedType.LPUTF8Str)] string existing, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    public async Task StopAsync()
    {
        if (hosted is not null)
        {
            try
            {
                hostStop!.Cancel();
                await hosted.WaitAsync(TimeSpan.FromSeconds(20));
                TestCheck.That(hosted.IsCompletedSuccessfully, "isolated listeners and sessions stopped cleanly");
            }
            finally
            {
                hosted = null;
                hostStop!.Dispose();
                hostStop = null;
            }
        }
        if (process is null)
            return;
        var child = process;
        process = null;
        try
        {
            if (!child.HasExited)
            {
                child.Refresh();
                File.WriteAllText(Path.Combine(Options.LogDirectory, "memory.json"), JsonSerializer.Serialize(new
                {
                    Pid = child.Id, PeakWorkingSetBytes = child.PeakWorkingSet64, PrivateBytesBeforeStop = child.PrivateMemorySize64
                }));
                File.WriteAllText(Path.Combine(Options.LogDirectory, "stop.request"), "C# TCP regression finished");
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            }

            await Task.WhenAll(output!, errors!);
            string stderr = Path.Combine(Options.LogDirectory, "stderr.txt");
            TestCheck.That(child.ExitCode == 0 && new FileInfo(stderr).Length == 0, "isolated server stopped cleanly: " + ReadDiagnostic(stderr));
        }
        finally
        {
            await FinishProcessAsync(child);
        }
    }

    async Task FinishProcessAsync(Process child)
    {
        try
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync();
            }
            await Task.WhenAll(output!, errors!);
        }
        finally
        {
            output = errors = null;
            child.Dispose();
        }
    }

    // 固定缓冲写磁盘，避免 ReadToEndAsync 随服务输出量长期保留字符串。
    internal static async Task CopyOutputAsync(TextReader reader, string path)
    {
        await using var writer = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read,
            4096, FileOptions.Asynchronous));
        char[] buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory())) != 0)
            await writer.WriteAsync(buffer.AsMemory(0, count));
    }

    static string ReadDiagnostic(string path)
    {
        using var reader = File.OpenText(path);
        char[] prefix = new char[4096];
        int count = reader.ReadBlock(prefix, 0, prefix.Length);
        return new string(prefix, 0, count) + (reader.Peek() < 0 ? "" : "（完整输出见 fixture 日志）");
    }

    public static object[][] Rows(string database, string sql, params object[] parameters)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = sql;
        for (int i = 0; i < parameters.Length; i++)
            command.Parameters.AddWithValue("$p" + i, parameters[i]);
        using var reader = command.ExecuteReader();
        var rows = new List<object[]>();
        while (reader.Read())
        {
            var row = new object[reader.FieldCount];
            reader.GetValues(row);
            rows.Add(row);
        }

        return rows.ToArray();
    }

    public object[][] Query(string sql, params object[] parameters) => Rows(Database, sql, parameters);
    public void Sql(string sql, params object[] parameters)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Database, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = sql;
        for (int i = 0; i < parameters.Length; i++)
            command.Parameters.AddWithValue("$p" + i, parameters[i]);
        command.ExecuteNonQuery();
    }
    public string Snapshot(string sql = "SELECT * FROM characters ORDER BY id", params object[] parameters) => JsonSerializer.Serialize(Query(sql, parameters));
    public JsonElement[] Events() => File.ReadLines(Path.Combine(Options.LogDirectory, "server.jsonl")).Select(line =>
    {
        using var doc = JsonDocument.Parse(line);
        return doc.RootElement.Clone();
    }).ToArray();
    public ValueTask DisposeAsync() => new(StopAsync());
}
