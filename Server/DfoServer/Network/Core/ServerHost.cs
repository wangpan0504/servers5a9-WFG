using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Management;

namespace DfoServer.Network;

// saveVersion 由装配入口传入本次程序使用的存档 schema 版本（0 表示未装配，例如自测宿主）；
// 就绪文件与启动日志靠它标出「哪份程序配哪一版存档」，不在这里反向依赖 Persistence。
internal sealed class ServerHost(ServerOptions options, IConnectionProtocol channelProtocol, IConnectionProtocol gameProtocol,
    Func<PacketCapture?, CancellationToken, Task>? runManagement, Func<OnlineCharacterView[]> onlineCharacters, Action onStarted, ServerLog log, GameChannelListeners gameChannels,
    Protocol.NativeChannel? native = null, IDisposable? runtimeMemory = null, IDisposable? guildInvitations = null,
    IDisposable? guildEconomy = null, IDisposable? guildTeleport = null, IDisposable? guildNpcs = null,
    Func<CancellationToken, Task>? runGuildHallWait = null, Func<CancellationToken, Task>? runWorld = null,
    IAsyncDisposable? gameplayTimers = null, int saveVersion = 0)
{
    readonly ConcurrentDictionary<int, Task> active = new();
    int counter;
    readonly SemaphoreSlim connections = new(64, 64);
    public async Task RunAsync(CancellationToken stop)
    {
        using var ownedRuntimeMemory = runtimeMemory;
        await using var ownedGameplayTimers = gameplayTimers;
        using var ownedGuildInvitations = guildInvitations;
        using var ownedGuildEconomy = guildEconomy;
        using var ownedGuildTeleport = guildTeleport;
        using var ownedGuildNpcs = guildNpcs;
        Directory.CreateDirectory(options.LogDirectory);
        bool retainPackets = options.PacketHttpPort != 0 || options.GmWebPort != 0;
        using var packetCapture = options.PacketCaptureDirectory is not null || retainPackets
            ? new PacketCapture(options.PacketCaptureDirectory, retainRecent: retainPackets) : null;
        capture = packetCapture;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stop);
        var channel = new TcpListener(IPAddress.Parse(options.ListenAddress), options.ChannelPort);
        Task gm = Task.CompletedTask;
        Task http = Task.CompletedTask;
        Task hallWait = Task.CompletedTask;
        Task world = Task.CompletedTask;
        Task[] listeners = [];
        string starting = "Channel listener";
        try
        {
            channel.Start();
            starting = "GM listener";
            if (runManagement is not null)
                gm = runManagement(packetCapture, lifetime.Token);
            if (gm.IsFaulted) await gm;
            starting = "Packet HTTP listener";
            if (options.PacketHttpPort != 0)
                http = PacketHttpServer.Run(options.PacketHttpPort, packetCapture!, options, onlineCharacters, DateTimeOffset.Now, log, lifetime.Token);
            if (http.IsFaulted) await http;
            starting = "Game listeners";
            gameChannels.Start();
            starting = "Native listener";
            native?.Start();
            starting = "Guild hall contribution worker";
            if (runGuildHallWait is not null)
                hallWait = runGuildHallWait(lifetime.Token);
            if (hallWait.IsFaulted) await hallWait;
            var primary = gameChannels.Bound.Single(row => row.Endpoint.Entry.Identity.Number == GameWorld.Channels.ChannelCatalog.PrimaryChannelNumber);
            log.Write("listening", new { ChannelName = "#ch." + primary.Endpoint.Entry.Identity.Number,
                Channel = channel.LocalEndpoint.ToString(), Game = primary.Listener.LocalEndpoint.ToString(), ChannelEntries = gameChannels.Endpoints,
                AdvertisedGame = $"{options.AdvertisedAddress}:{options.GamePort}",
                Native = native is null ? "disabled" : native.Listener.LocalEndpoint.ToString(),
                Gm = options.GmPort == 0 ? "disabled" : $"127.0.0.1:{options.GmPort}",
                GmWeb = options.GmWebPort == 0 ? "disabled" : $"127.0.0.1:{options.GmWebPort}",
                GmPacketQueries = options.GmWebPort == 0 ? "disabled" : $"http://127.0.0.1:{options.GmWebPort}/api/packets",
                PacketHttp = options.PacketHttpPort == 0 ? "disabled" : $"127.0.0.1:{options.PacketHttpPort}",
                PacketCapture = packetCapture?.IsEnabled == true ? options.PacketCaptureDirectory : "disabled",
                ServiceLog = Path.Combine(options.LogDirectory, "server.jsonl") });
            starting = "Readiness marker";
            onStarted();
            log.Flush();
            File.WriteAllText(Path.Combine(options.LogDirectory, "ready.json"), JsonSerializer.Serialize(new
            {
                options.ChannelPort, options.GamePort, options.GmPort, options.GmWebPort, options.PacketHttpPort, options.AdvertisedAddress, options.ListenAddress,
                options.PvfPath, options.DataDirectory, Pid = Environment.ProcessId, ChannelEntries = gameChannels.Endpoints,
                NativePort = native?.Port ?? 0,
                // 构建标识：程序集版本、构建提交号（源码 zip 构建为空）与存档 schema 版本。
                ServerVersion = ServerBuildInfo.Version, Commit = ServerBuildInfo.Commit, SaveVersion = saveVersion
            }));
            log.Write("server_ready", new { Pid = Environment.ProcessId, ServerVersion = ServerBuildInfo.Version,
                Commit = ServerBuildInfo.Commit, SaveVersion = saveVersion });
            starting = "Running listeners";
            if (runWorld is not null) world = runWorld(lifetime.Token);
            listeners = [Accept(channel, channelProtocol, true, lifetime.Token),
                .. gameChannels.Bound.Select(entry => Accept(entry.Listener, gameProtocol, false, lifetime.Token, entry.Endpoint)),
                .. (native is null ? Array.Empty<Task>() : [Accept(native.Listener, native, false, lifetime.Token)])];
            // 某个监听失败时取消整个实例，避免 Task.WhenAll 永久等待另一个监听。
            Task[] workers = [.. listeners];
            if (runManagement is not null)
                workers = [.. workers, gm];
            if (options.PacketHttpPort != 0)
                workers = [.. workers, http];
            if (runGuildHallWait is not null)
                workers = [.. workers, hallWait];
            if (runWorld is not null)
                workers = [.. workers, world];
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
            // 先停止玩法时钟并等待已开始的有界操作；不让旧回调跨越Runtime缓存释放。
            if (gameplayTimers is not null) await gameplayTimers.DisposeAsync();
            channel.Stop();
            gameChannels.Stop();
            native?.Stop();
            try
            {
                await Task.WhenAll([.. listeners, gm, http, hallWait, world]);
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
            bool isNative = protocol is Protocol.NativeChannel;
            capture?.Register(id, channel, peer, native: isNative);
            log.Write("connected", new { Connection = id, Protocol = isNative ? "NATIVE" : channel ? "CHANNEL" : "GAME", Peer = client.Client.RemoteEndPoint?.ToString(),
                Local = client.Client.LocalEndPoint?.ToString() });
            client.NoDelay = true;
            await protocol.RunAsync(new(id, peer.Address, client.GetStream(), lifetime, channel, capture, endpoint,
                localAddress: (client.Client.LocalEndPoint as IPEndPoint)?.Address));
        }
        catch (Exception ex) when (DfoServer.Persistence.SqliteBusyRetry.IsBusy(ex))
        {
            // 忙锁不是连接故障：分发层已按包做过有界重试，漏网至此也只记录诊断，不再写成 closed 断线原因。
            log.Write("db_busy", new { Connection = id, Channel = channel,
                Protocol = protocol is Protocol.NativeChannel ? "NATIVE" : channel ? "CHANNEL" : "GAME", Stage = "connection",
                Attempts = 0, Message = ex.Message });
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or InvalidDataException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            log.Write("closed", new { Connection = id, Channel = channel, Reason = ex.GetType().Name, ex.Message });
        }
        finally
        {
            capture?.Remove(id);
            log.Write("disconnected", new { Connection = id, Protocol = protocol is Protocol.NativeChannel ? "NATIVE" : channel ? "CHANNEL" : "GAME" });
            connections.Release();
        }
    }
}
