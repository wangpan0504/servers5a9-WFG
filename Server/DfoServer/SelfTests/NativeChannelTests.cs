using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using DfoServer.Game.Accounts;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Protocol;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class NativeChannelTests
{
    public static async Task RunAsync()
    {
        int before = Count;
        string root = Path.Combine(Path.GetTempPath(), "S5A9-native-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var log = new ServerLog(root);
            var online = new OnlineSessionDirectory();
            var channel = new NativeChannel(new() { ListenAddress = "127.0.0.1" }, online, log);
            var owners = new AccountOwners();
            using var mainStop = new CancellationTokenSource();
            using var owner = owners.Acquire("native", mainStop.Cancel);
            using var stream = new MemoryStream();
            await using var sender = new GameConnectionSender(owner, stream, mainStop, 0, (_, _, _, _, _) => { });
            var point = new Location(1, 0, 10, 10);
            var towns = new TownCatalog(new(), point, point, new(0, [], []), [], new Dictionary<uint, uint[]>());
            var repository = new CharacterRepository(Path.Combine(root, "game.db"));
            var session = new GameSession(repository, towns, owner);
            var context = new GameConnectionContext(new(1, IPAddress.Loopback, stream, mainStop), owner, session, sender);
            online.Register(owner, session);
            Reject<InvalidOperationException>(() => channel.Register(context), "未启动不发行Native关联值");
            channel.Start();
            try
            {
                using var registration = channel.Register(context);
                That(registration.Token != 0, "主连接认证后注册非零关联值");
                using var firstStop = new CancellationTokenSource();
                using var firstBytes = new WaitingStream(registration.Token);
                using var fault = firstStop.Token.Register(() => throw new InvalidOperationException("cancel-test"));
                Task first = channel.RunAsync(new(2, IPAddress.Loopback, firstBytes, firstStop));
                That(registration.Current is not null, "第一帧绑定精确主连接");
                var old = registration.Current!;
                using var nextStop = new CancellationTokenSource();
                using var nextBytes = new WaitingStream(registration.Token);
                Task next = channel.RunAsync(new(3, IPAddress.Loopback, nextBytes, nextStop));
                await Cancelled(first);
                That(firstStop.IsCancellationRequested && registration.Current is not null &&
                    !ReferenceEquals(registration.Current, old), "旧Native取消回调失败不遗留无人持有的新租约");
                old.Dispose();
                That(registration.Current is not null && !nextStop.IsCancellationRequested,
                    "迟到旧Native清理不删除新绑定");
                registration.Dispose();
                await Cancelled(next);
                That(registration.Current is null && owner.IsCurrent && ReferenceEquals(online.Find("native"), session),
                    "Native清理仅移除自己的传输索引，保留原Session权威");
                using var staleBytes = new WaitingStream(registration.Token);
                using var staleStop = new CancellationTokenSource();
                await Rejected(channel.RunAsync(new(4, IPAddress.Loopback, staleBytes, staleStop)));

                using var shutdown = channel.Register(context);
                using var shutdownStop = new CancellationTokenSource();
                using var shutdownBytes = new WaitingStream(shutdown.Token);
                using var shutdownFault = shutdownStop.Token.Register(() => throw new InvalidOperationException("stop-test"));
                Task shutdownTask = channel.RunAsync(new(5, IPAddress.Loopback, shutdownBytes, shutdownStop));
                channel.Stop();
                await Cancelled(shutdownTask);
                That(shutdown.Current is null && shutdownStop.IsCancellationRequested, "停服回调异常仍清空Native租约");
                // 同生产 finally 顺序，Native断开错误不能阻止后续主会话清理。
                shutdown.Dispose(); online.Remove("native", session); session.End();
                That(online.Find("native") is null, "Native失败不跳过主Session清理");
                log.Flush();
                using var reader = new StreamReader(new FileStream(Path.Combine(root, "server.jsonl"),
                    FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
                That(reader.ReadToEnd().Contains("native_disconnect_failed"),
                    "取消回调失败保留明确诊断");
            }
            finally { channel.Stop(); }

            var occupied = new TcpListener(IPAddress.Loopback, 0);
            occupied.Start();
            int port = ((IPEndPoint)occupied.LocalEndpoint).Port;
            var blocked = new NativeChannel(new() { ListenAddress = "127.0.0.1", NativePort = port }, online, log);
            try { Reject<SocketException>(blocked.Start, "占用端口拒绝Native启动"); }
            finally { occupied.Stop(); }
            blocked.Start();
            That(blocked.Port == port, "启动失败释放临时监听，可恢复原端口");
            blocked.Stop();
        }
        finally { TestFiles.DeleteDirectory(root, true); }
        Console.WriteLine($"PASS {Count - before} native channel lifecycle assertions");
    }

    static async Task Cancelled(Task task)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(3)); }
        catch (OperationCanceledException) { That(true, "Native收包响应取消"); return; }
        throw new InvalidOperationException("FAIL Native connection was not cancelled");
    }
    static async Task Rejected(Task task)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(3)); }
        catch (InvalidDataException) { That(true, "失效关联值拒绝"); return; }
        throw new InvalidOperationException("FAIL expired Native association accepted");
    }

    // 首帧后保持读取挂起，精确观察替换、取消及迟到 finally，不依赖调度睡眠。
    sealed class WaitingStream : MemoryStream
    {
        public WaitingStream(uint token) : base(Frame(token)) { }
        static byte[] Frame(uint token)
        {
            byte[] frame = new byte[14]; frame[0] = 1; frame[2] = 14; frame[13] = 0xff;
            BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(4), token);
            return frame;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position < Length) return await base.ReadAsync(buffer, cancellationToken);
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }
}
