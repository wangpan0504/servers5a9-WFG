using DfoServer.Game.Accounts;
using DfoServer.Development.Dungeons;
using Microsoft.Data.Sqlite;
using System.Diagnostics;
using System.Globalization;
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
    public DateTimeOffset UtcNow => (clock ?? TimeProvider.System).GetUtcNow();

    readonly Dictionary<byte, ushort[]> skillGuideIds = new();
    internal IReadOnlyList<ushort> SkillGuideIds(byte job)
    {
        if (resources is not null) return resources.Learning.SkillIds(job);
        if (skillGuideIds.TryGetValue(job, out var cached)) return cached;
        using var archive = PvfLib.PvfArchive.Open(pvfSource);
        var ids = PvfLib.PvfScript.List(archive.ReadText("skill/" + DfoServer.GameWorld.Characters.SkillLearningCatalog.Lists[job]))
            .Select(entry => checked((ushort)entry.Key)).Order().ToArray();
        skillGuideIds[job] = ids;
        return ids;
    }

    internal bool HasIncompleteHellQuest(ushort characterId)
    {
        var towns = townCatalog ?? resources?.Towns ?? throw new InvalidOperationException("选图预期需要本轮共享城镇资源");
        var position = Query("SELECT town,area FROM characters WHERE id=$p0", characterId).Single();
        if (towns.Areas[(Convert.ToByte(position[0]), Convert.ToByte(position[1]))].DungeonWorldMap is not { } world ||
            towns.DungeonMaps[world] is not { Error: null, HellDungeon: true } map || map.HellQuests.IsEmpty) return false;
        var completed = Query("""
            SELECT quest_id FROM character_completed_quests WHERE character_id=$p0
            UNION SELECT q.quest_id FROM account_completed_quests q
              JOIN characters c ON c.account=q.account WHERE c.id=$p0
            """, characterId).Select(row => Convert.ToInt32(row[0])).ToHashSet();
        return map.HellQuests.Any(quest => !completed.Contains(quest));
    }

    Process? process;
    Task? output, errors;
    Task? hosted;
    DfoServer.Network.ServerHost? runtimeHost;
    internal DfoServer.Game.Raids.LukeRaidDisconnectCandidate? LukeRejoinCandidate(ushort character)
    {
        var host = Volatile.Read(ref runtimeHost) ?? throw new InvalidOperationException("候选诊断需要同进程Runtime");
        if (!host.HasLukeRejoinDiagnostics) throw new InvalidOperationException("候选诊断委托尚未装配，不能当作无候选");
        return host.ReadLukeRejoinCandidate(character);
    }
    CancellationTokenSource? hostStop;
    readonly TcpTestResources? resources;
    internal byte[] ExpectedRecentEmoticons(ushort id) =>
        Query("SELECT e.entries FROM characters c JOIN account_recent_emoticons e ON e.account=c.account WHERE c.id=$p0", id)
            is [[byte[] entries]] ? entries : new byte[72];

    internal byte[]? ExpectedChatEmoticons(ushort id)
    {
        if (resources is null) return null;
        var character = Query("SELECT account,level FROM characters WHERE id=$p0", id).Single();
        var marriage = Query("SELECT r.aggregate_json FROM marriage_records r JOIN marriage_active_members m ON m.marriage_id=r.marriage_id WHERE m.character_id=$p0", id)
            .Select(row=>JsonSerializer.Deserialize<DfoServer.Game.Marriage.MarriageRecord>((string)row[0])!).FirstOrDefault();
        ulong low=0,high=0;byte tail=0;
        var rights = Query("SELECT premium_contracts FROM account_wallets WHERE account=$p0",character[0]);
        if (rights is [[string json]])
            foreach(var (type,expiry) in JsonSerializer.Deserialize<Dictionary<int,long>>(json)!)
            {
                if(expiry<=UtcNow.ToUnixTimeSeconds()) continue;
                if(type is >0 and <64) low|=1UL<<type;
                else if(type is >=64 and <128) high|=1UL<<(type-64);
                else if(type is >=128 and <132) tail|=(byte)(1<<(type-128));
            }
        var rows = resources.ChatEmoticons.Eligible(new DfoServer.GameWorld.Characters.ChatEmoticonEligibility(Convert.ToByte(character[1]),
            marriage is {State:4},marriage?.Members.GetValueOrDefault(id)?.LovePoints??0,low,high,tail));
        if (rows.IsEmpty) return null;
        var macros = Query("SELECT emoticon_id,macro FROM account_chat_emoticons WHERE account=$p0", character[0])
            .ToDictionary(row => Convert.ToUInt16(row[0]), row => (byte[])row[1]);
        return TcpPeer.Build(writer =>
        {
            writer.Write((ushort)rows.Length);
            for (int i = 0; i < rows.Length; i++)
            {
                writer.Write((ushort)i);
                writer.Write(rows[i].Id);
                byte[] text = macros.GetValueOrDefault(rows[i].Id) ?? [];
                writer.Write((uint)text.Length);
                writer.Write(text);
            }
        });
    }
    readonly DfoServer.GameWorld.Quests.QuestCatalog? questCatalog;
    readonly DfoServer.GameWorld.Towns.TownCatalog? townCatalog;
    readonly TimeProvider? clock;
    readonly DfoServer.Game.Raids.LukeRaidStartPolicy? lukeStartPolicy;
    readonly Func<int, int>? evildomHiddenDraw;
    readonly Func<DfoServer.GameWorld.Items.CashShopPromotionCatalog>? promotions;
    readonly bool enablePromotionPage;
    readonly Random? fiendwarMazeRandom;
    int cliSequence;
    readonly string pvfSource;
    string? previousPhase;
    public TcpTestFixture(ServerOptions source, string root, bool gm = false, TcpTestResources? resources = null,
        DfoServer.GameWorld.Quests.QuestCatalog? questCatalog = null, TimeProvider? clock = null, bool packetHttp = false,
        bool connectionAddress = false, DfoServer.Game.Raids.LukeRaidStartPolicy? lukeStartPolicy = null,
        Func<int, int>? evildomHiddenDraw = null, DfoServer.GameWorld.Towns.TownCatalog? townCatalog = null,
        Func<DfoServer.GameWorld.Items.CashShopPromotionCatalog>? promotions = null, bool sharedResourcePath = false, bool enablePromotionPage = false,
        Random? fiendwarMazeRandom = null)
    {
        if (sharedResourcePath && (resources is null ||
            !Path.GetFullPath(source.PvfPath).Equals(Path.GetFullPath(resources.PvfPath), StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Shared resource path requires the same read-only TcpTestResources");
        this.promotions = promotions;
        this.enablePromotionPage = enablePromotionPage;
        this.fiendwarMazeRandom = fiendwarMazeRandom;
        this.resources = resources;
        this.questCatalog = questCatalog;
        this.townCatalog = townCatalog;
        this.clock = clock;
        this.lukeStartPolicy = lukeStartPolicy;
        this.evildomHiddenDraw = evildomHiddenDraw;
        Root = Path.GetFullPath(root);
        Directory.CreateDirectory(Root);
        pvfSource = source.PvfPath;
        Options = AllocatePorts(source, gm, packetHttp) with
        {
            ListenAddress = connectionAddress ? "0.0.0.0" : "127.0.0.1",
            AdvertisedAddress = connectionAddress ? "connection" : "127.0.0.1",
            NativePort = 0,
            DataDirectory = Path.Combine(Root, "Data"),
            PvfPath = sharedResourcePath ? source.PvfPath : Path.Combine(Root, "Data", "Pvf", "Script.pvf"),
            LogDirectory = Path.Combine(Root, "Logs"),
            SessionTtlSeconds = 300
        };
    }

    static ServerOptions AllocatePorts(ServerOptions source, bool gm, bool packetHttp)
    {
        TcpListener[] reservations = Enumerable.Range(0, 3 + (gm ? 1 : 0) + (packetHttp ? 1 : 0)).Select(_ => new TcpListener(IPAddress.Loopback, 0)).ToArray();
        try
        {
            foreach (var listener in reservations)
                listener.Start();
            int[] ports = reservations.Select(l => ((IPEndPoint)l.LocalEndpoint).Port).ToArray();
            return source with
            {
                ChannelPort = ports[0],
                GamePort = ports[1],
                GmPort = gm ? ports[2] : 0,
                PacketHttpPort = packetHttp ? ports[gm ? 3 : 2] : 0,
                RechargeWebPort = ports[^1]
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
        bool singleFile = File.Exists(Path.Combine(Root, "S5A9Server.exe"));
        var info = new ProcessStartInfo(singleFile ? Path.Combine(Root, "S5A9Server.exe") :
            Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true
        };
        ConfigureChildHeapLimit(info, Environment.GetEnvironmentVariable("DFO_TCP_CHILD_GC_HEAP_HARD_LIMIT"));
        info.ArgumentList.Add(singleFile ? "--server" : Path.Combine(Root, "DfoServer.dll"));
        info.WorkingDirectory = Root;
        foreach (string argument in new[] { "--server-ip", Options.AdvertisedAddress, "--listen-ip", Options.ListenAddress,
            "--channel-port", Options.ChannelPort.ToString(), "--game-port", Options.GamePort.ToString(),
            "--gm-port", Options.GmPort.ToString(), "--recharge-web-port", Options.RechargeWebPort.ToString(), "--session-ttl", Options.SessionTtlSeconds.ToString() })
            info.ArgumentList.Add(argument);
        if (Options.PacketHttpPort != 0)
        {
            info.ArgumentList.Add("--packet-http-port");
            info.ArgumentList.Add(Options.PacketHttpPort.ToString());
        }
        foreach (string argument in arguments)
            info.ArgumentList.Add(argument);
        if (Options.DevelopmentDungeonEntryProbe != DungeonEntryProbeMode.Disabled)
        {
            info.ArgumentList.Add("--dungeon-entry-probe");
            info.ArgumentList.Add(Options.DevelopmentDungeonEntryProbe.ToString());
        }
        if (Options.EnableHostedDungeonEligibility)
        {
            info.ArgumentList.Add("--hosted-dungeon-eligibility");
            info.ArgumentList.Add("On");
        }
        if (!string.IsNullOrWhiteSpace(Options.HostedDungeonEligibilityHost))
        {
            info.ArgumentList.Add("--hosted-dungeon-eligibility-host");
            info.ArgumentList.Add(Options.HostedDungeonEligibilityHost);
        }
        return info;
    }

    internal static void ConfigureChildHeapLimit(ProcessStartInfo start, string? value)
    {
        if (string.IsNullOrEmpty(value))
            return;
        ReadOnlySpan<char> digits = value.AsSpan();
        if (digits.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            digits = digits[2..];
        if (!ulong.TryParse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ulong bytes) || bytes == 0)
            throw new InvalidDataException("DFO_TCP_CHILD_GC_HEAP_HARD_LIMIT 必须为正数十六进制字节数");
        // 仅覆盖真实fixture子进程，不修改持有共享资源的驱动环境。
        start.Environment["DOTNET_GCHeapHardLimit"] = value;
    }

    public Task<string> CliAsync(string? input, params string[] arguments) => CliAsync(input, 0, arguments);
    public async Task<string> CliAsync(string? input, int expectedExitCode, params string[] arguments)
    {
        string prefix = Path.Combine(Root, "cli", (++cliSequence).ToString("D3"));
        Directory.CreateDirectory(Path.GetDirectoryName(prefix)!);
        var start = StartInfo(arguments);
        if (arguments.Length == 1 && arguments[0] is "--help" or "--selftest-tcp-list-json")
        {
            start.ArgumentList.Clear();
            start.ArgumentList.Add(File.Exists(Path.Combine(Root, "S5A9Server.exe")) ? "--server" : Path.Combine(Root, "DfoServer.dll"));
            start.ArgumentList.Add(arguments[0]);
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

    public async Task StartAsync(string phase = "initial", bool packetCapture = false, bool isolatedProcess = false, TimeSpan? startupTimeout = null)
    {
        var startup = Stopwatch.StartNew();
        if (process is not null || hosted is not null)
            throw new InvalidOperationException("Fixture already running");
        // 停服后旧端口可能被系统重新分配；重启重新申请端口，数据库和程序目录仍沿用原夹具。
        if (previousPhase is not null)
            Options = AllocatePorts(Options, Options.GmPort != 0, Options.PacketHttpPort != 0);
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
        // 网吧身份线上默认开；TCP 组在临时数据目录显式关闭，保持既有选角、经验和虚弱断言不随线上默认值漂移。
        Directory.CreateDirectory(Options.DataDirectory);
        File.WriteAllText(Path.Combine(Options.DataDirectory, "pc-room.json"), "{\"enabled\":false}");
        if (File.Exists(pvfSource) && !File.Exists(Options.PvfPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Options.PvfPath)!);
            LinkOrCopy(pvfSource, Options.PvfPath);
        }
        if (resources is not null && !isolatedProcess)
        {
            var quests = (questCatalog ?? resources.Quests).WithCreatures(resources.ItemResources.Creatures);
            var promotionCatalog = promotions is null ? ServerRuntime.LoadCashShopPromotions(resources.ItemResources) : null;
            DfoServer.GameWorld.Items.CashShopPromotionCatalog CurrentPromotions() => promotions?.Invoke() ?? promotionCatalog!;
            TcpTestDatabaseTemplate.CopyIfMissing(Database, Path.GetDirectoryName(Root)!);
            hostStop = new();
            var options = Options with { PacketCaptureDirectory = packetCapture ? Options.LogDirectory : null };
            hosted = Task.Run(async () =>
            {
                Directory.CreateDirectory(options.DataDirectory);
                using var databaseLock = new FileStream(Path.Combine(options.DataDirectory, "server.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                using var log = new ServerLog(options.LogDirectory);
                // 与正式启动装配相同的成长及冒险团目录，不能把TCP中的账号经验默默禁用。
                var repository=new CharacterRepository(Database,quests,resources.Adventure,resources.Growth,
                    guildDungeonMileage: resources.ItemResources.GuildContents.DungeonMileage, promotions: CurrentPromotions, promotionClock: clock);
                // 旧测试允许先种角色再由原生登录自动注册；正式账本要求账号先存在。
                // 只补孤立的合成角色账号，不覆盖已有密码、禁用状态或 revision。
                var authentication = new AuthenticationService(new AuthenticationRepository(Database));
                foreach (var row in Query("SELECT DISTINCT c.account FROM characters c WHERE NOT EXISTS(SELECT 1 FROM accounts a WHERE a.account_id=c.account)"))
                {
                    string account = (string)row[0];
                    authentication.Create(account, account == LoginCredentials.DevelopmentAccount
                        ? LoginCredentials.DevelopmentDigest : NativeLoginWire.Digest);
                }
                repository.RecoverInterruptedFiendwar(UtcNow);
                repository.OpenAdventureLedger(DateTimeOffset.UtcNow);
                var runtime = ServerRuntime.Create(options, resources.Archive, repository, townCatalog ?? resources.Towns,
                    resources.Characters, resources.Learning, resources.ItemResources, log, quests, resources.Tutorials,
                    options.DevelopmentDungeonEntryProbe == DungeonEntryProbeMode.Disabled ? resources.TutorialScenes : null, resources.Adventure, resources.Expeditions, clock,resources.Growth,lukeStartPolicy,evildomHiddenDraw, promotions: CurrentPromotions, enablePromotionPage: enablePromotionPage, fiendwarMazeRandom: fiendwarMazeRandom);
                Volatile.Write(ref runtimeHost, runtime);
                try { await runtime.RunAsync(hostStop.Token); }
                finally { Volatile.Write(ref runtimeHost, null); }
            });
        }
        else
        {
            if (evildomHiddenDraw is not null) throw new InvalidOperationException("显式魔界随机源需要同进程Runtime装配");
            if (fiendwarMazeRandom is not null) throw new InvalidOperationException("显式漩涡迷宫随机源需要同进程Runtime装配");
            if (lukeStartPolicy is not null) throw new InvalidOperationException("显式Luke执行策略需要真实同进程Runtime装配");
            if (promotions is not null) throw new InvalidOperationException("合成促销目录不能由真实子进程悄然替换");
            if (questCatalog is not null) throw new InvalidOperationException("合成任务目录不能由真实子进程悄然替换");
            if (townCatalog is not null) throw new InvalidOperationException("合成城镇目录不能由真实子进程悄然替换");
            process = Process.Start(StartInfo(packetCapture ? ["--packet-capture"] : [])) ?? throw new IOException("Server process did not start");
            output = CopyOutputAsync(process.StandardOutput, Path.Combine(Options.LogDirectory, "stdout.txt"));
            errors = CopyOutputAsync(process.StandardError, Path.Combine(Options.LogDirectory, "stderr.txt"));
            process.StandardInput.Close();
        }
        try
        {
            var deadline = Stopwatch.StartNew();
            var readyTimeout = startupTimeout ?? TimeSpan.FromSeconds(45);
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
                if (deadline.Elapsed > readyTimeout)
                    throw new TimeoutException("Fixture not ready: " + Root + "; " +
                        ReadinessDiagnostic(phase, deadline.Elapsed, ready, Path.Combine(Options.LogDirectory, "server.jsonl"), process, hosted));
                await Task.Delay(50);
            }

            // ready.json 可能正处在 File.WriteAllText 的写入窗口。
            int pid = 0;
            while (pid == 0 && deadline.Elapsed < readyTimeout)
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
        Interlocked.Add(ref startupMs, startup.ElapsedMilliseconds);
        Interlocked.Increment(ref starts);
    }

    // 本驱动进程内所有 fixture 启动到 ready 的累计耗时与次数，供各组结果记录启动开销。
    static long startupMs, starts;
    internal static (long Milliseconds, long Count) StartupTotals => (Interlocked.Read(ref startupMs), Interlocked.Read(ref starts));

    // 每个子进程使用独立的程序目录，按正式固定布局隔离 Data/Logs，不增加测试专用启动参数。
    void PrepareRuntime()
    {
        string source = AppContext.BaseDirectory;
        if (System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name == "S5A9Server")
        {
            LinkOrCopy(Environment.ProcessPath ?? throw new InvalidDataException("单文件测试缺少 host 路径"),
                Path.Combine(Root, "S5A9Server.exe"));
            return;
        }
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

    // 只终止本夹具创建且仍持有句柄的独立服务，验证无会话收尾的真实进程中断。
    internal async Task CrashAsync()
    {
        if (hosted is not null || process is null || process.HasExited)
            throw new InvalidOperationException("崩溃测试要求本夹具仍存活的独立子进程");
        var child = process; process = null;
        try
        {
            child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            TestCheck.That(child.ExitCode != 0, "原服务未执行正常退出便被精确终止");
        }
        finally { await FinishProcessAsync(child); }
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

    // 只在原45秒门槛已失败时取证，不等待日志、不重试启动，也不改变原有退出清理。
    internal static string ReadinessDiagnostic(string phase, TimeSpan elapsed, string ready, string log,
        Process? child = null, Task? hosted = null)
    {
        try
        {
            string processState = "none";
            if (child is not null)
            {
                try
                {
                    int pid = child.Id;
                    bool exited = child.HasExited;
                    processState = $"Pid={pid};HasExited={exited};ExitCode={(exited ? child.ExitCode.ToString(CultureInfo.InvariantCulture) : "not-exited")}";
                }
                catch (Exception error) { processState = "unavailable:" + error.GetType().Name; }
            }
            return JsonSerializer.Serialize(new
            {
                ObservedUtc = DateTimeOffset.UtcNow, Phase = phase.Length <= 160 ? phase : phase[..160], ElapsedMs = elapsed.TotalMilliseconds,
                ReadyExists = File.Exists(ready), Process = processState, Hosted = hosted?.Status.ToString() ?? "none",
                ChildMetrics = ReadChildMetrics(child), Startup = ReadStartupDiagnostic(log)
            });
        }
        catch (Exception error) { return "readiness-diagnostic-unavailable:" + error.GetType().Name; }
    }

    internal sealed record ChildMetrics(int? Pid, double? CpuMs, long? WorkingSetBytes, long? PrivateBytes, string? Error);

    // 只读本夹具持有的子进程句柄，不枚举其他进程或冒充跨进程GC数据。
    internal static ChildMetrics ReadChildMetrics(Process? child)
    {
        if (child is null) return new(null, null, null, null, "no-child");
        int? pid = null;
        try
        {
            pid = child.Id;
            child.Refresh();
            return new(pid, child.TotalProcessorTime.TotalMilliseconds, child.WorkingSet64, child.PrivateMemorySize64, null);
        }
        catch (Exception error) { return new(pid, null, null, null, error.GetType().Name); }
    }

    internal sealed record StartupDiagnostic(long? FileBytes, int ReadBytes, bool TailOnly, string? Time,
        string? Stage, string? State, double? ElapsedMs, string? Error);

    // 活动日志只读最多64KiB尾部；不借用Events整份加载，半行/截断行跳过且不输出原始日志正文。
    internal static StartupDiagnostic ReadStartupDiagnostic(string path)
    {
        const int budget = 64 * 1024;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long length = stream.Length, offset = Math.Max(0, length - budget);
            stream.Seek(offset, SeekOrigin.Begin);
            byte[] bytes = new byte[(int)Math.Min(length, budget)];
            int count = 0, read;
            while (count < bytes.Length && (read = stream.Read(bytes, count, bytes.Length - count)) > 0) count += read;
            string text = System.Text.Encoding.UTF8.GetString(bytes, 0, count);
            if (offset > 0)
            {
                int newline = text.IndexOf('\n');
                text = newline < 0 ? "" : text[(newline + 1)..];
            }
            string? time = null, stage = null, state = null;
            double? milliseconds = null;
            bool found = false;
            foreach (string line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object || Value(root, "Kind") != "startup" ||
                        !root.TryGetProperty("Detail", out var detail) || detail.ValueKind != JsonValueKind.Object) continue;
                    time = Value(root, "Time"); stage = Value(detail, "Stage"); state = Value(detail, "State");
                    milliseconds = detail.TryGetProperty("ElapsedMs", out var number) && number.ValueKind == JsonValueKind.Number &&
                        number.TryGetDouble(out double value) && double.IsFinite(value) ? value : null;
                    found = true;
                }
                catch (JsonException) { /* 当前写入中的半行不能覆盖此前完整启动事件。 */ }
            }
            return new(length, count, offset > 0, time, stage, state, milliseconds, found ? null : "no-complete-startup-in-tail");
        }
        catch (Exception error) { return new(null, 0, false, null, null, null, null, error.GetType().Name); }

        static string? Value(JsonElement row, string property)
        {
            if (!row.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String) return null;
            string text = value.GetString()!;
            return text.Length <= 160 ? text : text[..160];
        }
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
    public JsonElement[] Events()
    {
        // 运行中的日志仍持写句柄；共享写读取，仅消费已落盘且带换行的完整记录。
        using var stream = new FileStream(Path.Combine(Options.LogDirectory, "server.jsonl"),
            FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        string text = reader.ReadToEnd();
        int completeLength = text.LastIndexOf('\n') + 1;
        return text[..completeLength].Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line =>
        {
            using var doc = JsonDocument.Parse(line);
            return doc.RootElement.Clone();
        }).ToArray();
    }
    public ValueTask DisposeAsync() => new(StopAsync());
}
