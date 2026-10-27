using System.Buffers.Binary;
using System.Net;
using System.Reflection;
using System.Threading.Channels;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Parsers;
using DfoServer.Network.Protocol;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class NativeObjectStateRelayTests
{
    // 复用已真实入场的四人Luke事务夹具；不反射设置Session/Run，只调用实际私有Relay分支。
    internal static async Task RunAsync(GameSession[] sessions, string root, Action advanceScene)
    {
        int before = Count;
        var online = new OnlineSessionDirectory();
        using var log = new ServerLog(Path.Combine(root, "native-observer"));
        var channel = new NativeChannel(new() { ListenAddress = "127.0.0.1" }, online, log);
        var stops = sessions.Select(_ => new CancellationTokenSource()).ToArray();
        var senders = new List<GameConnectionSender>();
        var registrations = new List<NativeChannel.Registration>();
        var nativeStops = new List<CancellationTokenSource>();
        var streams = new List<WireStream>();
        var tasks = new List<Task>();
        var method = typeof(NativeChannel).GetMethod("RelayAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        channel.Start();
        try
        {
            for (int i = 0; i < 3; i++)
            {
                var actor = sessions[i].PartyDungeon!.Members.Single(m => ReferenceEquals(m.Actor.Session, sessions[i])).Actor;
                online.Register(actor.Owner, sessions[i]);
                var sender = new GameConnectionSender(actor.Owner, Stream.Null, stops[i], 0, (_, _, _, _, _) => { });
                senders.Add(sender);
                var context = new GameConnectionContext(new(i + 1, IPAddress.Loopback, Stream.Null, stops[i]), actor.Owner, sessions[i], sender);
                var registration = channel.Register(context); registrations.Add(registration);
                Bind(registration, i + 10);
            }
            var source = registrations[0].Current!;
            var scene = sessions[0].CurrentFormalDungeonScene()!;
            byte[] plain = new byte[21]; plain[0] = 0x21; plain[2] = 9; plain[4] = 2;
            byte[] wire = NativeObjectStateTests.Frame(plain).Wire.ToArray(); wire[7] = scene.PartySlot;
            async Task<bool> Relay(byte[] body, int target, NativeChannel.NativeLease? lease = null)
            {
                var packet = NativeFrame.Parse(NativeFrame.Build(registrations[0].Token, registrations[target].Token, body));
                return await ((Task<bool>)method.Invoke(channel, [packet, lease ?? source])!).WaitAsync(TimeSpan.FromSeconds(5));
            }
            That(await Relay(wire, 1), "真实Luke Relay分支接纳第一目标写队列");
            Bytes(NativeFrame.Parse(await streams[1].Written.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5))).Body,
                wire, "实际Native发送泵输出原始Native5字节不变");
            That(await Relay(wire, 2), "同来源记录可继续中继到第二目标");
            Bytes(NativeFrame.Parse(await streams[2].Written.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5))).Body,
                wire, "第二目标同样原样收到，不因只读去重丢转发");
            That(source.StateObservations.Snapshot() is { Added: 1, Duplicates: 1 }, "真实多目标Relay只记一条新增观测");
            var wrong = wire.ToArray(); wrong[7] = 7;
            That(!await Relay(wrong, 1) && source.StateObservations.Snapshot().Added == 1, "伪造sourceSlot既不转发也不观测");
            var badCrc = wire.ToArray(); badCrc[^1] ^= 1;
            That(await Relay(badCrc, 1), "Native5坏CRC不收窄原relay合同");
            Bytes(NativeFrame.Parse(await streams[1].Written.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5))).Body,
                badCrc, "拒绝观测的Native5仍按原字节转发");
            That(source.StateObservations.Snapshot().Added == 1, "坏CRC没有进入观测账本");
            await sessions[0].CommandGate.WaitAsync();
            Task<bool>? staleScene = null;
            try
            {
                staleScene = Relay(wire, 1);
                That(!staleScene.IsCompleted, "旧场景捕获后在真实source命令门等待");
                advanceScene();
            }
            finally { sessions[0].CommandGate.Release(); }
            That(!await staleScene! && source.StateObservations.Snapshot().Added == 1,
                "等待门禁期间真实过图使旧场景失效，既不入队也不记账");
            Bind(registrations[0], 99);
            That(!await Relay(wire, 1, source) && source.StateObservations.Snapshot().Added == 1 &&
                registrations[0].Current!.StateObservations.Snapshot().Added == 0, "实际替换Native租约后旧source不能继续记账");
        }
        finally
        {
            channel.Stop();
            foreach (var stop in nativeStops) stop.Cancel();
            foreach (var task in tasks)
                try { await task.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { }
            foreach (var registration in registrations) registration.Dispose();
            foreach (var sender in senders) await sender.DisposeAsync();
            foreach (var stream in streams) stream.Dispose();
            foreach (var stop in nativeStops.Concat(stops)) stop.Dispose();
        }
        Console.WriteLine($"PASS {Count - before} 真实Luke Native relay只读观测接线断言");

        void Bind(NativeChannel.Registration registration, int id)
        {
            var stop = new CancellationTokenSource(); nativeStops.Add(stop);
            var stream = new WireStream(registration.Token); streams.Add(stream);
            tasks.Add(channel.RunAsync(new(id, IPAddress.Loopback, stream, stop)));
            That(registration.Current is not null, "通过真实Native首帧绑定原账号租约");
        }
    }

    sealed class WireStream(uint token) : Stream
    {
        readonly byte[] first = First(token);
        int offset;
        public Channel<byte[]> Written { get; } = Channel.CreateBounded<byte[]>(8);
        static byte[] First(uint token)
        {
            byte[] frame = new byte[14]; frame[0] = 1; frame[2] = 14; frame[13] = 0xff;
            BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(4), token); return frame;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (offset < first.Length) { int size = Math.Min(buffer.Length, first.Length - offset); first.AsMemory(offset, size).CopyTo(buffer); offset += size; return size; }
            await Task.Delay(Timeout.Infinite, cancellationToken); return 0;
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => Written.Writer.WriteAsync(buffer.ToArray(), cancellationToken);
        public override bool CanRead => true; public override bool CanWrite => true; public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
