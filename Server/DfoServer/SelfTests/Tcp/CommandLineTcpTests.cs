namespace DfoServer.Testing;

internal static class CommandLineTcpTests
{
    public static async Task RunAsync(ServerOptions options, string root)
    {
        CommandLineTests.Run();
        // 此组不依赖 PVF；错误参数与帮助不能因资源不存在而尝试加载归档。
        var noResources = options with { PvfPath = Path.Combine(root, "missing.pvf") };
        await using var fixture = new TcpTestFixture(noResources, Path.Combine(root, "commands"));
        string[][] invalid =
        [
            ["--unknown"], ["--selftest", "extra"], ["--selftest-core", "extra"], ["--selftest-tcp", "extra"],
            ["--inspect-dungeon", "-1"], ["--prepare-jianying", "missing-audit"], ["--import-characters"],
            ["--selftest-tcp", "booster,booster"], ["--selftest-tcp", "booster", "town"]
        ];
        foreach (var args in invalid)
            await fixture.CliAsync(null, 2, args);
        string help = await fixture.CliAsync(null, "--gm", "help");
        // 检查命令内容，避免将 Windows 控制台代码页差异当作命令分派失败。
        TestCheck.That(help.Contains("quest-clear", StringComparison.Ordinal) && help.Contains("account", StringComparison.Ordinal),
            "GM help remains available without starting a server");
        TestCheck.That(!Directory.Exists(fixture.Options.DataDirectory), "invalid commands and help create no player database or instance lock");
        TestCheck.That(!File.Exists(Path.Combine(fixture.Options.LogDirectory, "ready.json")), "invalid commands never publish readiness");
        string directHelp = await fixture.CliAsync(null, "--help");
        TestCheck.That(directHelp.Contains("--server-ip", StringComparison.Ordinal) && directHelp.Contains("Data/Pvf/Script.pvf", StringComparison.Ordinal), "direct CLI help works without JSON or resources");
        foreach (string[] args in new string[][] { ["--unknown"], ["--server-ip"], ["--game-port", "0"], ["--server-ip", "::1"], ["--client-ip", "bad"], ["--pvf", "file.pvf"], ["--data-dir", "Data"], ["--log-dir", "Logs"], ["--duplicate-login", "Reject"], ["server.json"], ["--selftest", "extra"] })
            await fixture.CliAsync(null, 2, args);
        await fixture.CliAsync(null, "--gm", "help");
        TestCheck.That(!Directory.Exists(fixture.Options.DataDirectory), "direct CLI help and invalid options create no player database");

        string core = await fixture.CliAsync(null, "--selftest-core");
        TestCheck.That(core.Contains("resource-free repository boundary assertions", StringComparison.Ordinal), "基础自测在缺少 PVF 的进程中完整执行");
        TestCheck.That(!Directory.Exists(fixture.Options.DataDirectory) && !Directory.Exists(fixture.Options.LogDirectory),
            "基础自测不创建运行 Data、Logs 或监听就绪文件");

        string text = new string('x', 128 * 1024) + "日志尾部";
        string output = Path.Combine(fixture.Root, "output-copy.txt");
        using (var reader = new StringReader(text))
            await TcpTestFixture.CopyOutputAsync(reader, output);
        TestCheck.That(File.ReadAllText(output) == text, "server output above the CLI reply limit is fully streamed to disk");

        await using var failed = new TcpTestFixture(noResources, Path.Combine(root, "startup-failure"));
        bool rejected = false;
        try
        {
            await failed.StartAsync();
        }
        catch (InvalidOperationException error) when (error.Message.StartsWith("Fixture exited before ready:", StringComparison.Ordinal))
        {
            rejected = true;
        }
        // StartAsync 失败时已回收进程和输出任务，后续 Dispose 不得再次抛异常掩盖原始错误。
        await failed.StopAsync();
        TestCheck.That(rejected && new FileInfo(Path.Combine(failed.Options.LogDirectory, "stderr.txt")).Length > 0,
            "startup failure preserves diagnostics and completes cleanup");
        Console.WriteLine("PASS C# command entry process checks");
    }
}
