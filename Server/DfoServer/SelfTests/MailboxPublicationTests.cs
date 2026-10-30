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
        TestCheck.That(session.CanPublishMailbox(initialization, initializing: true) && !session.CanPublishMailbox(initialization) &&
            !session.CanPublishMailboxAlarm(initialization),
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
        TestCheck.That(!session.CanPublishMailbox(old) && !session.CanPublishMailboxAlarm(old) &&
            !session.CanPublishMailbox(initialization, initializing: true),
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
        // GM 网页的新邮件提醒走真实 GmWebData：账号不符、同账号其他角色、未知角色都不提醒；匹配时经原连接投递一次。
        string gmTemp = Path.Combine(Path.GetTempPath(), "gm-notify-" + Guid.NewGuid().ToString("N"));
        var gmData = new DfoServer.Infrastructure.GmWebData(new ServerOptions { DataDirectory = gmTemp }, DateTimeOffset.UtcNow, online,
            new DfoServer.Persistence.GmAccountDirectory(Path.Combine(gmTemp, "game.db")), _ => "", new MailboxService(fixture.Repository, null!, null!, null!), null!, null!,
            new DfoServer.Network.Management.GmAudit(Path.Combine(gmTemp, "audit.jsonl")));
        gmData.NotifyMailArrived("no-such-account", "Receiver");
        gmData.NotifyMailArrived(fixture.ReceiverOwner.Account, "Sender");
        gmData.NotifyMailArrived(fixture.ReceiverOwner.Account, "NoSuchCharacter");
        await sender.Send(1, 1347, [1], "test_barrier");
        TestCheck.That(stream.Frames.Count == 4, "GM 提醒：账号不符、其他角色、未知角色都不投递");
        gmData.NotifyMailArrived(fixture.ReceiverOwner.Account, "Receiver");
        await sender.Send(1, 1347, [1], "test_barrier");
        TestCheck.That(stream.Frames.Count == 6 && BitConverter.ToUInt16(stream.Frames[4], 1) == 99, "GM 提醒：匹配的在线角色收到一次 MAILBOX_ALARM");
        // 非城镇仍投递新邮件提示，但不开放邮件列表或操作；沿用真实选图/返城入口。
        session.BeginDungeonSelection();
        var outsideTown = new MailboxReceipt(session.Selected!, fixture.ReceiverOwner.Generation, session.SelectionEpoch);
        TestCheck.That(session.CanPublishMailboxAlarm(outsideTown) && !session.CanPublishMailbox(outsideTown) &&
            session.RefreshMailbox(null!, outsideTown) is null &&
            !session.SendMail(null!, null!).Accepted && !session.ClaimMail(null!, []).Accepted &&
            !session.ChangeMailState(null!, [], 0).Accepted && !session.QueryMailRecipient(null!, "Receiver").Accepted,
            "非城镇新邮件可提醒，列表与收发及状态操作仍拒绝");
        TestCheck.That(!session.CanPublishMailboxAlarm(outsideTown with { Generation = outsideTown.Generation + 1 }) &&
            !session.CanPublishMailboxAlarm(outsideTown with { SelectionEpoch = outsideTown.SelectionEpoch + 1 }),
            "新邮件提示仍拒绝不匹配的 generation 和选角 epoch");
        session.NotifyMailbox(fixture.Receiver.Id, restoreOnTown: true);
        await sender.Send(1, 1347, [1], "test_barrier");
        TestCheck.That(stream.Frames.Count == 8 && BitConverter.ToUInt16(stream.Frames[6], 1) == 99 &&
            stream.Frames[6].AsSpan(16).SequenceEqual(new byte[] { 1, 0 }) && session.PendingMailboxRestore() is null,
            "非城镇99提示经原连接实际投递，返城恢复提示尚不消费");
        session.ReturnFromDungeonSelection();
        var restore = session.PendingMailboxRestore();
        TestCheck.That(restore is not null && session.CanPublishMailbox(outsideTown), "返城保留当前选角邮件恢复提示并恢复列表发布资格");
        session.ConfirmMailboxRestore(restore!);
        TestCheck.That(session.PendingMailboxRestore() is null, "当前返城恢复提示仍按原身份消费");
        // GM 强制小退：无连接钩子、角色名不符、账号不在线都不动会话；匹配时经连接钩子回到选角。
        var account = fixture.ReceiverOwner.Account;
        TestCheck.That(!await online.ForceReturnToSelectionAsync(account) && session.Selected is not null, "未安装连接钩子时强制小退不改变会话");
        int hookCalls = 0;
        session.ReturnToSelectionRequested = () => { hookCalls++; session.ReturnToRoster(); return Task.CompletedTask; };
        TestCheck.That(!await online.ForceReturnToSelectionAsync(account, "NoSuchCharacter") && hookCalls == 0 && session.Selected is not null, "角色名不符不触发小退");
        TestCheck.That(!await online.ForceReturnToSelectionAsync("no-such-account") && hookCalls == 0, "账号不在线不触发小退");
        TestCheck.That(await online.ForceReturnToSelectionAsync(account, "Receiver".ToUpperInvariant()) && hookCalls == 1 && session.Selected is null &&
            session.Phase == SessionPhase.Unselected && !session.Ready, "匹配角色强制小退后回到选角（角色名不区分大小写）");
        TestCheck.That(!await online.ForceReturnToSelectionAsync(account) && hookCalls == 1, "已在选角界面时再次强制小退无效果");
        session.Select(0); session.MarkReady();
        var ownerReceipt = new MailboxReceipt(session.Selected!, fixture.ReceiverOwner.Generation, session.SelectionEpoch);
        using var replacement = fixture.Owners.Acquire(fixture.ReceiverOwner.Account, () => { });
        session.NotifyMailbox(fixture.Receiver.Id);
        TestCheck.Reject<InvalidOperationException>(() => session.CanPublishMailboxAlarm(ownerReceipt), "旧 owner 不能发布新邮件提示");
        TestCheck.That(stream.Frames.Count == 8 && replacement.IsCurrent, "过期目录引用不向新连接投递或释放新 owner");
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
