using System.Diagnostics;
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
    }
}
