using DfoServer.Game.Accounts;
using DfoServer.Network.Builders;
using System.Threading.Channels;

namespace DfoServer.Network;

internal sealed record RemoteTownActor(ushort Id, long Membership, ReadOnlyMemory<byte> Actor, ReadOnlyMemory<byte> State, ReadOnlyMemory<byte> Position, Func<bool> Visible)
{
    public ReadOnlyMemory<byte> Area { get; init; }
    public IReadOnlyList<byte[]> EchonSets { get; init; } = [];
}
internal enum SceneActorClear { Delete, Preserve, Hide }
// 一个连接只有一个写入泵；账号 gate 内只入队，网络等待及日志在泵内执行。
internal sealed class GameConnectionSender : IAsyncDisposable
{
    // 按包体及帧头计费，包含活动消息；共享包体在每个接收连接独立计费。
    internal const long MaxPendingBytes = 2 * 1024 * 1024;
    sealed record Output(byte Kind, ushort Opcode, byte[] Body, string Action, TaskCompletionSource? Done = null, Func<bool>? CanSend = null, bool Protected = false, Action? Written = null, Func<byte[]?>? Prepare = null, Action? AfterWritten = null, bool AllowShorter = false);
    sealed record Message(long Epoch, Output? Own = null, RemoteTownActor? Enter = null, (ushort, long)? Move = null,
        (ushort Id, long Membership, byte[]? Effect, Func<bool>? Current, byte[]? Prepare, byte[]? DestinationArea, bool HideActor)? Leave = null,
        TaskCompletionSource? Barrier = null, RemoteTownActor? Refresh = null, SceneActorClear Actors = SceneActorClear.Delete, long Bytes = 0, bool IsClear = false);
    readonly AccountOwners.Lease owner;
    readonly Stream stream;
    readonly CancellationTokenSource connection;
    readonly Action<byte, ushort, ushort, ReadOnlyMemory<byte>, string> sent;
    readonly Action<byte[]>? capture;
    readonly Action<string, Exception?>? failed;
    int failureReported;
    readonly Channel<Message> queue = Channel.CreateBounded<Message>(new BoundedChannelOptions(256) { SingleReader = true, AllowSynchronousContinuations = false, FullMode = BoundedChannelFullMode.Wait });
    readonly object queueGate = new();
    readonly Dictionary<(ushort, long), RemoteTownActor> moves = [];
    readonly Dictionary<ushort, long> known = [];
    readonly Task pump;
    readonly Lazy<Task> cancellation;
    readonly AsyncLocal<Func<Func<bool>>?> queuedPublication = new();
    ushort sequence;
    long epoch;
    long pendingBytes;
    bool closed;
    public GameConnectionSender(AccountOwners.Lease owner, Stream stream, CancellationTokenSource connection, ushort sequence, Action<byte, ushort, ushort, ReadOnlyMemory<byte>, string> sent, Action<byte[]>? capture = null, Action<string, Exception?>? failed = null)
    {
        this.owner = owner;
        this.stream = stream;
        this.connection = connection;
        this.sequence = sequence;
        this.sent = sent;
        this.capture = capture;
        this.failed = failed;
        cancellation = new(CancelConnection);
        pump = Run();
    }

