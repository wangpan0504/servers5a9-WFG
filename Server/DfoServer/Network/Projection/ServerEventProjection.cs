using System.Threading.Channels;
using DfoServer.Game.Events;
using DfoServer.Infrastructure;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

// 每连接只保留最新唤醒；活动资格仍由原活动服务和资产事务校验。
internal sealed class ServerEventProjection : IAsyncDisposable
{
    readonly GameConnectionContext context;
    readonly ServerEventService events;
    readonly TownProjection town;
    readonly ServerLog log;
    readonly TimeProvider time;
    readonly CancellationTokenSource stop;
    readonly Channel<bool> pending = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite });
    readonly Task worker;
    long queuedEpoch = -1;
    long queuedStrongwayRevision = -1;
    bool queuedMileage;
    bool queuedStrongway;
    bool queuedNolan;

    public ServerEventProjection(GameConnectionContext context, ServerEventService events, TownProjection town, ServerLog log, TimeProvider time)
    {
        this.context = context; this.events = events; this.town = town; this.log = log; this.time = time;
        stop = CancellationTokenSource.CreateLinkedTokenSource(context.Stop);
        events.Changed += Changed;
        worker = RunAsync();
    }
    void Changed(ushort id)
    {
        if (id is ServerEventService.MileageShopId or ServerEventService.Strongway2019Id or ServerEventService.NolanVisibilityId)
            pending.Writer.TryWrite(true);
    }
    internal bool NeedsStrongwayDisplay(bool value) => queuedEpoch != context.Session.SelectionEpoch ||
        queuedStrongway != value || queuedStrongwayRevision != events.Strongway2019DisplayRevision;
    // 这里只去重已接受的快照，不表示客户端已显示；版本变化仍须重发被发送守卫丢弃的旧包。
    public void Queued(bool value, bool strongway, long selectionEpoch, long revision, bool nolanVisible = true)
    {
        queuedEpoch = selectionEpoch;
        queuedMileage = value;
        queuedStrongway = strongway;
        queuedStrongwayRevision = revision;
        queuedNolan = nolanVisible;
        pending.Writer.TryWrite(true);
    }
    async Task RunAsync()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var state = events.Read(ServerEventService.MileageShopId);
                bool nolanVisible = events.Read(ServerEventService.NolanVisibilityId).Active;
                var strongway = events.Read(ServerEventService.Strongway2019Id);
                if (queuedEpoch >= 0 && context.Session.Ready &&
                    (queuedMileage != (state.Active && town.MileageAvailable) ||
                     queuedNolan != nolanVisible || NeedsStrongwayDisplay(strongway.Active))) pending.Writer.TryWrite(true);
                var now = time.GetUtcNow();
                long? next = new[] { state.Schedule, strongway.Schedule }.Where(schedule => schedule.OverrideActive is null)
                    .SelectMany(schedule => new[] { schedule.StartsAt, schedule.EndsAt })
                    .Where(value => value > now.ToUnixTimeSeconds()).Min();
                using (var wait = CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
                {
                    var request = pending.Reader.ReadAsync(wait.Token).AsTask();
                    var delay = next is { } point ? DateTimeOffset.FromUnixTimeSeconds(point) - now : Timeout.InfiniteTimeSpan;
                    if (delay > TimeSpan.FromDays(30)) delay = TimeSpan.FromDays(30);
                    var timer = Task.Delay(delay, time, wait.Token);
                    await Task.WhenAny(request, timer);
                    await wait.CancelAsync();
                    try { await request; } catch (OperationCanceledException) when (wait.IsCancellationRequested) { }
                    try { await timer; } catch (OperationCanceledException) when (wait.IsCancellationRequested) { }
                }
                await context.CommandGate.WaitAsync(stop.Token);
                try
                {
                    var selection = context.Owner.Execute(() => (context.Session.Ready, context.Session.Selected, context.Session.SelectionEpoch));
                    bool current = events.Read(ServerEventService.MileageShopId).Active && town.MileageAvailable;
                    bool currentNolan = events.Read(ServerEventService.NolanVisibilityId).Active;
                    bool strongwayCurrent = events.Read(ServerEventService.Strongway2019Id).Active;
                    if (!selection.Ready || selection.Selected is null || queuedEpoch == selection.SelectionEpoch &&
                        current == queuedMileage && currentNolan == queuedNolan && !NeedsStrongwayDisplay(strongwayCurrent)) continue;
                    bool CanSend() => context.Owner.Execute(() => context.Session.Ready && context.Session.SelectionEpoch == selection.SelectionEpoch);
                    await town.ServerEventsAsync(context, CanSend);
                }
                finally { context.CommandGate.Release(); }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested || context.Stop.IsCancellationRequested) { }
        catch (ChannelClosedException) when (stop.IsCancellationRequested) { }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            log.Write("connection_error", new { Connection = context.Id, Stage = "server_event_display", Error = error.Message });
            context.Disconnect();
        }
    }
    public async ValueTask DisposeAsync()
    {
        events.Changed -= Changed;
        await stop.CancelAsync();
        pending.Writer.TryComplete();
        await worker;
        stop.Dispose();
    }
}
