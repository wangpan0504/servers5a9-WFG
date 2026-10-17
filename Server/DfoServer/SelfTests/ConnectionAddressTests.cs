using System.Net;
using DfoServer.Network;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class ConnectionAddressTests
{
    public static void Run()
    {
        int checks = Count;
        using var stop = new CancellationTokenSource();
        var dynamicOptions = new ServerOptions { AdvertisedAddress = "connection", ListenAddress = "0.0.0.0" };
        var peer = IPAddress.Parse("192.0.2.220");
        foreach (string local in new[] { "127.0.0.1", "127.0.0.2", "192.0.2.185", "10.0.0.2" })
        {
            var connection = new ConnectionContext(1, peer, Stream.Null, stop, localAddress: IPAddress.Parse(local));
            That(connection.ServerAddress(dynamicOptions) == local, "连接模式返回实际到达的服务端地址，而非客户端地址 " + local);
            That(connection.ServerAddress(new() { AdvertisedAddress = "203.0.113.10" }) == "203.0.113.10",
                "显式通告地址覆盖本地地址，保留 NAT 或代理部署");
        }
        foreach (var local in new IPAddress?[] { null, IPAddress.Any, IPAddress.Broadcast, IPAddress.IPv6Loopback })
        {
            var connection = new ConnectionContext(2, peer, Stream.Null, stop, localAddress: local);
            Reject<InvalidDataException>(() => connection.ServerAddress(dynamicOptions), "无有效服务端地址时拒绝构造错误端点");
        }
        That(dynamicOptions.AdvertisedAddress == "connection", "单连接解析不改写共享配置或污染下一连接");
        var legacy = new ConnectionContext(3, IPAddress.Loopback, Stream.Null, stop);
        That(legacy.ServerAddress(new()) == "127.0.0.1", "静态模式不要求额外连接元数据，保留原有调用行为");
        Console.WriteLine($"PASS {Count - checks} 连接选址、显式覆盖及无效本地地址断言");
    }
}