    public long Epoch => Volatile.Read(ref epoch);
    internal bool IsPublicationQueued => queuedPublication.Value is not null;
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
        if (queuedPublication.Value is { } current) return QueuePublished(kind, opcode, body, action, canSend, current());
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!Enqueue(new(Epoch, new(kind, opcode, body, action, done, canSend))))
            done.TrySetException(new IOException("Game send queue unavailable"));
        return done.Task;
    }

    public Task SendProtected(byte kind, ushort opcode, byte[] body, string action, Func<bool>? canSend = null)
    {
        if (queuedPublication.Value is { } current) return QueuePublished(kind, opcode, body, action, canSend, current(), true);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!Enqueue(new(Epoch, new(kind, opcode, body, action, done, canSend, Protected: true))))
            done.TrySetException(new IOException("Game send queue unavailable"));
        return done.Task;
    }

    // 自然区域共同入场必须区分真实写帧与守卫失效后的静默跳过。
    internal async Task<bool> SendConfirmedAsync(byte kind, ushort opcode, byte[] body, string action, Func<bool> canSend)
    {
        bool written = false;
        var outer = queuedPublication.Value?.Invoke();
        bool Current() => (outer?.Invoke() ?? true) && canSend();
        if (!owner.Execute(Current)) return false;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!Enqueue(new(Epoch, new(kind, opcode, body, action, done, Current, Written: () => written = true))))
            done.TrySetException(new IOException("Game send queue unavailable"));
        await done.Task;
        return written;
    }

    // 原生只读快照排队后仍须复核目录及角色；不改变已提交奖励的原发布语义。
    internal Task SendProtectedRevalidated(byte kind, ushort opcode, byte[] body, string action, Func<bool> canSend)
    {
        if (queuedPublication.Value is not { } captureCurrent) return SendProtected(kind, opcode, body, action, canSend);
        var outer = captureCurrent();
        bool Current() => outer() && canSend();
        if (!owner.Execute(Current)) return Task.CompletedTask;
        return Enqueue(new(Epoch, new(kind, opcode, body, action, CanSend: Current, Protected: true)))
            ? Task.CompletedTask : Task.FromException(new IOException("Game send queue unavailable"));
    }

    // Written仅用于成功写帧后的本人owner内展示记账，不得执行网络等待或SQL。
    public void Post(byte kind, ushort opcode, byte[] body, string action, Func<bool>? canSend = null, Action? written = null) =>
        TryPost(kind, opcode, body, action, canSend, written);

    // 返回的是有界队列接收状态，不表示网络已经写出或客户端已经显示。
    // afterWritten在写成功且退出本人owner后同步调用；不得等待网络/SQL，可发布已提交领域变化。
    internal bool TryPost(byte kind, ushort opcode, byte[] body, string action, Func<bool>? canSend = null, Action? written = null, Action? afterWritten = null) =>
        Enqueue(new(Epoch, new(kind, opcode, body, action, CanSend: canSend, Written: written, AfterWritten: afterWritten)));

    // 默认固定长度；变长快照可显式按上限预算。泵内不得执行SQL或网络等待，null代表原身份失效。
    internal void PostPrepared(byte kind, ushort opcode, int bodyLength, Func<byte[]?> prepare, string action, Func<bool> canSend,
        bool allowShorter = false, Action? written = null)
    {
        if (bodyLength is < 0 or > 65520) throw new ArgumentOutOfRangeException(nameof(bodyLength));
        Enqueue(new(Epoch, new(kind, opcode, new byte[bodyLength], action, CanSend: canSend, Prepare: prepare, AllowShorter: allowShorter, Written: written)));
    }

    // 共享副本先完整规划并提交；发布只入原有有界发送队列，不持队伍/连接门禁等待慢客户端。
    // 原回执守卫在入队时复核；泵复核原会话，后续合法revision不能吞掉已提交的奖励帧。
    public IDisposable QueuePublication(Func<bool> current) => QueuePublication(() => current);

    public IDisposable QueuePublication(Func<Func<bool>> captureCurrent)
    {
        var previous = queuedPublication.Value;
        queuedPublication.Value = captureCurrent;
        return new PublicationScope(() => queuedPublication.Value = previous);
    }

    sealed class PublicationScope(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }

    Task QueuePublished(byte kind, ushort opcode, byte[] body, string action, Func<bool>? canSend, Func<bool> current, bool protectedFrame = false)
    {
        if (!owner.Execute(() => current() && (canSend?.Invoke() ?? true))) return Task.CompletedTask;
        // 本人已提交消息必须在FIFO中的后续清场前写出；不能因同一命令推进场景就丢掉传送效果或奖励。
        return Enqueue(new(Epoch, new(kind, opcode, body, action,
            CanSend: current, Protected: protectedFrame)))
            ? Task.CompletedTask : Task.FromException(new IOException("Game send queue unavailable"));
    }

    public Task ClearScene(SceneActorClear actors = SceneActorClear.Delete)
    {
        long next = Interlocked.Increment(ref epoch);
        var done = queuedPublication.Value is null ? new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) : null;
        if (!Enqueue(new(next, Barrier: done, Actors: actors, IsClear: true)))
        {
            var failure = new IOException("Game scene clear queue unavailable");
            if (done is null) return Task.FromException(failure);
            done.TrySetException(failure);
        }
        return done?.Task ?? Task.CompletedTask;
    }

    // 退出应答必须实际写出；调用方释放共享门禁后等待，不额外发送网络帧或清场。
    public Task DrainAsync()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!Enqueue(new(Epoch, Barrier: done)))
            done.TrySetException(new IOException("Game send queue unavailable"));
        return done.Task;
    }

    public void InvalidateScene() => Interlocked.Increment(ref epoch);
    public void Enter(RemoteTownActor actor) => Enqueue(new(Epoch, Enter: actor));
    public void Refresh(RemoteTownActor actor) => Enqueue(new(Epoch, Refresh: actor));
    public void Leave(ushort id, long membership, byte[]? departureEffect = null, Func<bool>? current = null,
        byte[]? prepareEffect = null, byte[]? destinationArea = null, bool hideActor = false) =>
        Enqueue(new(Epoch, Leave: (id, membership, departureEffect, current, prepareEffect, destinationArea, hideActor)));
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
        if (!accepted) { ReportFailure("move-queue"); Cancel(); }
    }

    bool Enqueue(Message message)
    {
        long bytes = message.Own is { } own ? own.Body.Length + 16L :
            message.Enter is { } enter ? ActorBytes(enter) :
            message.Refresh is { } refresh ? ActorBytes(refresh) :
            message.Leave is { } left ? 16L + (left.HideActor ? 7 : left.DestinationArea?.Length ?? 2) + (left.Effect is { } effect ? 16L + effect.Length : 0) +
                (left.Prepare is { } prepare ? 16L + prepare.Length : 0) : message.Actors == SceneActorClear.Hide ? 23 : 18;
        bool accepted;
        lock (queueGate)
            accepted = TryEnqueue(message with { Bytes = bytes }, bytes);
        // 标记取消并异步执行回调，不能从源账号/成员锁内重入另一账号。
        if (!accepted)
        {
            ReportFailure("queue", new IOException($"Game send queue rejected action={message.Own?.Action ?? "scene"} pending={queue.Reader.Count} bytes={PendingBytes}"));
            Cancel();
        }
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

    static long ActorBytes(RemoteTownActor actor) => (long)actor.Actor.Length + actor.State.Length + actor.Position.Length + 3 * 16 +
        (actor.Area.IsEmpty ? 0 : actor.Area.Length + 16L) + actor.EchonSets.Sum(body => body.Length + 16L);

    void ReportFailure(string stage, Exception? error = null)
    {
        if (!connection.IsCancellationRequested && Interlocked.Exchange(ref failureReported, 1) == 0)
            failed?.Invoke(stage, error);
    }

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
        await WriteEchonSets(actor, expected);
        known[actor.Id] = actor.Membership;
        return true;
    }

    async Task WriteEchonSets(RemoteTownActor actor, long expected)
    {
        foreach (var body in actor.EchonSets)
            if (Visible(actor, expected))
                await Write(0, (ushort)NotiPacketType.CUSTOM_ABILITY_PART_SET_OPTION, body, "town_peer_echon_set");
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
                        var body = owner.Execute(() => (own.CanSend?.Invoke() ?? true)
                            ? own.Prepare is { } prepare ? prepare() : own.Body : null);
                        if (body is not null)
                        {
                            if (body.Length > own.Body.Length || !own.AllowShorter && body.Length != own.Body.Length)
                                throw new InvalidDataException("发送时包体长度违反预留预算合同");
                            await Write(own.Kind, own.Opcode, body, own.Action, own.Protected);
                            if (own.Written is { } written) owner.Execute(() => { written(); return true; });
                            own.AfterWritten?.Invoke();
                        }
                    }
                    else if (message.IsClear)
                    {
                        // 普通切区由本人AREA_USERS重置地图；USER_LEAVE还会清掉客户端队伍槽，不能用于切区。
                        if (message.Actors != SceneActorClear.Preserve)
                            foreach (ushort id in known.Keys.ToArray())
                            {
                                if (message.Actors == SceneActorClear.Hide)
                                    await Write(0, (ushort)NotiPacketType.USER_STATE, TownPackets.HideTownEntity(id), "town_peer_phase_clear");
                                else
                                    await Write(0, (ushort)NotiPacketType.USER_LEAVE, TownPackets.RemoveActor(id), "town_peer_clear");
                            }
                        known.Clear();
                    }
                    else if (message.Enter is { } enter)
                    {
                        if (await EnsureActor(enter, message.Epoch) && Visible(enter, message.Epoch))
                        {
                            await Write(0, (ushort)NotiPacketType.USER_STATE, enter.State, "town_peer_state");
                            // 资料包只建立缓存；23的远端分支才为同区域角色创建地图实体。
                            if (!enter.Area.IsEmpty && Visible(enter, message.Epoch))
                                await Write(0, (ushort)NotiPacketType.USER_AREA, enter.Area, "town_peer_area");
                            if (Visible(enter, message.Epoch))
                                await Write(0, (ushort)NotiPacketType.USER_POSITION, enter.Position, "town_peer_position");
                        }
                    }
                    else if (message.Refresh is { } refreshed)
                    {
                        bool alreadyKnown = known.TryGetValue(refreshed.Id, out long membership) && membership == refreshed.Membership;
                        if (await EnsureActor(refreshed, message.Epoch) && alreadyKnown && Visible(refreshed, message.Epoch))
                        {
                            await Write(0, (ushort)NotiPacketType.USERINFO, refreshed.Actor, "town_peer_equipment");
                            await WriteEchonSets(refreshed, message.Epoch);
                        }
                    }
                    else if (message.Move is not null)
                    {
                        if (latest is not null && await EnsureActor(latest, message.Epoch) && Visible(latest, message.Epoch))
                            await Write(0, (ushort)NotiPacketType.USER_POSITION, latest.Position, "town_peer_position");
                    }
                    else if (message.Leave is { } left && Epoch == message.Epoch && known.GetValueOrDefault(left.Id) == left.Membership)
                    {
                        // 出发事件与旧成员离场同队列：源已离场不抹掉已提交事件，未知Actor、旧目标视图或旧源owner仍跳过。
                        if (left.Prepare is { } prepare && owner.Execute(() => Epoch == message.Epoch && left.Current?.Invoke() != false))
                            await Write(0, (ushort)NotiPacketType.SHOW_EFFECT, prepare, "teleport_party_prepare_peer");
                        if (left.Effect is { } effect && owner.Execute(() => Epoch == message.Epoch && left.Current?.Invoke() != false))
                            await Write(0, (ushort)NotiPacketType.SHOW_EFFECT, effect, "teleport_aura_departure_peer");
                        if (Epoch == message.Epoch)
                        {
                            if (left.HideActor)
                                await Write(0, (ushort)NotiPacketType.USER_STATE, TownPackets.HideTownEntity(left.Id), "town_peer_phase_leave");
                            else if (left.DestinationArea is { } area)
                                await Write(0, (ushort)NotiPacketType.USER_AREA, area, "town_peer_depart_area");
                            else
                                await Write(0, (ushort)NotiPacketType.USER_LEAVE, TownPackets.RemoveActor(left.Id), "town_peer_leave");
                            known.Remove(left.Id);
                        }
                    }
                }
                finally
                {
                    lock (queueGate)
                        pendingBytes -= activeBytes;
                }

                message.Own?.Done?.TrySetResult();
                message.Barrier?.TrySetResult();
                active = null;
            }
        }
        catch (Exception ex)
        {
            // 泵的任何失败都结束该连接并释放等待者；不能留下无人消费的可靠发送任务。
            ReportFailure("pump", ex);
            failure = ex is IOException or OperationCanceledException or InvalidOperationException ? ex : new IOException("Game sender failed", ex);
            Cancel();
        }
        finally
        {
            failure ??= new IOException("Game sender closed");
            active?.Own?.Done?.TrySetException(failure);
            active?.Barrier?.TrySetException(failure);
            lock (queueGate)
            {
                closed = true;
                queue.Writer.TryComplete();
            }
            while (queue.Reader.TryRead(out var message))
            {
                message.Own?.Done?.TrySetException(failure);
                message.Barrier?.TrySetException(failure);
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
