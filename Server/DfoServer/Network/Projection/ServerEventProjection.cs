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
    bool queuedSeriaLuck = true;
    bool queuedHongbao = true;
    bool queuedJonathan = true;
    long queuedPromotionRevision;
    long battlePassEpoch = -1;
    bool battlePassActive;
    bool queuedEndless;
    bool queuedAttendance;

    public ServerEventProjection(GameConnectionContext context, ServerEventService events, TownProjection town, ServerLog log, TimeProvider time)
    {
        this.context = context; this.events = events; this.town = town; this.log = log; this.time = time;
        stop = CancellationTokenSource.CreateLinkedTokenSource(context.Stop);
        events.Changed += Changed;
        worker = RunAsync();
    }
    void Changed(DfoServer.Game.Events.ServerEventChange change)
    {
        ushort id = change.State.Schedule.EventId;
        if (id == ServerEventService.DailyAttendanceId) context.DailyAttendance?.Invalidate();
        if (id is ServerEventService.MileageShopId or ServerEventService.Strongway2019Id or ServerEventService.NolanVisibilityId or
            ServerEventService.BattlePassId or ServerEventService.SeriaLuckId or ServerEventService.GuildHongbaoId or
            ServerEventService.JonathanVisibilityId or Game.Events.EndlessAdventurePolicy.EventId or
            ServerEventService.DailyAttendanceId or ServerEventService.WeeklyAttendanceId or 10999)
            pending.Writer.TryWrite(true);
    }
    internal bool NeedsStrongwayDisplay(bool value) => queuedEpoch != context.Session.SelectionEpoch ||
        queuedStrongway != value || queuedStrongwayRevision != events.Strongway2019DisplayRevision;
    // 这里只去重已接受的快照，不表示客户端已显示；版本变化仍须重发被发送守卫丢弃的旧包。
    public void Queued(bool value, bool strongway, long selectionEpoch, long revision, bool nolanVisible = true,
        bool seriaLuck = true, bool hongbao = true, bool jonathan = true, long promotionRevision = 0,
        bool attendance = false)
    {
        queuedEpoch = selectionEpoch;
        queuedMileage = value;
        queuedStrongway = strongway;
        queuedStrongwayRevision = revision;
        queuedNolan = nolanVisible;
        queuedSeriaLuck = seriaLuck;
        queuedHongbao = hongbao;
        queuedJonathan = jonathan;
        queuedPromotionRevision = promotionRevision;
        queuedEndless = context.Session.EndlessAdventureActive;
        queuedAttendance = attendance;
        pending.Writer.TryWrite(true);
    }
    public void Refresh() => pending.Writer.TryWrite(true);
    public void PublishedBattlePass(long selectionEpoch, bool value)
    {
        if (context.Session.SelectionEpoch != selectionEpoch) return;
        battlePassEpoch = selectionEpoch;
        battlePassActive = value;
        pending.Writer.TryWrite(true);
    }

    // 事件worker与日切调用方均持本人命令门禁；开始边界只能由这一入口初始化整组页面。
    internal async Task PublishBattlePassOpeningAsync()
    {
        var selection = context.Owner.Execute(() => (context.Session.Ready, context.Session.Selected, context.Session.SelectionEpoch));
        if (!selection.Ready || selection.Selected is null || !town.IsBattlePassEventActive(time.GetUtcNow()) ||
            battlePassEpoch == selection.SelectionEpoch && battlePassActive) return;
        bool CanSend() => context.Owner.Execute(() => context.Session.Ready && context.Session.SelectionEpoch == selection.SelectionEpoch);
        await town.ServerEventsAsync(context, CanSend);
        if (CanSend() && town.BattlePass is { } battlePass)
            await battlePass.ReadyAsync(context);
    }

    async Task RunAsync()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var state = events.Read(ServerEventService.MileageShopId);
                bool nolanVisible = events.Read(ServerEventService.NolanVisibilityId).Active;
                var seriaLuck = events.Read(ServerEventService.SeriaLuckId);
                var hongbao = events.Read(ServerEventService.GuildHongbaoId);
                var jonathan = events.Read(ServerEventService.JonathanVisibilityId);
                var strongway = events.Read(ServerEventService.Strongway2019Id);
                var now = time.GetUtcNow();
                if (queuedEpoch >= 0 && context.Session.Ready &&
                    (queuedMileage != (state.Active && town.MileageAvailable) ||
                     queuedEndless != context.Session.EndlessAdventureActive ||
                     queuedNolan != nolanVisible || queuedSeriaLuck != seriaLuck.Active ||
                     queuedHongbao != hongbao.Active || queuedJonathan != jonathan.Active || queuedPromotionRevision != events.PromotionRevision ||
                     NeedsStrongwayDisplay(strongway.Active) ||
                     battlePassEpoch != context.Session.SelectionEpoch || battlePassActive != town.IsBattlePassEventActive(now))) pending.Writer.TryWrite(true);
                long? next = new[] { state.Schedule, strongway.Schedule, seriaLuck.Schedule, hongbao.Schedule,
                    events.Read(ServerEventService.BattlePassId).Schedule,
                    events.Read(Game.Events.EndlessAdventurePolicy.EventId).Schedule,
                    events.Read(ServerEventService.WeeklyAttendanceId).Schedule }.Where(schedule => schedule.OverrideActive is null)
                    .SelectMany(schedule => new[] { schedule.StartsAt, schedule.EndsAt })
                    .Where(value => value > now.ToUnixTimeSeconds()).Min();
                if (town.BattlePassMissions is { } policy)
                    foreach (long boundary in new[] { policy.Start.ToUnixTimeSeconds(), policy.End.ToUnixTimeSeconds() })
                        if (boundary > now.ToUnixTimeSeconds() && (next is null || boundary < next.Value)) next = boundary;
                using (var wait = CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
                {
                    var request = pending.Reader.ReadAsync(wait.Token).AsTask();
                    var delay = next is { } point ? DateTimeOffset.FromUnixTimeSeconds(point) - now : Timeout.InfiniteTimeSpan;
                    if (delay > TimeSpan.FromDays(30)) delay = TimeSpan.FromDays(30);
                    if (context.WeeklyAttendance is not null && (delay == Timeout.InfiniteTimeSpan || delay > TimeSpan.FromSeconds(30)))
                        delay = TimeSpan.FromSeconds(30);
                    var timer = Task.Delay(delay, time, wait.Token);
                    await Task.WhenAny(request, timer);
                    await wait.CancelAsync();
                    try { await request; } catch (OperationCanceledException) when (wait.IsCancellationRequested) { }
                    try { await timer; } catch (OperationCanceledException) when (wait.IsCancellationRequested) { }
                }
                await context.CommandGate.WaitAsync(stop.Token);
                try
                {
                    if (context.WeeklyAttendance is { } weekly) await weekly.RefreshAsync(force: true);
                    var selection = context.Owner.Execute(() => (context.Session.Ready, context.Session.Selected, context.Session.SelectionEpoch));
                    bool current = events.Read(ServerEventService.MileageShopId).Active && town.MileageAvailable;
                    bool currentBattlePass = town.IsBattlePassEventActive(time.GetUtcNow());
                    bool currentNolan = events.Read(ServerEventService.NolanVisibilityId).Active;
                    bool currentSeriaLuck = events.Read(ServerEventService.SeriaLuckId).Active;
                    bool currentHongbao = events.Read(ServerEventService.GuildHongbaoId).Active;
                    bool currentJonathan = events.Read(ServerEventService.JonathanVisibilityId).Active;
                    bool strongwayCurrent = events.Read(ServerEventService.Strongway2019Id).Active;
                    if (!selection.Ready || selection.Selected is null) continue;
                    bool currentAttendance = context.DailyAttendance?.Enabled == true;
                    bool CanSend() => context.Owner.Execute(() => context.Session.Ready && context.Session.SelectionEpoch == selection.SelectionEpoch);
                    if (queuedEpoch == selection.SelectionEpoch &&
                        current == queuedMileage && currentNolan == queuedNolan && currentSeriaLuck == queuedSeriaLuck &&
                        currentHongbao == queuedHongbao && currentJonathan == queuedJonathan && queuedPromotionRevision == events.PromotionRevision &&
                        queuedEndless == context.Session.EndlessAdventureActive && queuedAttendance == currentAttendance &&
                        !NeedsStrongwayDisplay(strongwayCurrent) &&
                        battlePassEpoch == selection.SelectionEpoch && currentBattlePass == battlePassActive)
                    {
                        // 签到进度待刷新不代表整张活动目录变化，首次入城仍保持原有包序。
                        if (context.DailyAttendance is { NeedsRefresh: true } progress && CanSend()) await progress.RefreshAsync();
                        continue;
                    }
                    bool newlyOpenedBattlePass = currentBattlePass && (battlePassEpoch != selection.SelectionEpoch || !battlePassActive);
                    if (newlyOpenedBattlePass) await PublishBattlePassOpeningAsync();
                    else await town.ServerEventsAsync(context, CanSend);
                    if (context.DailyAttendance is { } attendance && CanSend()) await attendance.RefreshAsync();
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
