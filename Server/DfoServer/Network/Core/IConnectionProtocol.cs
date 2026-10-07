using System.Net;

namespace DfoServer.Network;

internal interface IConnectionProtocol
{
    Task RunAsync(ConnectionContext connection);
}

// 生命周期由 Host 持有；协议只能取消自己的连接。
internal sealed class ConnectionContext(int id, IPAddress peer, Stream stream, CancellationTokenSource lifetime, bool channel = false, PacketCapture? capture = null)
{
    public int Id { get; } = id;
    public IPAddress Peer { get; } = peer;
    public Stream Stream { get; } = stream;
    public CancellationToken Stop => lifetime.Token;
    internal CancellationTokenSource Lifetime => lifetime;

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
