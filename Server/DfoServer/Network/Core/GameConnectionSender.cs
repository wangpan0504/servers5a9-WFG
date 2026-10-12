using DfoServer.Game.Accounts;
using DfoServer.Network.Builders;
using System.Threading.Channels;

namespace DfoServer.Network;

internal sealed record RemoteTownActor(ushort Id, long Membership, ReadOnlyMemory<byte> Actor, ReadOnlyMemory<byte> State, ReadOnlyMemory<byte> Position, Func<bool> Visible);
// 一个连接只有一个写入泵；账号 gate 内只入队，网络等待及日志在泵内执行。
internal sealed class GameConnectionSender : IAsyncDisposable
{
    // 按包体及帧头计费，包含活动消息；共享包体在每个接收连接独立计费。
    internal const long MaxPendingBytes = 2 * 1024 * 1024;
    sealed record Output(byte Kind, ushort Opcode, byte[] Body, string Action, TaskCompletionSource? Done = null, Func<bool>? CanSend = null, bool Protected = false);
    sealed record Message(long Epoch, Output? Own = null, RemoteTownActor? Enter = null, (ushort, long)? Move = null, (ushort, long)? Leave = null, TaskCompletionSource? Clear = null, RemoteTownActor? Refresh = null, long Bytes = 0);
    readonly AccountOwners.Lease owner;
    readonly Stream stream;
    readonly CancellationTokenSource connection;
    readonly Action<byte, ushort, ushort, ReadOnlyMemory<byte>, string> sent;
    readonly Action<byte[]>? capture;
    readonly Channel<Message> queue = Channel.CreateBounded<Message>(new BoundedChannelOptions(256) { SingleReader = true, AllowSynchronousContinuations = false, FullMode = BoundedChannelFullMode.Wait });
    readonly object queueGate = new();
    readonly Dictionary<(ushort, long), RemoteTownActor> moves = [];
    readonly Dictionary<ushort, long> known = [];
    readonly Task pump;
    readonly Lazy<Task> cancellation;
    ushort sequence;
    long epoch;
    long pendingBytes;
    bool closed;
    public GameConnectionSender(AccountOwners.Lease owner, Stream stream, CancellationTokenSource connection, ushort sequence, Action<byte, ushort, ushort, ReadOnlyMemory<byte>, string> sent, Action<byte[]>? capture = null)
    {
        this.owner = owner;
        this.stream = stream;
        this.connection = connection;
        this.sequence = sequence;
        this.sent = sent;
        this.capture = capture;
        cancellation = new(CancelConnection);
        pump = Run();
    }

    public long Epoch => Volatile.Read(ref epoch);
    public long PendingBytes { get { lock (queueGate) return pendingBytes; } }

    public int PendingMoves
    {
        get
        {
            lock (queueGate)
                return moves.Count;
        }
    }

