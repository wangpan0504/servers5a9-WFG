namespace DfoServer.Testing;

internal static class TcpTestArtifacts
{
    // 只清理本次新建的轮次；失败、取消和历史现场不在自动清理范围。
    public static async Task<string> RunAsync(Func<string, Task> run, bool? keepArtifacts = null)
    {
        string root = Path.GetFullPath(Path.Combine("build", "tcp-tests", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        Console.WriteLine("C# TCP fixtures: " + root);
        await run(root);
        if (keepArtifacts ?? Environment.GetEnvironmentVariable("DFO_KEEP_TCP_ARTIFACTS") == "1")
        {
            Console.WriteLine("TCP artifacts retained: " + root);
            return root;
        }
        try
        {
            Prune(root, root);
            Console.WriteLine("TCP runtime artifacts removed; results and logs retained: " + root);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 清理错误不能隐藏测试结果；保留剩余文件并明确提示待清理。
            Console.WriteLine("WARN TCP artifact cleanup incomplete: " + exception.Message + "; " + root);
        }
        return root;
    }

    static void Prune(string root, string directory)
    {
        foreach (string path in Directory.EnumerateFileSystemEntries(directory))
        {
            FileAttributes attributes = File.GetAttributes(path);
            // 不沿符号链接或 junction 访问轮次以外的目录。
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                continue;
            string name = Path.GetFileName(path);
            if ((attributes & FileAttributes.Directory) != 0)
            {
                if (name is "Data" or "Resources" or "runtimes" or "Sqlite")
                    DeleteTree(root, path);
                else if (name is not ("Logs" or "cli"))
                    Prune(root, path);
            }
            else if (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(path);
            }
        }
    }

    static void DeleteTree(string root, string directory)
    {
        string absolute = Path.GetFullPath(directory);
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!absolute.StartsWith(root + Path.DirectorySeparatorChar, comparison))
            throw new IOException("TCP cleanup path is outside its run directory");
        foreach (string path in Directory.EnumerateFileSystemEntries(absolute))
        {
            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.Directory) != 0)
            {
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    Directory.Delete(path);
                else
                    DeleteTree(root, path);
            }
            else
                File.Delete(path);
        }
        Directory.Delete(absolute);
    }
}
