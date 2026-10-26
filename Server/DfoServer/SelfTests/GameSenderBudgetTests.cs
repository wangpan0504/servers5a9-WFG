using DfoServer.Game.Accounts;
using DfoServer.Network;
using DfoServer.Testing;

namespace DfoServer;

internal static class GameSenderBudgetTests
{
    public static async Task RunAsync()
    {
        int checks = TestCheck.Count;
        await OverflowAsync(byBytes: true);
        await OverflowAsync(byBytes: false);
        await CoalescingAsync(stale: false);
        await CoalescingAsync(stale: true);
        await ActiveMoveAsync();
        await ReplacementOverflowAsync();
        await ConcurrentMovesAsync();
        await DepartureAreaAsync();
        await PhaseDepartureAsync();
        await WriteFailureAsync();
        await QueuedPublicationAsync();
        await QueuedPartyCommandAsync();
        Console.WriteLine($"PASS {TestCheck.Count - checks} game sender budget assertions");
    }

    static async Task QueuedPublicationAsync()
    {
        using var owner = new AccountOwners().Acquire("queued-publication", () => { });
        using var stop = new CancellationTokenSource();
        using var stream = new HeldStream();
        await using var sender = new GameConnectionSender(owner, stream, stop, 0, (_, _, _, _, _) => { });
        Task held = sender.Send(0, 2, [1], "held");
        await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        bool generation = true, revision = true;
        int verified = 0;
        using (sender.QueuePublication(() => generation))
        {
            TestCheck.That(sender.Send(0, 37, [2], "old-scene").IsCompletedSuccessfully,
                "共享结果入有界队列，不等待慢客户端写入");
            TestCheck.That(sender.ClearScene().IsCompletedSuccessfully, "清场与后续帧在同一队列排队，不占队伍门禁等网络");
            await sender.Send(0, 38, [3], "committed", () => { verified++; return revision; });
            await sender.Send(0, 38, [9], "rejected", () => false);
        }
        revision = false;
        Task barrier = sender.Send(1, 3, [4], "barrier");
        TestCheck.That(!barrier.IsCompleted && verified == 1 && sender.PendingBytes <= GameConnectionSender.MaxPendingBytes,
            "发布作用域退出后恢复可靠等待，守卫只在提交结果入队时检查revision");
        using var healthyOwner = new AccountOwners().Acquire("queued-healthy", () => { });
        using var healthyStop = new CancellationTokenSource();
        using var healthyStream = new MemoryStream();
        await using var healthy = new GameConnectionSender(healthyOwner, healthyStream, healthyStop, 0, (_, _, _, _, _) => { });
        await healthy.Send(1, 3, [1], "healthy").WaitAsync(TimeSpan.FromSeconds(2));
        TestCheck.That(healthyStream.Length > 0 && !barrier.IsCompleted, "另一连接发送不依赖慢连接完成");
        stream.Release.TrySetResult();
        await Task.WhenAll(held, barrier).WaitAsync(TimeSpan.FromSeconds(5));
        TestCheck.That(stream.Frames.Count == 4 && verified == 1, "拒绝帧不发送，清场前已提交帧与后续结果保持FIFO，不被后续revision吞掉");
        TestCheck.Bytes(stream.Frames[1], GameFrame.Build(0, 37, 1, [2]), "本人清场前的已提交消息必须先发出");
        TestCheck.Bytes(stream.Frames[2], GameFrame.Build(0, 38, 2, [3]), "已提交结果保持包序、CRC及完整内容");
        using (sender.QueuePublication(() => generation))
        {
            generation = false;
            await sender.Send(0, 38, [5], "stale-generation");
        }
        await sender.Send(1, 3, [6], "final-barrier");
        TestCheck.That(stream.Frames.Count == 5 && sender.PendingBytes == 0, "旧generation结果不入队，预算完整归还");
    }

