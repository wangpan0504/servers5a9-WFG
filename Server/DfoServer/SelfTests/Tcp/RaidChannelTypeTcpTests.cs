using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

// ACK LOGIN 第 3 字节（频道类型 → 客户端 +0x1010）：团队频道发真实类型，普通频道发 0。
internal static class RaidChannelTypeTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "raid-channel-type"), resources: resources);
        await fixture.StartAsync();
        var endpoints = ChannelTcpTests.Endpoints(fixture);
        byte normal = endpoints.Keys.Min();
        foreach (var (channel, type) in new[] { ((byte)19, (byte)0x17), ((byte)20, (byte)0x20), ((byte)39, (byte)0x2D),
            ((byte)41, (byte)0x36), ((byte)42, (byte)0x32), (normal, (byte)0) })
        {
            using var peer = Open(fixture, "chtype" + channel, gamePort: endpoints[channel], channelNumber: channel);
            That(peer.LoginEndpoint.Length > 3 && peer.LoginEndpoint[3] == type,
                $"频道 {channel} ACK LOGIN 第 3 字节 = 0x{type:X2}（实际 0x{(peer.LoginEndpoint.Length > 3 ? peer.LoginEndpoint[3] : -1):X2}）");
        }
    }
}
