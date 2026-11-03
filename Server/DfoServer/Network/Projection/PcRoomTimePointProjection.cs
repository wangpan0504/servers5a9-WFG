using System.Threading.Channels;
using DfoServer.Game.Events;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

// 连接只拥有唤醒和发送任务，资格、计时与领取水位由Session和账号仓储持有。
internal sealed class PcRoomTimePointProjection : IAsyncDisposable
{
    readonly GameConnectionContext context;
    readonly PcRoomTimePointService service;
    readonly TownProjection town;
    readonly ServerLog log;
    readonly TimeProvider time;
    readonly Func<Exception, bool>? isBusy;
    readonly CancellationTokenSource cancellation;
    readonly Channel<byte> wake = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
        { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
    readonly Task pending;
    PcRoomTimePointUse? current;
    bool? eventPublished;
    internal void EventsPublished(bool enabled) => eventPublished = enabled;

    public PcRoomTimePointProjection(GameConnectionContext context, PcRoomTimePointService service,
        TownProjection town, ServerLog log, TimeProvider? time = null, Func<Exception, bool>? isBusy = null)
    {
        this.context = context; this.service = service; this.town = town; this.log = log;
        this.time = time ?? TimeProvider.System;
        this.isBusy = isBusy;
        cancellation = CancellationTokenSource.CreateLinkedTokenSource(context.Stop);
        service.Events.Changed += Changed;
        pending = RunAsync(cancellation.Token);
    }

    void Changed(ServerEventChange change)
    {
        if (change.State.Schedule.EventId == ServerEventService.PcRoomTimePointId) wake.Writer.TryWrite(0);
    }

    public async Task RefreshAsync()
    {
        // 普通移动等请求不新增SQL检查点；当前选角由到点任务和GM信号推进。
        if (current is not null && context.Owner.Execute(() =>
            context.Session.Ready && context.Session.SelectionEpoch == current.Epoch)) return;
        var use = context.Session.RefreshPcRoomTimePoint(service);
        bool selectionChanged = current?.Epoch != use?.Epoch;
        await PublishAsync(use, force: false);
        if (selectionChanged) wake.Writer.TryWrite(0);
    }

    public async Task UseAsync(byte type, byte index) =>
        await PublishAsync(context.Session.UsePcRoomTimePoint(service, type, index), force: true);

    async Task PublishAsync(PcRoomTimePointUse? use, bool force)
    {
        var before = current;
        current = use;
        if (use is null) return;
        bool Guard() => context.Session.CanPublishPcRoomTimePoint(use);
        if (eventPublished != use.Enabled)
            await town.ServerEventsAsync(context, Guard);
        if (force || use.Enabled && (use.Changed || before?.Epoch != use.Epoch) || before?.Enabled == true && !use.Enabled)
        {
            var snapshot = use.Snapshot;
            await context.Notify(NotiPacketType.PCROOM_TIME_POINT,
                PcRoomTimePointPackets.Progress(snapshot.Seconds, snapshot.QualifiedDays,
                    snapshot.DailyClaims, 0, snapshot.PeriodClaims), "pcroom_timepoint_progress", Guard);
        }
        if (use.Claimed && !use.Replayed)
        {
            await context.Notify(NotiPacketType.MAILBOX_ALARM, [1, 0], "pcroom_timepoint_mail", Guard);
            log.Write("pcroom_timepoint_claimed", new { Connection = context.Id, Character = use.CharacterId, use.MailId });
        }
    }

    async Task RunAsync(CancellationToken stop)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var use = current;
                // 关闭期间只等GM开关、选角或日界；无需每30秒写一次离线活动。
                var next = use is null ? DateTimeOffset.MaxValue : use.Enabled ? use.NextWake : PcRoomTimePointDay.NextBoundary(use.At);
                await WaitAsync(next, use?.At ?? time.GetUtcNow(), stop);
                try
                {
                    await context.CommandGate.WaitAsync(stop);
                    try
                    {
                        stop.ThrowIfCancellationRequested();
                        await PublishAsync(context.Session.RefreshPcRoomTimePoint(service), force: false);
                    }
                    finally { context.CommandGate.Release(); }
                }
                catch (Exception error) when (isBusy?.Invoke(error) == true)
                {
                    log.Write("pcroom_timepoint_busy", new { Connection = context.Id, error.Message });
                    await Task.Delay(TimeSpan.FromSeconds(1), time, stop);
                    wake.Writer.TryWrite(0);
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            log.Write("pcroom_timepoint_failed", new { Connection = context.Id, Error = error.Message });
            context.Disconnect();
        }
    }

    async Task WaitAsync(DateTimeOffset next, DateTimeOffset at, CancellationToken stop)
    {
        using var iteration = CancellationTokenSource.CreateLinkedTokenSource(stop);
        var signal = wake.Reader.ReadAsync(iteration.Token).AsTask();
        var delay = next == DateTimeOffset.MaxValue ? Timeout.InfiniteTimeSpan : next - at;
        if (delay < TimeSpan.Zero && delay != Timeout.InfiniteTimeSpan) delay = TimeSpan.Zero;
        var timer = Task.Delay(delay, time, iteration.Token);
        await Task.WhenAny(signal, timer);
        await iteration.CancelAsync();
        try { await signal; } catch (OperationCanceledException) when (iteration.IsCancellationRequested) { }
        try { await timer; } catch (OperationCanceledException) when (iteration.IsCancellationRequested) { }
        stop.ThrowIfCancellationRequested();
    }

    public async ValueTask DisposeAsync()
    {
        service.Events.Changed -= Changed;
        await cancellation.CancelAsync();
        await pending;
        wake.Writer.TryComplete();
        cancellation.Dispose();
    }
}
