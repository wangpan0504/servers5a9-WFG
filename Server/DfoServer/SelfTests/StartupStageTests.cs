using System.Text.Json;
using DfoServer.Infrastructure;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class StartupStageTests
{
    internal static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "S5A9-startup-stage-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var output = new StringWriter();
            var original = new InvalidDataException("fixture");
            int calls = 0;
            using (var log = new ServerLog(root, output))
            {
                object expected = new();
                That(ReferenceEquals(expected, StartupStage.Run(log, "Test catalog", () => { calls++; return expected; })) && calls == 1,
                    "启动诊断保持加载次数与返回对象，不增加资源遍历");
                try
                {
                    StartupStage.Run<int>(log, "Failed catalog", () => throw original);
                    That(false, "失败装配必须传播原异常");
                }
                catch (InvalidDataException error) { That(ReferenceEquals(error, original), "不替换或吞掉装配失败"); }
            }
            var lines = File.ReadAllLines(Path.Combine(root, "server.jsonl"));
            That(lines.Length == 3, "成功只写开始/完成，失败只保留开始，不伪造完成");
            using var complete = JsonDocument.Parse(lines[1]);
            var detail = complete.RootElement.GetProperty("Detail");
            That(detail.GetProperty("State").GetString() == "complete" && detail.GetProperty("ElapsedMs").GetDouble() >= 0,
                "耗时使用单调时钟且只在加载成功后写完成");
            That(detail.GetProperty("GcPauseMs").GetDouble() >= 0 && detail.GetProperty("Gen0").GetInt32() >= 0 &&
                detail.GetProperty("Metrics").GetProperty("Gen2").GetInt32() >= 0, "GC仅记录本进程累计计数和阶段差值");
            That(output.ToString().Contains("Loading Test catalog...") && output.ToString().Contains("Test catalog: OK (") &&
                !output.ToString().Contains("Metrics") && !output.ToString().Contains("Failed catalog: OK"),
                "复用启动英文纯文本展示，指标不整块输出控制台且失败无OK");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
