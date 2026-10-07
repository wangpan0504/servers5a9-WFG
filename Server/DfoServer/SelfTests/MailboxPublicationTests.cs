using DfoServer.Game.Mailbox;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Towns;
using DfoServer.Network;
using DfoServer.Network.Protocol;
using DfoServer.Network.Projection;
using DfoServer.Testing;

namespace DfoServer;

internal static class MailboxPublicationTests
{
    public static async Task RunAsync()
    {
        int before = TestCheck.Count;
        using var fixture = new MailboxPersistenceTests.Fixture();
        var point = fixture.Receiver.Position;
        var map = new TownMap("synthetic-mail.map", [new(new(0, 0, 1000, 1000), [], [])], [], false, []);
        var area = new TownArea(point.Town, point.Area, 1, [], [], [], point, map, [], []);
        var towns = new TownCatalog(new() { [(point.Town, point.Area)] = area }, point, point, new(0, [], []), [], new Dictionary<uint, uint[]>());
        var session = new GameSession(fixture.Repository, towns, fixture.ReceiverOwner);
        session.Select(0);
        var initialization = new MailboxReceipt(session.Selected!, fixture.ReceiverOwner.Generation, session.SelectionEpoch);
        TestCheck.That(session.CanPublishMailbox(initialization, initializing: true) && !session.CanPublishMailbox(initialization),
            "入城邮箱属于选角初始化阶段，普通响应仍等待 ready");
        session.MarkReady();
        TestCheck.That(!session.CanPublishMailbox(initialization, initializing: true) && session.CanPublishMailbox(initialization),
            "完成入城后旧初始化回执不再发布");
        var online = new OnlineSessionDirectory();
        online.Register(fixture.ReceiverOwner, session);
        using var stop = new CancellationTokenSource();
        using var stream = new PausedStream();
        await using var sender = new GameConnectionSender(fixture.ReceiverOwner, stream, stop, 0, (_, _, _, _, _) => { });
        var context = new GameConnectionContext(new(1, System.Net.IPAddress.Loopback, stream, stop), fixture.ReceiverOwner, session, sender);
        var projection = new MailboxProjection(null!, online);
        session.MailboxArrived = receipt => projection.Alarm(context, receipt);
        var result = new MailboxResult() { Recipient = fixture.Repository.FindMailRecipient("Receiver") };
        var watch = System.Diagnostics.Stopwatch.StartNew();
        projection.Arrived(result);
        TestCheck.That(watch.Elapsed < TimeSpan.FromSeconds(1), "发信完成不等待收件人 socket");
        await stream.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        TestCheck.That(fixture.SenderOwner.IsCurrent && !stop.IsCancellationRequested, "收件人阻塞不占用发送者账号或取消发送者");
        var old = new MailboxReceipt(session.Selected!, fixture.ReceiverOwner.Generation, session.SelectionEpoch);
        projection.Alarm(context, old);
        session.ReturnToRoster();
        TestCheck.That(!session.CanPublishMailbox(old) && !session.CanPublishMailbox(initialization, initializing: true),
            "返回选角使两类邮箱发布回执失效");
        stream.Release.TrySetResult();
        await sender.Send(1, 1347, [1], "test_barrier");
        TestCheck.That(stream.Frames.Count == 2 && stream.Frames[0][0] == 0 && BitConverter.ToUInt16(stream.Frames[0], 1) == 99 &&
            stream.Frames[0].AsSpan(16).SequenceEqual(new byte[] { 1, 0 }), "原提示完整投递，旧 epoch 排队提示跳过");
        session.Select(0); session.MarkReady();
        TestCheck.That(session.RefreshMailbox(null!, old) is null, "旧领取或删除回复不能在 await 后刷新后来选中的邮箱");
        projection.Arrived(result with { Recipient = result.Recipient! with { Id = fixture.Sender.Id } });
        await sender.Send(1, 1347, [1], "test_barrier");
        TestCheck.That(stream.Frames.Count == 3, "同账号其他角色不收到当前角色邮件提示");
        using var replacement = fixture.Owners.Acquire(fixture.ReceiverOwner.Account, () => { });
        session.NotifyMailbox(fixture.Receiver.Id);
        TestCheck.That(stream.Frames.Count == 3 && replacement.IsCurrent, "过期目录引用不向新连接投递或释放新 owner");
        stop.Cancel();
        Console.WriteLine($"PASS {TestCheck.Count - before} 邮件慢接收者隔离、epoch与原连接通知断言");
    }

    sealed class PausedStream : Stream
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<byte[]> Frames { get; } = [];
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            Frames.Add(buffer.ToArray());
        }
    }
}
