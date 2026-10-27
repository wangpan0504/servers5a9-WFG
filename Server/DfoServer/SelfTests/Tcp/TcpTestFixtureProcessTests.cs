using System.Diagnostics;
using System.Text.Json;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class TcpTestFixtureProcessTests
{
    public static void Run()
    {
        var start = new ProcessStartInfo();
        string? parent = Environment.GetEnvironmentVariable("DOTNET_GCHeapHardLimit");
        start.Environment["DOTNET_GCHeapHardLimit"] = "80000000";
        start.Environment["DOTNET_GCConserveMemory"] = "5";
        TcpTestFixture.ConfigureChildHeapLimit(start, null);
        TcpTestFixture.ConfigureChildHeapLimit(start, "");
        That(start.Environment["DOTNET_GCHeapHardLimit"] == "80000000", "未配置子进程预算时保留继承的驱动堆限额");
        foreach (string value in new[] { "0x30000000", "30000000" })
        {
            TcpTestFixture.ConfigureChildHeapLimit(start, value);
            That(start.Environment["DOTNET_GCHeapHardLimit"] == value, "子进程独立堆限额保留明确的十六进制配置");
        }
        foreach (string invalid in new[] { "0", "0x0", "0x", "xyz", "10000000000000000", " " })
            Reject<InvalidDataException>(() => TcpTestFixture.ConfigureChildHeapLimit(start, invalid), "非法预算在启动子进程前拒绝");
        That(start.Environment["DOTNET_GCHeapHardLimit"] == "30000000" && start.Environment["DOTNET_GCConserveMemory"] == "5",
            "预算拒绝保留原有子进程配置及其它环境设置");
        That(Environment.GetEnvironmentVariable("DOTNET_GCHeapHardLimit") == parent, "子进程预算不能修改驱动进程环境");
        ReadinessDiagnostics();
    }
    static void ReadinessDiagnostics()
    {
        string root = Path.Combine(Path.GetTempPath(), "S5A9-ready-diagnostic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string log = Path.Combine(root, "server.jsonl"), ready = Path.Combine(root, "ready.json");
        try
        {
            var missing = TcpTestFixture.ReadStartupDiagnostic(log);
            That(missing.Error == nameof(FileNotFoundException) && missing.ReadBytes == 0, "缺日志只提供诊断缺失，不抛新异常");
            string first = JsonSerializer.Serialize(new { Time = "2026-10-27T07:03:14Z", Kind = "startup",
                Detail = new { Stage = "Runtime assembly", State = "starting" } });
            string last = JsonSerializer.Serialize(new { Time = "2026-10-27T07:03:15Z", Kind = "startup",
                Detail = new { Stage = "Raid start settings", State = "complete", ElapsedMs = 12.5 } });
            File.WriteAllText(log, first + "\n" + last + "\n{\"Time\":");
            var partial = TcpTestFixture.ReadStartupDiagnostic(log);
            That(partial is { Error: null, TailOnly: false, Time: "2026-10-27T07:03:15Z", Stage: "Raid start settings",
                State: "complete", ElapsedMs: 12.5 }, "完整最后启动事件保留，正在写入的半行不覆盖它");
            File.WriteAllText(log, new string('x', 128 * 1024) + "\n" + last + "\n" +
                JsonSerializer.Serialize(new { Kind = "packet", Detail = new { Account = "must-not-leak" } }) + "\n");
            var tail = TcpTestFixture.ReadStartupDiagnostic(log);
            That(tail is { TailOnly: true, ReadBytes: 65536, Stage: "Raid start settings", Error: null } && tail.FileBytes > tail.ReadBytes,
                "超长日志只读64KiB尾部，跳过边界半行及非启动事件");
            using (var current = Process.GetCurrentProcess())
            using (var parsed = JsonDocument.Parse(TcpTestFixture.ReadinessDiagnostic("role-restart", TimeSpan.FromSeconds(45), ready, log, current, Task.CompletedTask)))
            {
                var value = parsed.RootElement;
                That(value.GetProperty("Phase").GetString() == "role-restart" && value.GetProperty("ElapsedMs").GetDouble() == 45000 &&
                    !value.GetProperty("ReadyExists").GetBoolean() && value.GetProperty("Hosted").GetString() == "RanToCompletion" &&
                    value.GetProperty("Process").GetString()!.Contains($"Pid={current.Id};HasExited=False;ExitCode=not-exited"),
                    "仅观察原持有句柄、原阶段及elapsed，不启动、终止或重试进程");
                That(!parsed.RootElement.GetRawText().Contains("must-not-leak"), "不把其它日志或完整正文附加到超时异常");
            }
            File.WriteAllText(log, first + "\n" + new string('x', 128 * 1024));
            That(TcpTestFixture.ReadStartupDiagnostic(log) is { TailOnly: true, Stage: null, Error: "no-complete-startup-in-tail" },
                "尾部没有完整启动事件时明确未知，不回扫整日志");
            File.WriteAllText(log, JsonSerializer.Serialize(new { Kind = "startup", Time = "now",
                Detail = new { Stage = new string('s', 1000), State = "complete" } }));
            That(TcpTestFixture.ReadStartupDiagnostic(log).Stage?.Length == 160, "诊断字段长度有界");
            var failed = TcpTestFixture.ReadStartupDiagnostic(root);
            That(failed.Error is not null && failed.ReadBytes == 0, "目录不能当日志读取，错误只进入诊断字段");
            File.WriteAllText(ready, "{}");
            using var unstarted = new Process();
            using var unavailable = JsonDocument.Parse(TcpTestFixture.ReadinessDiagnostic("initial", TimeSpan.FromSeconds(45), ready, root, unstarted));
            That(unavailable.RootElement.GetProperty("ReadyExists").GetBoolean() &&
                unavailable.RootElement.GetProperty("Process").GetString()!.StartsWith("unavailable:") &&
                unavailable.RootElement.GetProperty("Startup").GetProperty("Error").ValueKind == JsonValueKind.String,
                "进程/日志诊断自身失败不会覆盖原始ready超时");
        }
        finally { Directory.Delete(root, true); }
    }

}
