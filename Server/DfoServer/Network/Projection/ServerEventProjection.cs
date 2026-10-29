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
    long epoch = -1;
    bool active;

    public ServerEventProjection(GameConnectionContext context, ServerEventService events, TownProjection town, ServerLog log, TimeProvider time)
    {
        this.context = context; this.events = events; this.town = town; this.log = log; this.time = time;
        stop = CancellationTokenSource.CreateLinkedTokenSource(context.Stop);
        events.Changed += Changed;
        worker = RunAsync();
    }
    void Changed(ushort id) { if (id == ServerEventService.MileageShopId) pending.Writer.TryWrite(true); }
    public void Published(bool value)
    {
        epoch = context.Session.SelectionEpoch;
        active = value;
        pending.Writer.TryWrite(true);
    }
    async Task RunAsync()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var state = events.Read(ServerEventService.MileageShopId);
                var schedule = state.Schedule;
                if (epoch >= 0 && context.Session.Ready && active != (state.Active && town.MileageAvailable)) pending.Writer.TryWrite(true);
                var now = time.GetUtcNow();
                long? next = schedule.OverrideActive is not null ? null :
                    new[] { schedule.StartsAt, schedule.EndsAt }.Where(value => value > now.ToUnixTimeSeconds()).Min();
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
                    if (!selection.Ready || selection.Selected is null || epoch == selection.SelectionEpoch && current == active) continue;
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
            log.Write("connection_error", new { Connection = context.Id, Stage = "mileage_shop_event", Error = error.Message });
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