    static async Task QueuedPartyCommandAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "s5a9-party-command-" + Guid.NewGuid().ToString("N"));
        try
        {
            var repository = new Persistence.CharacterRepository(Path.Combine(root, "game.db"));
            var point = new GameWorld.Towns.Location(1, 0, 10, 10);
            var towns = new GameWorld.Towns.TownCatalog([], point, point, new(0, [], []), [], new Dictionary<uint, uint[]>());
            var owners = new AccountOwners();
            using var slowOwner = owners.Acquire("command-slow", () => { });
            using var fastOwner = owners.Acquire("command-fast", () => { });
            using var slowStop = new CancellationTokenSource();
            using var fastStop = new CancellationTokenSource();
            using var slowStream = new HeldStream();
            using var fastStream = new MemoryStream();
            await using var slow = new GameConnectionSender(slowOwner, slowStream, slowStop, 0, (_, _, _, _, _) => { });
            await using var fast = new GameConnectionSender(fastOwner, fastStream, fastStop, 0, (_, _, _, _, _) => { });
            var party = new Game.Party.PartySnapshot(1, [], 0, 1);
            var slowSession = new Game.Session.GameSession(repository, towns, slowOwner) { CurrentParty = () => party };
            var fastSession = new Game.Session.GameSession(repository, towns, fastOwner) { CurrentParty = () => party };
            var slowContext = new Network.Protocol.GameConnectionContext(new(1, System.Net.IPAddress.Loopback, slowStream, slowStop),
                slowOwner, slowSession, slow);
            var fastContext = new Network.Protocol.GameConnectionContext(new(2, System.Net.IPAddress.Loopback, fastStream, fastStop),
                fastOwner, fastSession, fast);
            var commands = new Network.Protocol.GameCommandRegistry();
            commands.Register(CmdPacketType.SET_PARTY_INFO, async (context, _) =>
            {
                await context.Reply(CmdPacketType.SET_PARTY_INFO, [1], "party-local-reply");
                return Network.Protocol.CommandResult.Handled;
            }, nameof(GameSenderBudgetTests));
            commands.Register(CmdPacketType.EXIT, async (context, _) =>
            {
                await context.Reply(CmdPacketType.EXIT, [1], "party-exit-reply");
                return Network.Protocol.CommandResult.CloseConnection;
            }, nameof(GameSenderBudgetTests));
            commands.Seal();
            Task held = slow.Send(0, 2, [1], "held");
            await slowStream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var request = new Packet(1, (ushort)CmdPacketType.SET_PARTY_INFO, []);
            await commands.DispatchAsync(slowContext, request).WaitAsync(TimeSpan.FromSeconds(2));
            Task closing = commands.DispatchAsync(slowContext, new(1, (ushort)CmdPacketType.EXIT, []));
            TestCheck.That(!closing.IsCompleted, "退出须在门禁外等待队列中的应答真正写出");
            await commands.DispatchAsync(fastContext, request).WaitAsync(TimeSpan.FromSeconds(2));
            await fast.Send(0, 2, [2], "fast-barrier");
            TestCheck.That(!held.IsCompleted && fastStream.Length > 0 && party.Commands.CurrentCount == 1,
                "成员本地应答不等待慢写入，释放队伍门禁后队友命令和响应继续");
            slowStream.Release.TrySetResult();
            await Task.WhenAll(held, closing).WaitAsync(TimeSpan.FromSeconds(5));
            await slow.Send(0, 2, [3], "slow-barrier");
            TestCheck.That(slowStream.Frames.Count == 4 && slow.PendingBytes == 0, "慢连接恢复后按原序写出完整应答并释放预算");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    static async Task OverflowAsync(bool byBytes)
    {
        var owners = new AccountOwners();
        using var owner = owners.Acquire("budget", () => { });
        using var stop = new CancellationTokenSource();
        using var stream = new HeldStream();
        await using var sender = new GameConnectionSender(owner, stream, stop, 0, (_, _, _, _, _) => { });
        byte[] body = new byte[byBytes ? 65520 : 1];
        var tasks = new List<Task> { sender.Send(0, 2, body, "held") };
        await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        int queued = byBytes ? 31 : 256;
        for (int i = 0; i < queued; i++) tasks.Add(sender.Send(0, 2, body, "queued"));
        TestCheck.That(!stop.IsCancellationRequested && sender.PendingBytes == (queued + 1L) * (body.Length + 16), "active and queued messages retain their budget");
        var callbackCompleted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var callback = stop.Token.Register(() => callbackCompleted.TrySetResult(Task.Run(() => sender.PendingBytes).Wait(TimeSpan.FromSeconds(3))));
        tasks.Add(sender.Send(0, 2, body, "overflow"));
        TestCheck.That(stop.IsCancellationRequested && await callbackCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5)), "overflow cancels outside queue lock");
        await FailuresAsync(tasks);
        await sender.DisposeAsync();
        TestCheck.That(sender.PendingBytes == 0 && sender.PendingMoves == 0, "overflow drains all retained messages");
        TestCheck.That(stream.Frames.Count == 0, "blocked receiver cannot publish a successful send");

        // 一个慢接收连接退出，不应取消同一个账号集合中的其他连接。
        using var healthyOwner = owners.Acquire("healthy", () => { });
        using var healthyStop = new CancellationTokenSource();
        using var healthyStream = new MemoryStream();
        await using var healthy = new GameConnectionSender(healthyOwner, healthyStream, healthyStop, 9, (_, _, _, _, _) => { });
        await healthy.Send(1, 3, [1], "healthy").WaitAsync(TimeSpan.FromSeconds(5));
        TestCheck.Bytes(healthyStream.ToArray(), GameFrame.Build(1, 3, 9, [1]), "unrelated connection continues with unchanged frame");
        TestCheck.That(healthy.PendingBytes == 0 && !healthyStop.IsCancellationRequested, "completed reliable send releases budget before completion");
    }

    static async Task CoalescingAsync(bool stale)
    {
        using var owner = new AccountOwners().Acquire("coalescing", () => { });
        using var stop = new CancellationTokenSource();
        using var stream = new HeldStream();
        await using var sender = new GameConnectionSender(owner, stream, stop, 5, (_, _, _, _, _) => { });
        Task first = sender.Send(0, 2, [7], "held");
        await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var actor = Actor(1, 1, 100, 200, 300);
        sender.Move(actor);
        TestCheck.That(sender.PendingMoves == 1 && sender.PendingBytes == 17 + 648, "pending move includes all retained snapshot bodies");
        var latest = Actor(1, 1, 9, 8, 7);
        sender.Move(latest);
        TestCheck.That(sender.PendingMoves == 1 && sender.PendingBytes == 17 + 72, "smaller replacement releases budget without another queue entry");
        sender.Move(actor);
        TestCheck.That(sender.PendingBytes == 17 + 648, "larger replacement reserves only its size difference");
        sender.Move(latest);
        if (stale) sender.InvalidateScene();
        Task barrier = sender.Send(1, 3, [1], "barrier");
        Task skipped = sender.Send(1, 3, [2], "guard-rejected", () => false);
        stream.Release.TrySetResult();
        await Task.WhenAll(first, barrier, skipped).WaitAsync(TimeSpan.FromSeconds(5));
        TestCheck.That(sender.PendingBytes == 0 && sender.PendingMoves == 0, "sent, stale and guard-rejected messages release their budget");
        TestCheck.That(stream.Frames.Count == (stale ? 2 : 4), "coalesced move emits only the current visible actor and position");
        if (!stale)
        {
            TestCheck.Bytes(stream.Frames[1], GameFrame.Build(0, (ushort)NotiPacketType.USERINFO, 6, latest.Actor.Span), "latest actor frame keeps sequence and CRC");
            TestCheck.Bytes(stream.Frames[2], GameFrame.Build(0, (ushort)NotiPacketType.USER_POSITION, 7, latest.Position.Span), "latest position replaces the old position");
        }
        TestCheck.That(!stop.IsCancellationRequested, "ordinary coalescing does not cancel connection");
    }

    static async Task ActiveMoveAsync()
    {
        using var owner = new AccountOwners().Acquire("active-move", () => { });
        using var stop = new CancellationTokenSource();
        using var stream = new HeldStream();
        await using var sender = new GameConnectionSender(owner, stream, stop, 0, (_, _, _, _, _) => { });
        sender.Move(Actor(1, 1, 100, 200, 300) with { Area = new byte[10] });
        await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        TestCheck.That(sender.PendingMoves == 0 && sender.PendingBytes == 674, "active snapshot also charges the area payload and its frame header");
        sender.Move(Actor(1, 1, 9, 8, 7));
        TestCheck.That(sender.PendingMoves == 1 && sender.PendingBytes == 674 + 72, "next move and active move have independent charges");
        Task clear = sender.ClearScene();
        await sender.DisposeAsync();
        await FailuresAsync([clear]);
        TestCheck.That(sender.PendingBytes == 0 && sender.PendingMoves == 0, "shutdown releases active and queued move snapshots");
    }

    static async Task ReplacementOverflowAsync()
    {
        using var owner = new AccountOwners().Acquire("replacement", () => { });
        using var stop = new CancellationTokenSource();
        using var stream = new HeldStream();
        await using var sender = new GameConnectionSender(owner, stream, stop, 0, (_, _, _, _, _) => { });
        byte[] body = new byte[65520];
        var tasks = new List<Task> { sender.Send(0, 2, body, "held") };
        await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        for (int i = 0; i < 30; i++) tasks.Add(sender.Send(0, 2, body, "queued"));
        sender.Move(Actor(1, 1, 10, 10, 10));
        TestCheck.That(!stop.IsCancellationRequested && sender.PendingBytes == 31L * 65536 + 78, "small move fits remaining byte budget");
        sender.Move(Actor(1, 1, 40000, 40000, 1));
        TestCheck.That(stop.IsCancellationRequested, "replacement growth exceeding byte budget closes the receiver");
        await FailuresAsync(tasks);
        await sender.DisposeAsync();
        TestCheck.That(sender.PendingBytes == 0, "replacement overflow releases all original charges");
    }

    static async Task ConcurrentMovesAsync()
    {
        using var owner = new AccountOwners().Acquire("concurrent", () => { });
        using var stop = new CancellationTokenSource();
        using var stream = new HeldStream();
        await using var sender = new GameConnectionSender(owner, stream, stop, 0, (_, _, _, _, _) => { });
        Task first = sender.Send(0, 2, [1], "held");
        await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.WhenAll(Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
        {
            for (int i = 0; i < 100; i++) sender.Move(Actor((ushort)(worker + 1), 1, i, i + 1, i + 2));
        })));
        TestCheck.That(sender.PendingMoves == 8 && sender.PendingBytes == 17 + 8 * 348, "concurrent replacements keep exact final charges");
        sender.InvalidateScene();
        Task barrier = sender.ClearScene();
        stream.Release.TrySetResult();
        await Task.WhenAll(first, barrier).WaitAsync(TimeSpan.FromSeconds(5));
        TestCheck.That(sender.PendingBytes == 0 && sender.PendingMoves == 0 && stream.Frames.Count == 1, "stale concurrent moves drain without sending old snapshots");
    }

    static async Task WriteFailureAsync()
    {
        using var owner = new AccountOwners().Acquire("failure", () => { });
        using var stop = new CancellationTokenSource();
        using var stream = new HeldStream { FailWrite = true };
        await using var sender = new GameConnectionSender(owner, stream, stop, 0, (_, _, _, _, _) => { });
        var tasks = new List<Task> { sender.Send(0, 2, [1], "held") };
        await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        tasks.Add(sender.Send(0, 2, [2], "queued"));
        tasks.Add(sender.ClearScene());
        stream.Release.TrySetResult();
        await FailuresAsync(tasks);
        await sender.DisposeAsync();
        TestCheck.That(stop.IsCancellationRequested && sender.PendingBytes == 0, "write failure cancels connection and drains reliable waiters");
        await FailuresAsync([sender.Send(0, 2, [3], "closed")]);
        TestCheck.That(sender.PendingBytes == 0, "closed queue rejects without reserving bytes");
    }

    static async Task DepartureAreaAsync()
    {
        using var owner = new AccountOwners().Acquire("departure-area", () => { });
        using var stop = new CancellationTokenSource();
        using var stream = new HeldStream();
        await using var sender = new GameConnectionSender(owner, stream, stop, 0, (_, _, _, _, _) => { });
        Task held = sender.Send(1, 1347, [1], "held");
        await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        byte[] area = [1, 0, 38, 2, 61, 4, 249, 0, 0, 1];
        sender.Enter(Actor(1, 1, 1, 1, 1));
        sender.Leave(1, 1, destinationArea: area);
        sender.Leave(1, 2, destinationArea: area);
        TestCheck.That(sender.PendingBytes == 17 + 51 + 2 * 26, "切区离场及过期成员消息均计入区域包体和帧头");
        Task barrier = sender.Send(1, 1347, [1], "barrier");
        stream.Release.TrySetResult();
        await Task.WhenAll(held, barrier).WaitAsync(TimeSpan.FromSeconds(5));
        TestCheck.That(stream.Frames.Count == 6 && sender.PendingBytes == 0, "精确成员仅离场一次并释放区域消息预算");
        TestCheck.Bytes(stream.Frames[4], GameFrame.Build(0, 23, 4, area), "普通切区发送目标区域而非销毁队伍引用的NOTI6");
        sender.Enter(Actor(1, 3, 1, 1, 1));
        await sender.Send(1, 1347, [1], "reenter");
        int before = stream.Frames.Count;
        await sender.ClearScene(SceneActorClear.Preserve);
        TestCheck.That(stream.Frames.Count == before, "本人普通切区只清服务器可见集合，不发NOTI6");
        sender.Enter(Actor(1, 4, 1, 1, 1));
        await sender.Send(1, 1347, [1], "new-scene");
        await sender.ClearScene();
        TestCheck.Bytes(stream.Frames[^1], GameFrame.Build(0, 6, (ushort)(stream.Frames.Count - 1), [1, 0]),
            "重逢后真正清场仍删除当前Actor，普通切区没有遗留旧membership");
    }

    static async Task PhaseDepartureAsync()
    {
        foreach (bool staleView in new[] { false, true })
        {
            using var owner = new AccountOwners().Acquire("phase-budget", () => { });
            using var stop = new CancellationTokenSource();
            using var stream = new HeldStream();
            await using var sender = new GameConnectionSender(owner, stream, stop, 0, (_, _, _, _, _) => { });
            Task held = sender.Send(1, 1347, [1], "held");
            await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            sender.Enter(Actor(1, 1, 1, 1, 1));
            sender.Leave(1, 2, hideActor: true);
            sender.Leave(1, 1, hideActor: true);
            sender.Leave(2, 1, hideActor: true);
            TestCheck.That(sender.PendingBytes == 17 + 51 + 3 * 23,
                "阶段离场按两行状态包体7B及16B帧头计费，包括未知和旧membership");
            Task cleared = staleView ? sender.ClearScene(SceneActorClear.Hide) : Task.CompletedTask;
            if (staleView) TestCheck.That(sender.PendingBytes == 17 + 51 + 4 * 23, "阶段清场控制消息保留完整状态帧额度");
            Task barrier = sender.Send(1,1347,[1],"barrier");
            stream.Release.TrySetResult();
            await Task.WhenAll(held,cleared,barrier).WaitAsync(TimeSpan.FromSeconds(5));
            TestCheck.That(sender.PendingBytes == 0 && stream.Frames.Count == (staleView ? 2 : 6),
                "旧视图不会产生幽灵实体或删除未见Actor，所有阶段消息释放原额度");
            if (!staleView)
                TestCheck.Bytes(stream.Frames[4],GameFrame.Build(0,3,4,[2,1,0,1,1,0,0]),
                    "精确membership只隐藏一次且在同帧恢复城镇状态，没有NOTI6");
            sender.Enter(Actor(1,3,1,1,1));
            await sender.Send(1,1347,[1],"reenter");
            int before = stream.Frames.Count;
            await sender.ClearScene(SceneActorClear.Hide);
            TestCheck.That(stream.Frames.Count == before + 1 && sender.PendingBytes == 0,
                "新阶段清场隐藏已送达实体且完成后预算归零");
            TestCheck.Bytes(stream.Frames[^1],GameFrame.Build(0,3,(ushort)before,[2,1,0,1,1,0,0]),
                "本人阶段清场也保留Actor和队伍引用");
        }
    }

    static RemoteTownActor Actor(ushort id, long membership, int actor, int state, int position) =>
        new(id, membership, new byte[actor], new byte[state], new byte[position], () => true);

    static async Task FailuresAsync(IEnumerable<Task> tasks)
    {
        foreach (var task in tasks)
        {
            bool rejected = false;
            try { await task.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception ex) when (ex is IOException or OperationCanceledException) { rejected = true; }
            TestCheck.That(rejected, "reliable waiter ends with failure, never hangs or succeeds after cancellation");
        }
    }

    internal sealed class HeldStream : MemoryStream
    {
        public bool FailWrite { get; init; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<byte[]> Frames { get; } = [];
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            if (FailWrite) throw new IOException("synthetic send failure");
            Frames.Add(buffer.ToArray());
        }
    }
}