    public Task Send(byte kind, ushort opcode, byte[] body, string action, Func<bool>? canSend = null)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!Enqueue(new(Epoch, new(kind, opcode, body, action, done, canSend))))
            done.TrySetException(new IOException("Game send queue unavailable"));
        return done.Task;
    }

    public Task SendProtected(byte kind, ushort opcode, byte[] body, string action, Func<bool>? canSend = null)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!Enqueue(new(Epoch, new(kind, opcode, body, action, done, canSend, Protected: true))))
            done.TrySetException(new IOException("Game send queue unavailable"));
        return done.Task;
    }

    public void Post(byte kind, ushort opcode, byte[] body, string action, Func<bool>? canSend = null) =>
        Enqueue(new(Epoch, new(kind, opcode, body, action, CanSend: canSend)));

    public Task ClearScene()
    {
        long next = Interlocked.Increment(ref epoch);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!Enqueue(new(next, Clear: done)))
            done.TrySetException(new IOException("Game scene clear queue unavailable"));
        return done.Task;
    }

    public void InvalidateScene() => Interlocked.Increment(ref epoch);
    public void Enter(RemoteTownActor actor) => Enqueue(new(Epoch, Enter: actor));
    public void Refresh(RemoteTownActor actor) => Enqueue(new(Epoch, Refresh: actor));
    public void Leave(ushort id, long membership) => Enqueue(new(Epoch, Leave: (id, membership)));
    public void Move(RemoteTownActor actor)
    {
        var key = (actor.Id, actor.Membership);
        bool accepted = false;
        lock (queueGate)
        {
            if (!closed && !connection.IsCancellationRequested)
            {
                long bytes = ActorBytes(actor);
                if (moves.TryGetValue(key, out var previous))
                {
                    long difference = bytes - ActorBytes(previous);
                    if (difference <= MaxPendingBytes - pendingBytes)
                    {
                        moves[key] = actor;
                        pendingBytes += difference;
                        accepted = true;
                    }
                }
                else
                {
                    moves.Add(key, actor);
                    // 移动消息只保存键，其计费随合并字典中的最新快照变化。
                    accepted = TryEnqueue(new(Epoch, Move: key), bytes);
                    if (!accepted)
                        moves.Remove(key);
                }
            }
        }
        if (!accepted) Cancel();
    }

    bool Enqueue(Message message)
    {
        long bytes = message.Own is { } own ? own.Body.Length + 16L :
            message.Enter is { } enter ? ActorBytes(enter) :
            message.Refresh is { } refresh ? ActorBytes(refresh) : 18;
        bool accepted;
        lock (queueGate)
            accepted = TryEnqueue(message with { Bytes = bytes }, bytes);
        // 标记取消并异步执行回调，不能从源账号/成员锁内重入另一账号。
        if (!accepted) Cancel();
        return accepted;
    }

    bool TryEnqueue(Message message, long bytes)
    {
        if (closed || connection.IsCancellationRequested || bytes > MaxPendingBytes - pendingBytes)
            return false;
        pendingBytes += bytes;
        if (queue.Writer.TryWrite(message))
            return true;
        pendingBytes -= bytes;
        return false;
    }

    static long ActorBytes(RemoteTownActor actor) => (long)actor.Actor.Length + actor.State.Length + actor.Position.Length + 3 * 16;

    void Cancel() => _ = cancellation.Value;

    Task CancelConnection()
    {
        try
        {
            return connection.CancelAsync();
        }
        catch (ObjectDisposedException)
        {
            return Task.CompletedTask;
        }
    }

    async Task Write(byte kind, ushort opcode, ReadOnlyMemory<byte> body, string action, bool protectedFrame = false)
    {
        owner.Execute(() => true);
        ushort sentSequence = sequence;
        byte[] frame = protectedFrame ? GameFrame.BuildProtected(kind, opcode, body.Span) : GameFrame.Build(kind, opcode, sequence++, body.Span);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(connection.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        await stream.WriteAsync(frame, timeout.Token);
        capture?.Invoke(frame);
        owner.Execute(() => true);
        sent(kind, opcode, protectedFrame ? sentSequence : (ushort)(sequence - 1), body, action);
    }

    bool Visible(RemoteTownActor actor, long expected) => owner.Execute(() => Epoch == expected && actor.Visible());
    async Task<bool> EnsureActor(RemoteTownActor actor, long expected)
    {
        if (!Visible(actor, expected))
            return false;
        if (known.TryGetValue(actor.Id, out long current))
        {
            if (current == actor.Membership)
                return true;
            await Write(0, (ushort)NotiPacketType.USER_LEAVE, TownPackets.RemoveActor(actor.Id), "town_peer_leave");
            known.Remove(actor.Id);
        }

        if (!Visible(actor, expected))
            return false;
        await Write(0, (ushort)NotiPacketType.USERINFO, actor.Actor, "town_peer_actor");
        known[actor.Id] = actor.Membership;
        return true;
    }

    async Task Run()
    {
        Message? active = null;
        Exception? failure = null;
        try
        {
            await foreach (var message in queue.Reader.ReadAllAsync(connection.Token))
            {
                active = message;
                RemoteTownActor? latest = null;
                long activeBytes = message.Bytes;
                lock (queueGate)
                    if (message.Move is { } move && moves.Remove(move, out latest))
                        activeBytes = ActorBytes(latest);
                try
                {
                    if (message.Own is { } own)
                    {
                        if (owner.Execute(() => own.CanSend?.Invoke() ?? true))
                            await Write(own.Kind, own.Opcode, own.Body, own.Action, own.Protected);
                    }
                    else if (message.Clear is { } clear)
                    {
                        foreach (ushort id in known.Keys.ToArray())
                            await Write(0, (ushort)NotiPacketType.USER_LEAVE, TownPackets.RemoveActor(id), "town_peer_clear");
                        known.Clear();
                    }
                    else if (message.Enter is { } enter)
                    {
                        if (await EnsureActor(enter, message.Epoch) && Visible(enter, message.Epoch))
                        {
                            await Write(0, (ushort)NotiPacketType.USER_STATE, enter.State, "town_peer_state");
                            if (Visible(enter, message.Epoch))
                                await Write(0, (ushort)NotiPacketType.USER_POSITION, enter.Position, "town_peer_position");
                        }
                    }
                    else if (message.Refresh is { } refreshed)
                    {
                        bool alreadyKnown = known.TryGetValue(refreshed.Id, out long membership) && membership == refreshed.Membership;
                        if (await EnsureActor(refreshed, message.Epoch) && alreadyKnown && Visible(refreshed, message.Epoch))
                            await Write(0, (ushort)NotiPacketType.USERINFO, refreshed.Actor, "town_peer_equipment");
                    }
                    else if (message.Move is not null)
                    {
                        if (latest is not null && await EnsureActor(latest, message.Epoch) && Visible(latest, message.Epoch))
                            await Write(0, (ushort)NotiPacketType.USER_POSITION, latest.Position, "town_peer_position");
                    }
                    else if (message.Leave is { } left && Epoch == message.Epoch && known.GetValueOrDefault(left.Item1) == left.Item2)
                    {
                        await Write(0, (ushort)NotiPacketType.USER_LEAVE, TownPackets.RemoveActor(left.Item1), "town_peer_leave");
                        known.Remove(left.Item1);
                    }
                }
                finally
                {
                    lock (queueGate)
                        pendingBytes -= activeBytes;
                }

                message.Own?.Done?.TrySetResult();
                message.Clear?.TrySetResult();
                active = null;
            }
        }
        catch (Exception ex)
        {
            // 泵的任何失败都结束该连接并释放等待者；不能留下无人消费的可靠发送任务。
            failure = ex is IOException or OperationCanceledException or InvalidOperationException ? ex : new IOException("Game sender failed", ex);
            Cancel();
        }
        finally
        {
            failure ??= new IOException("Game sender closed");
            active?.Own?.Done?.TrySetException(failure);
            active?.Clear?.TrySetException(failure);
            lock (queueGate)
            {
                closed = true;
                queue.Writer.TryComplete();
            }
            while (queue.Reader.TryRead(out var message))
            {
                message.Own?.Done?.TrySetException(failure);
                message.Clear?.TrySetException(failure);
            }

            lock (queueGate)
            {
                moves.Clear();
                pendingBytes = 0;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        queue.Writer.TryComplete();
        Cancel();
        await pump;
        await cancellation.Value;
    }
}
