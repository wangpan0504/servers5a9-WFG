using System.Diagnostics;

namespace DfoServer.Testing;

internal static class TcpTestArtifactsTests
{
    public static async Task RunAsync()
    {
        var roots = new List<string>();
        Task Prepare(string root)
        {
            roots.Add(root);
            foreach (string directory in new[] { "group/Data/Pvf", "group/runtimes/native", "group/Logs", "group/cli" })
                Directory.CreateDirectory(Path.Combine(root, directory));
            foreach (string path in new[] { "group/Data/game.db", "group/Data/Pvf/Script.pvf", "group/runtimes/native/runtime.dll", "group/DfoServer.dll", "group/DfoServer.exe", "group/DfoServer.pdb", "group/DfoServer.deps.json", "group/DfoServer.runtimeconfig.json", "group/Logs/server.jsonl", "group/cli/001.stdout.txt", "group.result.json", "result.json", "evidence.txt" })
                File.WriteAllText(Path.Combine(root, path), "artifact");
            return Task.CompletedTask;
        }

        try
        {
            var failure = new InvalidOperationException("fixture failed");
            try
            {
                await TcpTestArtifacts.RunAsync(async root => { await Prepare(root); throw failure; }, false);
                throw new InvalidOperationException("expected fixture failure");
            }
            catch (InvalidOperationException exception) when (ReferenceEquals(exception, failure))
            {
                TestCheck.That(File.Exists(Path.Combine(roots[0], "group/Data/game.db")), "failed run preserves its database");
                TestCheck.That(File.Exists(Path.Combine(roots[0], "group/DfoServer.dll")), "failed run preserves runtime files");
            }
            using var cancel = new CancellationTokenSource();
            cancel.Cancel();
            try
            {
                await TcpTestArtifacts.RunAsync(async root => { await Prepare(root); cancel.Token.ThrowIfCancellationRequested(); }, false);
                throw new InvalidOperationException("expected fixture cancellation");
            }
            catch (OperationCanceledException exception) when (exception.CancellationToken == cancel.Token)
            {
                TestCheck.That(File.Exists(Path.Combine(roots[1], "group/Data/Pvf/Script.pvf")), "cancelled run preserves its resources");
            }
            bool linked = false;
            string passed = await TcpTestArtifacts.RunAsync(async root =>
            {
                await Prepare(root);
                try
                {
                    string link = Path.Combine(root, "group/Data/previous");
                    string target = Path.Combine(roots[0], "group/Data");
                    if (OperatingSystem.IsWindows())
                    {
                        // junction同样是目录重解析点，保留越界保护断言且不依赖管理员/开发者模式。
                        using var process = Process.Start(new ProcessStartInfo("cmd.exe")
                        {
                            Arguments = $"/d /c mklink /J \"{link}\" \"{target}\"",
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true
                        }) ?? throw new IOException("Cannot create synthetic directory junction");
                        if (!process.WaitForExit(5000))
                        {
                            process.Kill();
                            process.WaitForExit();
                            throw new TimeoutException("Synthetic junction command timed out");
                        }
                        if (process.ExitCode != 0)
                            throw new IOException("Synthetic junction failed: " + process.StandardError.ReadToEnd());
                    }
                    else
                        Directory.CreateSymbolicLink(link, target);
                    TestCheck.That((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0,
                        "link boundary must exercise a real directory reparse point");
                    linked = true;
                }
                catch (Exception exception) when (exception is UnauthorizedAccessException or PlatformNotSupportedException)
                {
                    Console.WriteLine("SKIP TCP artifact link boundary: " + exception.GetType().Name);
                }
            }, false);
            TestCheck.That(!Directory.Exists(passed), "passed run is removed entirely after its timings are recorded");
            TestCheck.That(File.Exists(Path.Combine(roots[0], "group/Data/game.db")) &&
                File.Exists(Path.Combine(roots[1], "group/Data/Pvf/Script.pvf")), "successful rerun does not erase previous failed or cancelled scenes");
            if (linked)
                TestCheck.That(File.Exists(Path.Combine(roots[0], "group/Data/Pvf/Script.pvf")), "cleanup removes the directory link without following it into a previous run");
            string retained = await TcpTestArtifacts.RunAsync(Prepare, true);
            TestCheck.That(File.Exists(Path.Combine(retained, "group/Data/game.db")) &&
                File.Exists(Path.Combine(retained, "group/DfoServer.dll")), "explicit retention preserves successful runtime artifacts");
        }
        finally
        {
            // 只回收本用例新建的合成现场，不扫描已有轮次。
            string parent = Path.GetFullPath(Path.Combine("build", "tcp-tests"));
            foreach (string root in roots)
            {
                if (Path.GetDirectoryName(root) != parent || !Guid.TryParseExact(Path.GetFileName(root), "N", out _))
                    throw new IOException("Unexpected synthetic TCP artifact path");
                if (Directory.Exists(root))
                    Directory.Delete(root, true);
            }
        }
        RunRetention();
        RunPvfBudget();
    }

    static void RunPvfBudget()
    {
        string parent = Path.Combine(Path.GetTempPath(), "dfo-tcp-pvf-budget-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(parent);
        try
        {
            DateTime now = DateTime.UtcNow;
            string CreateRun(int hours)
            {
                string root = Path.Combine(parent, Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path.Combine(root, "Data", "Pvf"));
                File.WriteAllBytes(Path.Combine(root, "Data", "Pvf", "Script.pvf"), new byte[4096]);
                Directory.SetLastWriteTimeUtc(root, now.AddHours(-hours));
                return root;
            }
            string latest = CreateRun(1), older = CreateRun(2);
            TcpTestRetention.Apply(parent, new TcpTestRetention.Policy(5, TimeSpan.FromDays(3), 4096), now);
            TestCheck.That(Directory.Exists(latest) && !Directory.Exists(older), "PVF完整副本计入保留预算，最新失败现场仍保留");
        }
        finally { Directory.Delete(parent, true); }
    }

    // 保留策略：通过的旧轮次删除；失败轮次按数量、天数、体积裁剪且最新一个始终保留；运行中轮次不动。
    static void RunRetention()
    {
        string parent = Path.Combine(Path.GetTempPath(), "dfo-tcp-retention-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(parent);
        try
        {
            DateTime now = DateTime.UtcNow;
            string Run(int hoursAgo, bool passed, int bytes = 1)
            {
                string root = Path.Combine(parent, Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path.Combine(root, "group", "Data", "Pvf"));
                File.WriteAllBytes(Path.Combine(root, "group", "Data", "game.db"), new byte[bytes]);
                File.WriteAllBytes(Path.Combine(root, "group", "Data", "Pvf", "Script.pvf"), new byte[4096]);
                if (passed)
                    File.WriteAllText(Path.Combine(root, "result.json"), "{\"Passed\":true}");
                Directory.SetLastWriteTimeUtc(root, now.AddHours(-hoursAgo));
                return root;
            }
            string legacyPassed = Run(1, true);
            string[] failed = Enumerable.Range(0, 7).Select(i => Run(2 + i, false)).ToArray();
            string stale = Run(24 * 10, false);
            string running = Run(24 * 20, false);
            string other = Path.Combine(parent, "not-a-run");
            Directory.CreateDirectory(other);
            using (TcpTestRetention.MarkRunning(running))
                TcpTestRetention.Apply(parent, new TcpTestRetention.Policy(3, TimeSpan.FromDays(3), long.MaxValue), now);
            // 删除标记会刷新目录时间；还原为原轮次时间，模拟中断后遗留的旧现场。
            Directory.SetLastWriteTimeUtc(running, now.AddHours(-24 * 20));
            TestCheck.That(!Directory.Exists(legacyPassed), "retention removes leftover passed runs");
            TestCheck.That(failed.Take(3).All(Directory.Exists) && !failed.Skip(3).Any(Directory.Exists), "retention keeps only the newest N failed runs");
            TestCheck.That(!Directory.Exists(stale), "retention removes failed runs older than the age limit");
            TestCheck.That(Directory.Exists(running) && Directory.Exists(other), "retention skips running runs and non-run directories");
            TcpTestRetention.Apply(parent, new TcpTestRetention.Policy(5, TimeSpan.FromDays(3), 1), now);
            TestCheck.That(Directory.Exists(failed[0]) && !failed.Skip(1).Any(Directory.Exists), "size cap prunes older failures but always keeps the newest failed run");
            TestCheck.That(!Directory.Exists(running), "a released running marker makes an expired run eligible");
        }
        finally
        {
            Directory.Delete(parent, true);
        }
    }
}
