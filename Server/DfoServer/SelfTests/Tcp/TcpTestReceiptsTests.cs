using System.Text.Json;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class TcpTestReceiptsTests
{
    public static void Run()
    {
        int before = Count;
        var identity = new TcpTestReceipts.Identity(new('a', 64), new('b', 64), new('c', 64));
        string id = Guid.NewGuid().ToString("N");
        TcpTestReceipts.Receipt passed;
        using (var group = new TcpTestReceipts.Group("fixture", id, identity, coverageAudited: true))
        {
            That(!group.Snapshot("running").CacheEligible && group.Snapshot("running").CompletedUtc is null,
                "开始记录不能当作终态成功，kill后遗留running不可复用");
            passed = group.Snapshot("passed");
            That(passed.Passed && passed.CacheEligible && passed.CompletedUtc >= passed.StartedUtc, "完整执行且身份齐备才可候选复用");
            That(!group.Snapshot("failed").CacheEligible && !group.Snapshot("interrupted").CacheEligible,
                "失败或取消不能复用");
            using (var child = new TcpTestReceipts.Group("child", id, identity, coverageAudited: true))
            {
                // 不输出伪造的外层 SKIP：只测试结构化记录本身。
                child.Add(new("fixture.input-missing", "合成输入缺失"));
                var partial = child.Snapshot("passed");
                That(partial.Passed && partial.Coverage == "partial" && !partial.CacheEligible && partial.Skips.Length == 1,
                    "旧退出成功语义保留，但部分覆盖不可冒充full");
                That(!Reusable(partial), "序列化partial不能成为缓存命中");
            }
            That(group.Snapshot("passed").Skips.Length == 0, "嵌套收据不把子项skip污染别的测试组");
        }
        using (var profile = new TcpTestReceipts.Group("chat-rate-rules", id, identity))
        {
            int count = Count;
            DfoServer.ChatServiceTests.RunRateRules();
            var result = profile.Snapshot("passed");
            That(Count - count == 39 && result.Profile == "chat-rate-rules-v1" && result.CacheEligible,
                "确定性profile执行原39条短长滑窗断言，不把其他聊天网络覆盖冒充完整");
        }
        using (var unknown = new TcpTestReceipts.Group("not-audited", id, identity))
            That(unknown.Snapshot("passed").Coverage == "unknown" && !unknown.Snapshot("passed").CacheEligible,
                "未迁移覆盖合同的组即使成功也保持unknown");
        using (var missing = new TcpTestReceipts.Group("fixture", id, identity with { Input = null }, true))
            That(!missing.Snapshot("passed").CacheEligible, "缺输入身份不允许复用");
        using (var old = JsonDocument.Parse("{\"Passed\":true}"))
            That(!TcpTestReceipts.IsReusable(old.RootElement), "旧schema不能悄然升级");
        That(Reusable(passed), "完整新schema保留资格");
        That(!Reusable(passed with { SchemaVersion = 99 }) && !Reusable(passed with { Coverage = "unknown" }) &&
            !Reusable(passed with { Identity = identity with { Runner = "not-a-digest" } }), "未知schema/覆盖/身份均fail closed");
        string root = Path.Combine(Path.GetTempPath(), "S5A9-test-receipts-" + id);
        try
        {
            Directory.CreateDirectory(root);
            string module = Path.Combine(AppContext.BaseDirectory, typeof(TcpTestReceipts).Assembly.GetName().Name + ".dll");
            That(TcpTestReceipts.IsDirectDllInvocation("dotnet", module, module) &&
                !TcpTestReceipts.IsDirectDllInvocation("DfoServer", module, module) &&
                !TcpTestReceipts.IsDirectDllInvocation("dotnet", Path.Combine(root, "other.dll"), module) &&
                !TcpTestReceipts.IsDirectDllInvocation(null, null, module),
                "仅直接dotnet指定DLL入口允许核身份；apphost、其它入口和未知宿主拒绝");
            if (File.Exists(module))
            {
                Guid mvid = typeof(TcpTestReceipts).Assembly.ManifestModule.ModuleVersionId;
                string? actual = TcpTestReceipts.ReadRunnerDigest(module, mvid);
                That(TcpTestReceipts.IsDigest(actual), "实际加载模块MVID与普通DLL一致才能计算身份");
                That(TcpTestReceipts.ReadRunnerDigest(module, Guid.Empty) is null &&
                    TcpTestReceipts.ReadRunnerDigest(module, Guid.NewGuid()) is null,
                    "未知或错误模块MVID不能借旁置DLL冒充已加载身份");
            }
            string invalid = Path.Combine(root, "wrong-side.dll");
            File.WriteAllText(invalid, "not a PE module");
            That(TcpTestReceipts.ReadRunnerDigest(invalid, Guid.NewGuid()) is null &&
                TcpTestReceipts.ReadRunnerDigest(Path.Combine(root, "absent.dll"), Guid.NewGuid()) is null,
                "坏PE或缺失DLL只得到未知身份，不产生假hash");
            string path = Path.Combine(root, "group.json");
            TcpTestReceipts.WriteAtomic(path, passed with { Status = "running", CompletedUtc = null });
            TcpTestReceipts.WriteAtomic(path, passed);
            using (var saved = JsonDocument.Parse(File.ReadAllText(path)))
                That(TcpTestReceipts.IsReusable(saved.RootElement), "原子替换后的完整JSON可读");
            string blocked = Path.Combine(root, "directory.json");
            Directory.CreateDirectory(blocked);
            try { TcpTestReceipts.WriteAtomic(blocked, passed); throw new InvalidOperationException("expected receipt write failure"); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                That(Directory.Exists(blocked) && !Directory.EnumerateFiles(root, "*.tmp").Any(),
                    "失败写入不产生成功目标且清除临时文件");
            }
            using var unchanged = JsonDocument.Parse(File.ReadAllText(path));
            That(TcpTestReceipts.IsReusable(unchanged.RootElement), "其他收据写失败不改已完成记录");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        Console.WriteLine($"PASS {Count - before} structured TCP receipt assertions");
    }

    static bool Reusable(TcpTestReceipts.Receipt value)
    {
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(value));
        return TcpTestReceipts.IsReusable(json.RootElement);
    }
}
