using System.Threading.Channels;

namespace DfoServer.Network;

// 独立Native连接的唯一写入者；不占用队伍/账号门禁等待慢接收者，正文不进入普通抓包。
internal sealed class NativeConnectionSender : IAsyncDisposable
{
    sealed record Message(byte[] Frame, Func<bool> Current);
    readonly Channel<Message> messages = Channel.CreateBounded<Message>(new BoundedChannelOptions(32)
        { SingleReader = true, FullMode = BoundedChannelFullMode.Wait, AllowSynchronousContinuations = false });
    readonly ConnectionContext connection;
    readonly Action disconnect;
    readonly CancellationTokenSource stop;
    readonly Task pending;

    public NativeConnectionSender(ConnectionContext connection, CancellationToken mainStop, Action disconnect)
    {
        this.connection = connection;
        this.disconnect = disconnect;
        stop = CancellationTokenSource.CreateLinkedTokenSource(connection.Stop, mainStop);
        pending = RunAsync();
    }

    public bool Post(byte[] frame, Func<bool> current)
    {
        if (frame.Length > Parsers.NativeGameplayRecords.MaximumLength + 13 || stop.IsCancellationRequested) return false;
        if (messages.Writer.TryWrite(new(frame, current))) return true;
        disconnect();
        return false;
    }

    async Task RunAsync()
    {
        try
        {
            await foreach (var message in messages.Reader.ReadAllAsync(stop.Token))
            {
                if (!message.Current()) continue;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                await connection.Stream.WriteAsync(message.Frame, timeout.Token);
                connection.CaptureNative(message.Frame.Length, message.Frame.Length - 13, true);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        {
            disconnect();
        }
        finally { messages.Writer.TryComplete(); }
    }

    public async ValueTask DisposeAsync()
    {
        messages.Writer.TryComplete();
        await stop.CancelAsync();
        await pending;
        stop.Dispose();
    }
}
