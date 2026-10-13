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
            TestCheck.That(!Directory.Exists(Path.Combine(passed, "group/Data")) &&
                !Directory.Exists(Path.Combine(passed, "group/runtimes")) &&
                !File.Exists(Path.Combine(passed, "group/DfoServer.dll")) &&
                !File.Exists(Path.Combine(passed, "group/DfoServer.exe")) &&
                !File.Exists(Path.Combine(passed, "group/DfoServer.deps.json")), "passed run removes temporary data and program files");
            foreach (string path in new[] { "group/Logs/server.jsonl", "group/cli/001.stdout.txt", "group.result.json", "result.json", "evidence.txt" })
                TestCheck.That(File.ReadAllText(Path.Combine(passed, path)) == "artifact", "passed run retains diagnostic evidence: " + path);
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
                Directory.Delete(root, true);
            }
        }
    }
}
