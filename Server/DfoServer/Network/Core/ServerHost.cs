using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using DfoServer.Infrastructure;

namespace DfoServer.Network;

internal sealed class ServerHost(ServerOptions options, IConnectionProtocol channelProtocol, IConnectionProtocol gameProtocol, Func<CancellationToken, Task>? runManagement, Action onStarted, ServerLog log)
{
    readonly ConcurrentDictionary<int, Task> active = new();
    int counter;
    public async Task RunAsync(CancellationToken stop)
    {
        Directory.CreateDirectory(options.LogDirectory);
        using var packetCapture = options.PacketCaptureDirectory is { } directory ? new PacketCapture(directory) : null;
        capture = packetCapture;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stop);
        var channel = new TcpListener(IPAddress.Parse(options.ListenAddress), options.ChannelPort);
        var game = new TcpListener(IPAddress.Parse(options.ListenAddress), options.GamePort);
        Task gm = Task.CompletedTask;
        Task[] listeners = [];
        try
        {
            channel.Start();
            game.Start();
            if (runManagement is not null)
                gm = runManagement(lifetime.Token);
            onStarted();
            log.Flush();
            File.WriteAllText(Path.Combine(options.LogDirectory, "ready.json"), JsonSerializer.Serialize(new { options.ChannelPort, options.GamePort, options.GmPort, Pid = Environment.ProcessId }));
            listeners = [Accept(channel, channelProtocol, true, lifetime.Token), Accept(game, gameProtocol, false, lifetime.Token)];
            // 某个监听失败时取消整个实例，避免 Task.WhenAll 永久等待另一个监听。
            var workers = runManagement is null ? listeners : [.. listeners, gm];
            await await Task.WhenAny(workers);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        finally
        {
            lifetime.Cancel();
            channel.Stop();
            game.Stop();
            try
            {
                await Task.WhenAll([.. listeners, gm]);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
            }
            finally
            {
                // 监听退出后不再新增连接，才能等待完整的连接清理集合。
                await Task.WhenAll(active.Values);
                log.Write("stopped", new { Pid = Environment.ProcessId });
            }
        }
    }

    PacketCapture? capture;

    async Task Accept(TcpListener listener, IConnectionProtocol protocol, bool channel, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            var client = await listener.AcceptTcpClientAsync(stop);
            if (client.Client.RemoteEndPoint is not IPEndPoint peer || active.Count >= 64)
            {
                client.Dispose();
                continue;
            }

            int id = Interlocked.Increment(ref counter);
            Task task = Serve(client, protocol, channel, peer.Address, id, stop);
            active[id] = task;
            _ = task.ContinueWith(_ => active.TryRemove(id, out var ignored), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    async Task Serve(TcpClient client, IConnectionProtocol protocol, bool channel, IPAddress peer, int id, CancellationToken stop)
    {
        using var ownedClient = client;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stop);
        try
        {
            client.NoDelay = true;
            await protocol.RunAsync(new(id, peer, client.GetStream(), lifetime, channel, capture));
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or InvalidDataException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            log.Write("closed", new { Connection = id, Channel = channel, Reason = ex.GetType().Name, ex.Message });
        }
    }
}
