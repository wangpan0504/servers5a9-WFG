using System.Data.Common;
using DfoServer.Game.Dungeons;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

// 每连接最多一个到期任务；等待在命令门外，发布、回城与本连接请求串行。
internal sealed class LostLandProjection(GameConnectionContext context, TownProjection town, ServerLog log,
    TimeProvider? time = null) : IAsyncDisposable
{
    internal static readonly TimeSpan ReturnGrace = TimeSpan.FromSeconds(30);
    CancellationTokenSource? cancellation;
    Task pending = Task.CompletedTask;
    Guid scheduledInstance;

    public async Task StartAsync(FormalDungeonReceipt loaded)
    {
        if (context.Session.StartedLostLandTimer(loaded) is not { } receipt || scheduledInstance == receipt.InstanceId) return;
        await StopAsync();
        scheduledInstance = receipt.InstanceId;
        bool Guard() => context.Session.CanPublishLostLandTimer(receipt);
        await context.Notify(NotiPacketType.DUNGEON_CLEAR_LIMIT_TIME_START,
            DungeonPackets.ClearLimitTimeStart(LostLandAdmission.InitialSeconds, receipt.OpaqueValue), "formal_lost_land_clock", Guard);
        if (!Guard()) return;
        if (context.Session.TickLostLandTimer(receipt) is not { } scheduled) return;
        log.Write("formal_lost_land_timer", new { Connection = context.Id, receipt.InstanceId, State = "started", Seconds = LostLandAdmission.InitialSeconds });
        cancellation = CancellationTokenSource.CreateLinkedTokenSource(context.Stop);
        pending = RunAsync(scheduled, cancellation.Token);
    }

    async Task RunAsync(LostLandTimerReceipt scheduled, CancellationToken stop)
    {
        try
        {
            while (true)
            {
                if (!scheduled.Expired)
                    await Task.Delay(scheduled.Remaining > TimeSpan.Zero ? scheduled.Remaining : TimeSpan.FromMilliseconds(1),
                        time ?? TimeProvider.System, stop);
                await context.CommandGate.WaitAsync(stop);
                try
                {
                    if (context.Session.TickLostLandTimer(scheduled) is not { } next) return;
                    scheduled = next;
                    if (!scheduled.Expired) continue;
                    await context.Notify(NotiPacketType.ENABLE_CLEAR_DUNGEON, [], "formal_lost_land_expired",
                        () => context.Session.CanPublishLostLandTimer(scheduled));
                    log.Write("formal_lost_land_timer", new { Connection = context.Id, scheduled.InstanceId, State = "expired" });
                    break;
                }
                finally { context.CommandGate.Release(); }
            }

            // 原生结束界面会发空42；未响应时仍在有限等待后沿原回城用例退出，不走普通翻牌。
            var returnGrace = Task.Delay(ReturnGrace, time ?? TimeProvider.System, stop);
            log.Write("formal_lost_land_timer", new { Connection = context.Id, scheduled.InstanceId,
                State = "return_grace_started", Seconds = (int)ReturnGrace.TotalSeconds });
            await returnGrace;
            await context.CommandGate.WaitAsync(stop);
            try
            {
                if (!context.Session.CanPublishLostLandTimer(scheduled)) return;
                if (context.Session.ReturnFromExpiredLostLand(scheduled) is { } returned)
                {
                    await town.ReturnFromFormalDungeonAsync(context, returned, "formal_lost_land_timeout_town");
                    log.Write("formal_dungeon_return", new { Connection = context.Id, Reason = "lost_land_timeout",
                        returned.Character.Id, returned.Character.Position });
                }
                else
                {
                    // 未决事务不得伪造成功或丢弃；关闭旧连接，交给已有持久回执恢复。
                    log.Write("formal_lost_land_timer_failed", new { Connection = context.Id, scheduled.InstanceId, Error = "Return blocked by pending operation" });
                    context.Disconnect();
                }
            }
            finally { context.CommandGate.Release(); }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested || context.Stop.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or InvalidOperationException or DbException)
        {
            log.Write("formal_lost_land_timer_failed", new { Connection = context.Id, scheduled.InstanceId, Error = error.Message });
            context.Disconnect();
        }
    }

    public async Task StopAsync()
    {
        if (cancellation is null) return;
        await cancellation.CancelAsync();
        await pending;
        cancellation.Dispose();
        cancellation = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
