using System.Text.Json;

namespace DfoServer.Testing;

// 记录各组最近一次通过的耗时，供并行分片按耗时均衡；文件缺失或损坏时退回默认权重。
internal static class TcpTestTimings
{
    const string FileName = "timings.json";

    public static Dictionary<string, long> Load(string parent)
    {
        try
        {
            string path = Path.Combine(parent, FileName);
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
            var timings = Load(parent);
            foreach (string file in Directory.EnumerateFiles(root, "*.result.json", SearchOption.AllDirectories))
            {
                string name = Path.GetFileName(file)[..^".result.json".Length];
                if (name.Length == 0)
                    continue;
                using var document = JsonDocument.Parse(File.ReadAllText(file));
                if (document.RootElement.TryGetProperty("ElapsedMs", out var elapsed) && elapsed.TryGetInt64(out long ms))
                    timings[name] = ms;
            }
            string target = Path.Combine(parent, FileName);
            string temporary = target + "." + Environment.ProcessId + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(timings));
            File.Move(temporary, target, true);
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            Console.WriteLine("WARN TCP timings not recorded: " + exception.Message);
        }
    }
}
