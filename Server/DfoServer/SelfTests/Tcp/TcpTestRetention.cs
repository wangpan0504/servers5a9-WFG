using System.Text.Json;

namespace DfoServer.Testing;

// TCP 现场默认授权回收：通过的轮次立即删除；失败轮次保留最近 N 个、D 天内且总量不超过上限，最新一个失败现场始终保留。
// 只处理 build/tcp-tests 下 GUID 命名的直接子目录，跳过运行中（.running 被占用）的轮次，不跟随符号链接或 junction。
internal static class TcpTestRetention
{
    internal sealed record Policy(int KeepFailed, TimeSpan MaxAge, long MaxBytes)
    {
        public static Policy FromEnvironment() => new(
            (int)Math.Max(1, Number("DFO_TCP_KEEP_FAILED", 5)),
            TimeSpan.FromDays(Math.Max(0, Number("DFO_TCP_KEEP_DAYS", 3))),
            (long)(Math.Max(0, Number("DFO_TCP_MAX_GB", 5)) * 1024 * 1024 * 1024));

        static double Number(string name, double fallback) =>
            double.TryParse(Environment.GetEnvironmentVariable(name), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double value) ? value : fallback;
    }

    const string RunningMarker = ".running";

    public static IDisposable MarkRunning(string root) => new FileStream(Path.Combine(root, RunningMarker), FileMode.Create,
        FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);

    public static IReadOnlyList<string> Apply(string parent, Policy policy, DateTime? now = null)
    {
        var removed = new List<string>();
        if (!Directory.Exists(parent))
            return removed;
        DateTime current = now ?? DateTime.UtcNow;
        var failed = new List<(string Path, DateTime Time, long Size)>();
        foreach (string directory in Directory.EnumerateDirectories(parent))
        {
            if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out _) ||
                (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0 || IsRunning(directory))
                continue;
            if (Passed(directory))
                TryDelete(parent, directory, removed);
            else
                failed.Add((directory, Directory.GetLastWriteTimeUtc(directory), Size(directory)));
        }
        long total = 0;
        int kept = 0;
        foreach (var run in failed.OrderByDescending(run => run.Time))
        {
            bool newest = kept == 0;
            bool keep = newest || kept < policy.KeepFailed && current - run.Time <= policy.MaxAge && total + run.Size <= policy.MaxBytes;
            if (keep)
            {
                kept++;
                total += run.Size;
            }
            else
                TryDelete(parent, run.Path, removed);
        }
        if (removed.Count != 0)
            Console.WriteLine($"TCP retention removed {removed.Count} old run(s); kept {kept} failed run(s) under {parent}");
        return removed;
    }

    static bool IsRunning(string directory)
    {
        string marker = Path.Combine(directory, RunningMarker);
        if (!File.Exists(marker))
            return false;
        try
        {
            using var probe = new FileStream(marker, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    static bool Passed(string directory)
    {
        string result = Path.Combine(directory, "result.json");
        if (!File.Exists(result))
            return false;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(result));
            return document.RootElement.TryGetProperty("Passed", out var passed) && passed.ValueKind == JsonValueKind.True;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // 按文件路径长度保守计量，PVF在硬链接失败后可能是完整副本，不能按扩展名忽略。
    static long Size(string directory)
    {
        long size = 0;
        foreach (string path in Directory.EnumerateFileSystemEntries(directory))
        {
            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                continue;
            if ((attributes & FileAttributes.Directory) != 0)
                size += Size(path);
            else
                size += new FileInfo(path).Length;
        }
        return size;
    }

    static void TryDelete(string parent, string directory, List<string> removed)
    {
        try
        {
            DeleteRun(parent, directory);
            removed.Add(directory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine("WARN TCP retention skipped " + directory + ": " + exception.Message);
        }
    }

    public static void DeleteRun(string parent, string root)
    {
        string absolute = Path.GetFullPath(root);
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(Path.GetDirectoryName(absolute), Path.GetFullPath(parent), comparison) ||
            !Guid.TryParseExact(Path.GetFileName(absolute), "N", out _))
            throw new IOException("TCP cleanup path is outside build/tcp-tests: " + absolute);
        DeleteTree(absolute, absolute);
    }

    static void DeleteTree(string root, string directory)
    {
        string absolute = Path.GetFullPath(directory);
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (absolute != root && !absolute.StartsWith(root + Path.DirectorySeparatorChar, comparison))
            throw new IOException("TCP cleanup path is outside its run directory");
        foreach (string path in Directory.EnumerateFileSystemEntries(absolute))
        {
            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.Directory) != 0)
            {
                // 链接本身删除，不进入目标目录。
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    Directory.Delete(path);
                else
                    DeleteTree(root, path);
            }
            else
            {
                if ((attributes & FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
                File.Delete(path);
            }
        }
        Directory.Delete(absolute);
    }
}
