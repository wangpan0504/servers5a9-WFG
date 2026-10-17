using System.Net;

namespace DfoServer.Network;

internal interface IConnectionProtocol
{
    Task RunAsync(ConnectionContext connection);
}

// 生命周期由 Host 持有；协议只能取消自己的连接。
internal sealed class ConnectionContext(int id, IPAddress peer, Stream stream, CancellationTokenSource lifetime, bool channel = false,
    PacketCapture? capture = null, DfoServer.GameWorld.Channels.ChannelEndpoint? channelEndpoint = null, IPAddress? localAddress = null)
{
    public int Id { get; } = id;
    public IPAddress Peer { get; } = peer;
    public IPAddress? LocalAddress { get; } = localAddress;
    public DfoServer.GameWorld.Channels.ChannelEndpoint? ChannelEndpoint { get; } = channelEndpoint;
    public Stream Stream { get; } = stream;
    public CancellationToken Stop => lifetime.Token;
    internal CancellationTokenSource Lifetime => lifetime;

    public string ServerAddress(ServerOptions options)
    {
        if (options.AdvertisedAddress != "connection")
            return options.AdvertisedAddress;
        // 返回本连接抵达的服务端地址，不是远端玩家IP，也不是通配监听地址。
        if (LocalAddress is null || LocalAddress.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork ||
            LocalAddress.Equals(IPAddress.Any) || LocalAddress.Equals(IPAddress.Broadcast))
            throw new InvalidDataException("连接缺少可返回客户端的本地 IPv4 地址");
        return LocalAddress.ToString();
    }

    public async Task<byte[]> ReadAsync()
    {
        byte[] frame = await GameFrame.ReadAsync(Stream, channel, Stop);
        capture?.Write(Id, channel, false, frame);
        return frame;
    }

    public void CaptureSent(byte[] frame) => capture?.Write(Id, channel, true, frame);

    public void Disconnect()
    {
        try
        {
            lifetime.Cancel();
        }
        catch (ObjectDisposedException)
        { /* 已完成清理的旧连接。 */
        }
    }

    public async Task WriteAsync(byte[] bytes)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Stop);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        await Stream.WriteAsync(bytes, timeout.Token);
        CaptureSent(bytes);
    }
}
