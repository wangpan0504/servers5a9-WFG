namespace DfoServer.Testing;

internal static class TcpTestArtifacts
{
    public static string Parent => Path.GetFullPath(Path.Combine("build", "tcp-tests"));

    // 启动前按保留策略回收旧现场；本轮通过即删除整个轮次目录，失败、取消保留现场供排查。
    public static async Task<string> RunAsync(Func<string, Task> run, bool? keepArtifacts = null)
    {
        string parent = Parent;
        TcpTestRetention.Apply(parent, TcpTestRetention.Policy.FromEnvironment());
        string root = Path.Combine(parent, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Console.WriteLine("C# TCP fixtures: " + root);
        using (TcpTestRetention.MarkRunning(root))
            await run(root);
        TcpTestTimings.Record(parent, root);
        if (keepArtifacts ?? Environment.GetEnvironmentVariable("DFO_KEEP_TCP_ARTIFACTS") == "1")
        {
            Console.WriteLine("TCP artifacts retained: " + root);
            return root;
        }
        try
        {
            TcpTestRetention.DeleteRun(parent, root);
            Console.WriteLine("TCP run passed; artifacts removed: " + root);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 清理错误不能隐藏测试结果；保留剩余文件并明确提示待清理。
            Console.WriteLine("WARN TCP artifact cleanup incomplete: " + exception.Message + "; " + root);
        }
        return root;
    }
}
