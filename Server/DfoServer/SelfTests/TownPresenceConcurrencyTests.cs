using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.GameWorld.Towns;
using DfoServer.Network;
using DfoServer.Network.Projection;
using DfoServer.Testing;

namespace DfoServer;

internal static class TownPresenceConcurrencyTests
{
    public static async Task RunAsync()
    {
        int before = TestCheck.Count;
        await VisibilityAsync();
        await CancellationAsync();
        Console.WriteLine($"PASS {TestCheck.Count - before} 城镇跨账号锁序、可见性、选图离场与取消收尾断言");
    }

    static async Task VisibilityAsync()
    {
        var location = new Location(1, 0, 10, 10);
        var area = new TownArea(1, 0, 1, [], [], [], location,
            new("shared.map", [new(new(0, 0, 20, 20), [], [])], [], false, []), [], []);
        var towns = new TownCatalog(new() { [(1, 0)] = area }, location, location, new(0, [], []), [], new Dictionary<uint, uint[]>());
        var presence = new TownPresence(towns);
        var owners = new AccountOwners();
        using var a = owners.Acquire("scene-a", () => { });
        using var b = owners.Acquire("scene-b", () => { });
        using var stopA = new CancellationTokenSource();
        using var stopB = new CancellationTokenSource();
        var sentA = new List<(byte Kind, ushort Opcode, byte[] Body)>();
        var sentB = new List<(byte Kind, ushort Opcode, byte[] Body)>();
        await using var sendA = new GameConnectionSender(a, Stream.Null, stopA, 0,
            (kind, opcode, _, body, _) => sentA.Add((kind, opcode, body.ToArray())));
        await using var sendB = new GameConnectionSender(b, Stream.Null, stopB, 0,
            (kind, opcode, _, body, _) => sentB.Add((kind, opcode, body.ToArray())));
        var roleA = new Character(1, 0, "SceneA", 1, location, location);
        var roleB = new Character(2, 0, "SceneB", 1, location, location);
        presence.Join(a, sendA, roleA);
        presence.Join(b, sendB, roleB);
        await Task.WhenAll(Drain(sendA), Drain(sendB));
        using var ready = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var blocked = Task.Run(() => a.Execute(() =>
        {
            presence.Refresh(a, sendA, roleA);
            ready.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
            return true;
        }));
        TestCheck.That(ready.Wait(TimeSpan.FromSeconds(5)), "源账号发布快照后仍持业务锁");
        try
        {
            await Drain(sendB);
            TestCheck.That(!stopB.IsCancellationRequested && presence.Count == 2, "接收者可见性检查不等待源账号锁");
        }
        finally { release.Set(); }
        await blocked.WaitAsync(TimeSpan.FromSeconds(5));

        using var both = new CountdownEvent(2);
        Task Update(AccountOwners.Lease owner, GameConnectionSender sender, Character character) => Task.Run(() => owner.Execute(() =>
        {
            both.Signal();
            if (!both.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Account gates are not independent");
            presence.Move(owner, sender, character);
            return true;
        }));
        await Task.WhenAll(Update(a, sendA, roleA), Update(b, sendB, roleB)).WaitAsync(TimeSpan.FromSeconds(5));
        await Task.WhenAll(Drain(sendA), Drain(sendB));
        TestCheck.That(!stopA.IsCancellationRequested && !stopB.IsCancellationRequested, "双向广播不嵌套对方账号锁");
        await Task.WhenAll(Task.Run(() => presence.Remove(sendA)), Task.Run(() => presence.Remove(sendB)));
        await Task.WhenAll(Drain(sendA), Drain(sendB));
        TestCheck.That(presence.Count == 0, "并发精确离场回收全部索引");

        using var observer = owners.Acquire("scene-observer", () => { });
        using var stopObserver = new CancellationTokenSource();
        var sentObserver = new List<(byte Kind, ushort Opcode, byte[] Body)>();
        await using var sendObserver = new GameConnectionSender(observer, Stream.Null, stopObserver, 0,
            (kind, opcode, _, body, _) => sentObserver.Add((kind, opcode, body.ToArray())));
        presence.Join(a, sendA, roleA);
        presence.Join(b, sendB, roleB);
        presence.Join(observer, sendObserver, new(3, 0, "Observer", 1, location, location));
        await Task.WhenAll(Drain(sendA), Drain(sendB), Drain(sendObserver));
        sentA.Clear();
        sentB.Clear();
        sentObserver.Clear();
        presence.Remove(sendA, preserveActors: new HashSet<ushort> { roleA.Id, roleB.Id });
        await sendA.ClearScene(SceneActorClear.Hide);
        await Task.WhenAll(Drain(sendA), Drain(sendB), Drain(sendObserver));
        TestCheck.That(sentB.Any(frame => frame is { Kind: 0, Opcode: 3 } &&
            frame.Body.SequenceEqual(new byte[] { 2, 1, 0, 1, 1, 0, 0 })) && sentB.All(frame => frame.Opcode != 6),
            "组队选图在队友视图中只隐藏实体，不发送删除Actor的6号通知");
        TestCheck.That(sentObserver.Any(frame => frame is { Kind: 0, Opcode: 6 } &&
            frame.Body.SequenceEqual(new byte[] { 1, 0 })) && sentObserver.All(frame => frame is not { Kind: 0, Opcode: 3, Body.Length: 7 }),
            "非队友城镇观察者仍删除离场Actor，不改变原有离场语义");
        TestCheck.That(new byte[] { 2, 3 }.All(id => sentA.Any(frame => frame is { Kind: 0, Opcode: 3 } &&
            frame.Body.SequenceEqual(new byte[] { 2, id, 0, 1, id, 0, 0 }))) && sentA.All(frame => frame.Opcode != 6),
            "本人选图清场隐藏旧实体并清理同屏账本，不销毁已缓存Actor");
        presence.Remove(sendB);
        presence.Remove(sendObserver);
        await Task.WhenAll(Drain(sendB), Drain(sendObserver));
        TestCheck.That(presence.Count == 0, "保留Actor的选图离场仍回收全部城镇成员索引");
    }

    static async Task CancellationAsync()
    {
        var owners = new AccountOwners();
        using var source = owners.Acquire("source", () => { });
        using var target = owners.Acquire("target", () => { });
        using var stop = new CancellationTokenSource();
        using var stream = new BlockedStream();
        await using var sender = new GameConnectionSender(target, stream, stop, 0, (_, _, _, _, _) => { });
        var sends = new List<Task> { sender.Send(0, 2, new byte[65520], "blocked") };
        await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Task held = Task.Run(() => target.Execute(() =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
            return true;
        }));
        TestCheck.That(entered.Wait(TimeSpan.FromSeconds(5)), "接收账号持锁以验证取消回调不反向等待");
        var callback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = stop.Token.Register(() =>
        {
            target.Dispose();
            callback.TrySetResult();
        });
        try
        {
            await Task.Run(() => source.Execute(() =>
            {
                for (int i = 0; i < 32; i++) sends.Add(sender.Send(0, 2, new byte[65520], "overflow"));
                return true;
            })).WaitAsync(TimeSpan.FromSeconds(2));
            TestCheck.That(stop.IsCancellationRequested && !callback.Task.IsCompleted, "源账号不等待接收账号取消清理，取消标记立即生效");
        }
        finally { release.Set(); }
        await held.WaitAsync(TimeSpan.FromSeconds(5));
        await sender.DisposeAsync();
        await callback.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try { await Task.WhenAll(sends).WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (Exception error) when (error is IOException or OperationCanceledException or InvalidOperationException) { }
        TestCheck.That(sends.All(task => task.IsCanceled || task.IsFaulted) && sender.PendingBytes == 0 && !target.IsCurrent,
            "收尾等待取消回调、发送泵及全部可靠任务，不遗留owner和队列");
        TestCheck.That(source.Execute(() => true), "源账号未被慢接收者取消");
    }

    static Task Drain(GameConnectionSender sender) => sender.Send(1, 3, [1], "presence-concurrency-barrier").WaitAsync(TimeSpan.FromSeconds(2));

    sealed class BlockedStream : MemoryStream
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
    }
}
