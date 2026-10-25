using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

// ACK LOGIN第3字节安装已确认的频道类型：团本准入与普通频道冒险团标签共用该上下文。
internal static class RaidChannelTypeTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "raid-channel-type"), resources: resources);
        await fixture.StartAsync();
        var endpoints = ChannelTcpTests.Endpoints(fixture);
        byte normal = endpoints.Keys.Min();
        foreach (var (channel, type) in new[] { ((byte)19, (byte)0x17), ((byte)20, (byte)0x20), ((byte)39, (byte)0x2D),
            ((byte)41, (byte)0x36), ((byte)42, (byte)0x32), (normal, (byte)22), ((byte)32, (byte)0), ((byte)43, (byte)0) })
        {
            using var peer = Open(fixture, "chtype" + channel, gamePort: endpoints[channel], channelNumber: channel);
            That(peer.LoginEndpoint.Length > 3 && peer.LoginEndpoint[3] == type,
                $"频道 {channel} ACK LOGIN 第 3 字节 = 0x{type:X2}（实际 0x{(peer.LoginEndpoint.Length > 3 ? peer.LoginEndpoint[3] : -1):X2}）");
        }
    }
}
