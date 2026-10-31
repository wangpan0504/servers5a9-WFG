using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace ServerLauncher;

// 启动器独有的无资源回归；仅使用隔离监听与合成GM，不读写运行中的玩家库。
internal static class GmWebTests
{
    public static Task RunAsync()
    {
        int checks = 0;
        void Check(bool value, string message)
        {
            if (!value) throw new InvalidOperationException("FAIL: " + message);
            checks++;
        }
        void Reject(Action action, string message)
        {
            bool rejected = false;
            try { action(); }
            catch (Exception error) when (error is IOException or InvalidDataException) { rejected = true; }
            Check(rejected, message);
        }
        var old = JsonSerializer.Deserialize<LaunchSettings>("""
            {"ServerIp":"127.0.0.1","ChannelPort":23100,"GamePort":23101,"PvfPath":"Data/Pvf/Script.pvf","DataDirectory":"Data"}
            """)!;
        old.Validate();
        Check(!old.GmEnabled && old.ActiveGmPort == 0 && old.GmPort == 23102 && old.GmWebPort == 23103,
            "旧配置缺少GM字段时保持关闭且恢复默认端口");
        var enabled = old with { GmEnabled = true, GmPort = 24002, GmWebPort = 24003 };
        enabled.Validate();
        Check(JsonSerializer.Deserialize<LaunchSettings>(JsonSerializer.Serialize(enabled)) == enabled,
            "GM开关和自定义端口按旧设置文件格式保存回放");
        foreach (var invalid in new[] { enabled with { GmPort = 0 }, enabled with { GmWebPort = 65536 },
            enabled with { GmPort = old.GamePort }, enabled with { GmWebPort = enabled.GmPort } })
            Reject(invalid.Validate, "启用GM的所有端口须有效且不重复");
        (old with { GmPort = old.GamePort, GmWebPort = old.GamePort }).Validate();
        Check(true, "关闭GM时不增加无关的端口占用限制");

        Check(ServerProcess.GmArguments(enabled).SequenceEqual(["--server-ip", "127.0.0.1", "--channel-port", "23100",
            "--game-port", "23101", "--gm-port", "24002", "--gm-web-port", "24003"]), "启用GM时向服务端传入 --gm-web-port");
        Check(!ServerProcess.GmArguments(old).Contains("--gm-web-port") && ServerProcess.GmArguments(old).Contains("--gm-port"),
            "关闭GM时不传 --gm-web-port");

        Check(!typeof(GmWebTests).Assembly.GetManifestResourceNames().Any(name => name.EndsWith(".html", StringComparison.OrdinalIgnoreCase)), "旧启动器不再内嵌网页");
        Console.WriteLine($"GM launcher checks: {checks} passed.");
        return Task.CompletedTask;
    }
}
