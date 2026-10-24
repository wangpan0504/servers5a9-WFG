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
        Console.WriteLine($"PASS {TestCheck.Count - checks} game sender budget assertions");
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

    sealed class HeldStream : MemoryStream
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
