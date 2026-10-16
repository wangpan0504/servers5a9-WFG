using System.Text.Json;

namespace DfoServer.Testing;

// 记录各组最近一次通过的耗时，供并行分片按耗时均衡；文件缺失或损坏时退回默认权重。
// DFO_TCP_TIMINGS 指向多个工作树共用的耗时文件（Tools/Select-AffectedTests.ps1 默认设置）；本地 build/tcp-tests 文件同时保留。
internal static class TcpTestTimings
{
    const string FileName = "timings.json";
    internal const string SharedVariable = "DFO_TCP_TIMINGS";

    static string? SharedPath()
    {
        string? shared = Environment.GetEnvironmentVariable(SharedVariable);
        return string.IsNullOrWhiteSpace(shared) ? null : Path.GetFullPath(shared);
    }

    // 共享文件先读，本地文件覆盖同名组，保证本工作树的最新结果优先。
    public static Dictionary<string, long> Load(string parent)
    {
        var timings = SharedPath() is { } shared ? Read(shared) : new();
        foreach (var (name, ms) in Read(Path.Combine(parent, FileName)))
            timings[name] = ms;
        return timings;
    }

    static Dictionary<string, long> Read(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<Dictionary<string, long>>(File.ReadAllText(path)) ?? new();
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
        }
        return new();
    }

    public static void Record(string parent, string root)
    {
        try
        {
            var latest = new Dictionary<string, long>();
            foreach (string file in Directory.EnumerateFiles(root, "*.result.json", SearchOption.AllDirectories))
            {
                string name = Path.GetFileName(file)[..^".result.json".Length];
                if (name.Length == 0)
                    continue;
                using var document = JsonDocument.Parse(File.ReadAllText(file));
                if (document.RootElement.TryGetProperty("ElapsedMs", out var elapsed) && elapsed.TryGetInt64(out long ms))
                    latest[name] = ms;
            }
            Write(Path.Combine(parent, FileName), latest);
            if (SharedPath() is { } shared)
                Write(shared, latest);
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            Console.WriteLine("WARN TCP timings not recorded: " + exception.Message);
        }
    }

    // 写入前重新读取再合并，并发工作树只覆盖各自刚跑完的组；临时文件改名保证读者不见半截内容。
    static void Write(string target, IReadOnlyDictionary<string, long> latest)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var timings = Read(target);
        foreach (var (name, ms) in latest)
            timings[name] = ms;
        string temporary = target + "." + Environment.ProcessId + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(timings));
        File.Move(temporary, target, true);
    }
}
