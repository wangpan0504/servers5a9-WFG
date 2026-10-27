using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.Game.Dungeons;
using DfoServer.Network.Parsers;
using DfoServer.Network.Projection;

namespace DfoServer.Network.Protocol;

// 只索引原游戏连接的传输引用；owner、角色和副本状态仍归原 GameSession。
internal sealed partial class NativeChannel(ServerOptions options, OnlineSessionDirectory online, ServerLog log,
    NpcFavorAppearanceService? favorNpcs = null) : IConnectionProtocol
{
    readonly ConcurrentDictionary<uint, Registration> registrations = new();
    TcpListener? listener;
    public TcpListener Listener => listener ?? throw new InvalidOperationException("Native listener not started");
    public int Port => ((IPEndPoint)Listener.LocalEndpoint).Port;

    public void Start()
    {
        if (listener is not null) throw new InvalidOperationException("Native listener already started");
        var next = new TcpListener(IPAddress.Parse(options.ListenAddress), options.NativePort);
        try { next.Start(); listener = next; }
        catch { next.Stop(); throw; }
    }

    public void Stop()
    {
        listener?.Stop();
        listener = null;
        foreach (var entry in registrations.Values) entry.Dispose();
    }

    public Registration Register(GameConnectionContext context)
    {
        _ = Port;
        for (int attempt = 0; attempt < 16; attempt++)
        {
            uint token = BinaryPrimitives.ReadUInt32LittleEndian(RandomNumberGenerator.GetBytes(4));
            if (token == 0) continue;
            var entry = new Registration(this, context, token);
            if (context.Owner.Execute(() => IsOnline(context) && registrations.TryAdd(token, entry)))
                return entry;
        }
        throw new InvalidOperationException("Native association registration failed");
    }

    bool IsOnline(GameConnectionContext context) => !context.Stop.IsCancellationRequested &&
        ReferenceEquals(online.Find(context.Owner.Account), context.Session);

    bool IsRegistered(Registration entry) => registrations.TryGetValue(entry.Token, out var current) &&
        ReferenceEquals(current, entry) && IsOnline(entry.Context);

    NativeLease Bind(uint token, ConnectionContext connection)
    {
        if (!registrations.TryGetValue(token, out var entry))
            throw new InvalidDataException("Unknown native association");
        NativeLease? previous = null;
        var next = entry.Context.Owner.Execute(() =>
        {
            if (!IsRegistered(entry)) throw new InvalidDataException("Expired native association");
            var value = new NativeLease(entry, connection, () => Disconnect(connection));
            previous = Interlocked.Exchange(ref entry.Current, value);
            return value;
        });
        // 断开旧连接放在账号锁外；旧 finally 仅清理自己的传输租约。
        try { previous?.Disconnect(); return next; }
        catch { next.Dispose(); throw; }
    }

    void Disconnect(ConnectionContext connection)
    {
        try { connection.Disconnect(); }
        catch (AggregateException error)
        {
            // Cancel 已请求停止；回调失败单独诊断，不能中断其它连接及主会话收尾。
            log.Write("native_disconnect_failed", new { Connection = connection.Id,
                Reason = error.GetType().Name, Callbacks = error.InnerExceptions.Count });
        }
    }

    public async Task RunAsync(ConnectionContext connection)
    {
        var packet = await NativeFrame.ReadAsync(connection.Stream, connection.Stop);
        await using var lease = Bind(packet.Association, connection);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(connection.Stop, lease.Context.Stop);
        log.Write("native_associated", new { Connection = connection.Id, GameConnection = lease.Context.Id,
            lease.Context.Owner.Generation });
        bool unimplementedLogged = false;
        while (!lifetime.IsCancellationRequested)
        {
            if (packet.Association != lease.Entry.Token)
                throw new InvalidDataException("Native association changed");
            connection.CaptureNative(packet.WireLength, packet.Body.Length);
            if (favorNpcs is not null && await RelayAsync(packet, lease))
            {
                packet = await NativeFrame.ReadAsync(connection.Stream, lifetime.Token);
                continue;
            }
            await lease.Context.CommandGate.WaitAsync(lifetime.Token);
            try
            {
                lease.Context.Owner.Execute(() =>
                {
                    if (!IsRegistered(lease.Entry) || !ReferenceEquals(Volatile.Read(ref lease.Entry.Current), lease))
                        throw new InvalidDataException("Expired native connection");
                    return true;
                });
                // 未接入真实实例来源的载荷只记录边界，不向任务领域伪造观察或成功应答。
                if (!unimplementedLogged)
                {
                    log.Write("native_unimplemented", new { Connection = connection.Id, GameConnection = lease.Context.Id,
                        Length = packet.Body.Length });
                    unimplementedLogged = true;
                }
            }
            finally { lease.Context.CommandGate.Release(); }
            packet = await NativeFrame.ReadAsync(connection.Stream, lifetime.Token);
        }
    }

    public sealed class Registration(NativeChannel channel, GameConnectionContext context, uint token) : IDisposable
    {
        public GameConnectionContext Context { get; } = context;
        public uint Token { get; } = token;
        internal NativeLease? Current;

        public void Dispose()
        {
            NativeLease? old;
            try { old = Context.Owner.Execute(Remove); }
            catch (InvalidOperationException) when (!Context.Owner.IsCurrent) { old = Remove(); }
            old?.Disconnect();

            NativeLease? Remove()
            {
                ((ICollection<KeyValuePair<uint, Registration>>)channel.registrations).Remove(new(Token, this));
                return Interlocked.Exchange(ref Current, null);
            }
        }
    }

    internal sealed class NativeLease : IDisposable, IAsyncDisposable
    {
        readonly Action disconnect;
        readonly NativeConnectionSender sender;
        public NativeStateObservation StateObservations { get; } = new();
        public Registration Entry { get; }
        public NativeLease(Registration entry, ConnectionContext connection, Action disconnect)
        {
            Entry = entry;
            this.disconnect = disconnect;
            sender = new(connection, entry.Context.Stop, disconnect);
        }
        public GameConnectionContext Context => Entry.Context;
        public void Disconnect() => disconnect();
        public void Dispose() => Interlocked.CompareExchange(ref Entry.Current, null, this);
        public bool Post(byte[] frame, Func<bool> current) => sender.Post(frame, current);
        public async ValueTask DisposeAsync()
        {
            Dispose();
            await sender.DisposeAsync();
        }
    }
}
