using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Management;

namespace DfoServer.Network;

internal sealed class ServerHost(ServerOptions options, IConnectionProtocol channelProtocol, IConnectionProtocol gameProtocol,
    Func<CancellationToken, Task>? runManagement, Func<OnlineCharacterView[]> onlineCharacters, Action onStarted, ServerLog log, GameChannelListeners gameChannels)
{
    readonly ConcurrentDictionary<int, Task> active = new();
    int counter;
    readonly SemaphoreSlim connections = new(64, 64);
    public async Task RunAsync(CancellationToken stop)
    {
        Directory.CreateDirectory(options.LogDirectory);
        using var packetCapture = options.PacketCaptureDirectory is not null || options.PacketHttpPort != 0
            ? new PacketCapture(options.PacketCaptureDirectory, retainRecent: options.PacketHttpPort != 0) : null;
        capture = packetCapture;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stop);
        var channel = new TcpListener(IPAddress.Parse(options.ListenAddress), options.ChannelPort);
        Task gm = Task.CompletedTask;
        Task http = Task.CompletedTask;
        Task[] listeners = [];
        string starting = "Channel listener";
        try
        {
            channel.Start();
            starting = "GM listener";
            if (runManagement is not null)
                gm = runManagement(lifetime.Token);
            if (gm.IsFaulted) await gm;
            starting = "Packet HTTP listener";
            if (options.PacketHttpPort != 0)
                http = PacketHttpServer.Run(options.PacketHttpPort, packetCapture!, options, onlineCharacters, DateTimeOffset.Now, log, lifetime.Token);
            if (http.IsFaulted) await http;
            starting = "Game listeners";
            gameChannels.Start();
            var primary = gameChannels.Bound.Single(row => row.Endpoint.Entry.Identity.Number == GameWorld.Channels.ChannelCatalog.PrimaryChannelNumber);
            log.Write("listening", new { ChannelName = "#ch." + primary.Endpoint.Entry.Identity.Number,
                Channel = channel.LocalEndpoint.ToString(), Game = primary.Listener.LocalEndpoint.ToString(), ChannelEntries = gameChannels.Endpoints,
                AdvertisedGame = $"{options.AdvertisedAddress}:{options.GamePort}",
                Gm = options.GmPort == 0 ? "disabled" : $"127.0.0.1:{options.GmPort}",
                PacketHttp = options.PacketHttpPort == 0 ? "disabled" : $"127.0.0.1:{options.PacketHttpPort}",
                PacketCapture = packetCapture?.IsEnabled == true ? options.PacketCaptureDirectory : "disabled",
                ServiceLog = Path.Combine(options.LogDirectory, "server.jsonl") });
            starting = "Readiness marker";
            onStarted();
            log.Flush();
            File.WriteAllText(Path.Combine(options.LogDirectory, "ready.json"), JsonSerializer.Serialize(new
            {
                options.ChannelPort, options.GamePort, options.GmPort, options.PacketHttpPort, options.AdvertisedAddress, options.ListenAddress,
                options.PvfPath, options.DataDirectory, Pid = Environment.ProcessId, ChannelEntries = gameChannels.Endpoints
            }));
            log.Write("server_ready", new { Pid = Environment.ProcessId });
            starting = "Running listeners";
            listeners = [Accept(channel, channelProtocol, true, lifetime.Token),
                .. gameChannels.Bound.Select(entry => Accept(entry.Listener, gameProtocol, false, lifetime.Token, entry.Endpoint))];
            // 某个监听失败时取消整个实例，避免 Task.WhenAll 永久等待另一个监听。
            Task[] workers = [.. listeners];
            if (runManagement is not null)
                workers = [.. workers, gm];
            if (options.PacketHttpPort != 0)
                workers = [.. workers, http];
            await await Task.WhenAny(workers);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            log.Write("listener_failed", new { Stage = starting, Reason = error.GetType().Name, error.Message });
            throw;
        }
        finally
        {
            lifetime.Cancel();
            channel.Stop();
            gameChannels.Stop();
            try
            {
                await Task.WhenAll([.. listeners, gm, http]);
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

    async Task Accept(TcpListener listener, IConnectionProtocol protocol, bool channel, CancellationToken stop,
        GameWorld.Channels.ChannelEndpoint? endpoint = null)
    {
        while (!stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(stop);
            }
            catch (SocketException error) when (stop.IsCancellationRequested && error.SocketErrorCode == SocketError.OperationAborted)
            {
                // 只把本监听已取消的待接入操作视为正常停止，其他套接字故障继续传播。
                break;
            }
            if (client.Client.RemoteEndPoint is not IPEndPoint peer || !connections.Wait(0))
            {
                client.Dispose();
                continue;
            }

            int id = Interlocked.Increment(ref counter);
            Task task = Serve(client, protocol, channel, peer, id, stop, endpoint);
            active[id] = task;
            _ = task.ContinueWith(_ => active.TryRemove(id, out var ignored), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    async Task Serve(TcpClient client, IConnectionProtocol protocol, bool channel, IPEndPoint peer, int id, CancellationToken stop,
        GameWorld.Channels.ChannelEndpoint? endpoint)
    {
        using var ownedClient = client;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stop);
        try
        {
            capture?.Register(id, channel, peer);
            log.Write("connected", new { Connection = id, Protocol = channel ? "CHANNEL" : "GAME", Peer = client.Client.RemoteEndPoint?.ToString() });
            client.NoDelay = true;
            await protocol.RunAsync(new(id, peer.Address, client.GetStream(), lifetime, channel, capture, endpoint));
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or InvalidDataException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            log.Write("closed", new { Connection = id, Channel = channel, Reason = ex.GetType().Name, ex.Message });
        }
        finally
        {
            capture?.Remove(id);
            log.Write("disconnected", new { Connection = id, Protocol = channel ? "CHANNEL" : "GAME" });
            connections.Release();
        }
    }
}
