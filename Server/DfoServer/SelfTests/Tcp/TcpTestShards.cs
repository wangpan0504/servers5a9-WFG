using System.Diagnostics;
using System.Text.Json;

namespace DfoServer.Testing;

// 按组分片到多个独立驱动子进程：进程隔离静态状态、端口与数据库，各分片输出写文件后整段回显，避免交错。
internal static class TcpTestShards
{
    internal const string ShardRootVariable = "DFO_TCP_SHARD_ROOT";
    internal const string ParallelVariable = "DFO_TCP_PARALLEL";
    const long DefaultWeightMs = 10_000;

    // 默认 min(4, 逻辑核/2)，并行开发会话较多时保持温和的 CPU 和内存占用；1 表示串行。
    public static int Parallelism()
    {
        if (int.TryParse(Environment.GetEnvironmentVariable(ParallelVariable), out int configured) && configured > 0)
            return configured;
        return Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
    }

    // 最长处理时间优先（LPT）：按上次耗时从大到小放入当前最轻的分片。
    public static List<string>[] Plan(IReadOnlyList<string> groups, int shards, IReadOnlyDictionary<string, long> timings)
    {
        var plan = Enumerable.Range(0, shards).Select(_ => new List<string>()).ToArray();
        var load = new long[shards];
        foreach (string group in groups.OrderByDescending(group => timings.TryGetValue(group, out long ms) ? ms : DefaultWeightMs)
            .ThenBy(group => group, StringComparer.Ordinal))
        {
            int target = Array.IndexOf(load, load.Min());
            plan[target].Add(group);
            load[target] += timings.TryGetValue(group, out long ms) ? ms : DefaultWeightMs;
        }
        return plan.Where(shard => shard.Count != 0).ToArray();
    }

    public static async Task RunAsync(string root, IReadOnlyList<string> groups, int parallel, IReadOnlyDictionary<string, long> timings)
    {
        var plan = Plan(groups, parallel, timings);
        Console.WriteLine($"TCP parallel: {plan.Length} shard(s) for {groups.Count} group(s); {ParallelVariable}=1 runs serially");
        var watch = Stopwatch.StartNew();
        var results = await Task.WhenAll(plan.Select((shard, index) => RunShardAsync(root, index, shard)));
        int assertions = 0;
        var failures = new List<string>();
        foreach (var result in results)
        {
            Console.WriteLine($"----- TCP shard {result.Index} ({string.Join(',', result.Groups)}): exit {result.ExitCode}, {result.Elapsed.TotalSeconds:F1}s");
            foreach (string line in File.ReadLines(result.Output))
                Console.WriteLine(line);
            string error = File.ReadAllText(result.Error);
            if (error.Length != 0)
                Console.Error.Write(error);
            if (result.ExitCode != 0 || result.Assertions is null)
                failures.Add($"shard {result.Index} [{string.Join(',', result.Groups)}] exit {result.ExitCode}: {result.Directory}");
            else
                assertions += result.Assertions.Value;
        }
        if (failures.Count != 0)
            throw new InvalidOperationException("FAIL: TCP shard(s) failed:" + Environment.NewLine + string.Join(Environment.NewLine, failures));
        TestCheck.That(true, "parallel TCP shards completed");
        File.WriteAllText(Path.Combine(root, "result.json"), JsonSerializer.Serialize(new
        {
            Passed = true, Assertions = assertions, Groups = groups, Shards = plan.Length, ElapsedMs = watch.ElapsedMilliseconds,
            Runtime = Environment.Version.ToString(), OS = Environment.OSVersion.ToString()
        }));
        Console.WriteLine($"PASS {assertions} C# TCP assertions in {plan.Length} shard(s), {watch.Elapsed.TotalSeconds:F1}s; {root}");
    }

    sealed record ShardResult(int Index, IReadOnlyList<string> Groups, string Directory, string Output, string Error, int ExitCode, int? Assertions, TimeSpan Elapsed);

    static async Task<ShardResult> RunShardAsync(string root, int index, IReadOnlyList<string> groups)
    {
        string directory = Path.Combine(root, "shard-" + index);
        Directory.CreateDirectory(directory);
        string output = Path.Combine(directory, "driver.stdout.txt"), error = Path.Combine(directory, "driver.stderr.txt");
        var start = ChildStartInfo(string.Join(',', groups));
        start.Environment[ShardRootVariable] = directory;
        start.Environment[ParallelVariable] = "1";
        var watch = Stopwatch.StartNew();
        using var process = Process.Start(start) ?? throw new IOException("TCP shard process did not start");
        var copyOutput = TcpTestFixture.CopyOutputAsync(process.StandardOutput, output);
        var copyError = TcpTestFixture.CopyOutputAsync(process.StandardError, error);
        process.StandardInput.Close();
        try
        {
            await process.WaitForExitAsync();
        }
        finally
        {
            // 父驱动异常退出时不遗留分片及其服务子进程。
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            await Task.WhenAll(copyOutput, copyError);
        }
        int? assertions = null;
        string result = Path.Combine(directory, "result.json");
        if (process.ExitCode == 0 && File.Exists(result))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(result));
            if (document.RootElement.GetProperty("Passed").GetBoolean())
                assertions = document.RootElement.GetProperty("Assertions").GetInt32();
        }
        return new(index, groups, directory, output, error, process.ExitCode, assertions, watch.Elapsed);
    }

    // 复用当前驱动的启动方式与运行参数，只替换测试组选择。
    static ProcessStartInfo ChildStartInfo(string selection)
    {
        string[] arguments = Environment.GetCommandLineArgs()[1..];
        int action = Array.IndexOf(arguments, "--selftest-tcp");
        if (action < 0)
            throw new InvalidOperationException("并行 TCP 需要 --selftest-tcp 命令行入口");
        string host = Environment.ProcessPath ?? throw new InvalidOperationException("缺少当前进程路径");
        var start = new ProcessStartInfo(host)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Environment.CurrentDirectory,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8
        };
        if (Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, typeof(TcpTestShards).Assembly.GetName().Name + ".dll"));
        foreach (string argument in arguments[..(action + 1)])
            start.ArgumentList.Add(argument);
        start.ArgumentList.Add(selection);
        return start;
    }
}
