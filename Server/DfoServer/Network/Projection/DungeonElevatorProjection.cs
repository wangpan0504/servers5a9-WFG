using DfoServer.Game.Dungeons;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

// 每个连接最多一个电梯计时任务；断线先取消并等待退出，再释放会话与账号owner。
internal sealed class DungeonElevatorProjection(GameConnectionContext context, ServerLog log) : IAsyncDisposable
{
    CancellationTokenSource? cancellation;
    Task pending = Task.CompletedTask;

    public async Task StartAsync(FormalDungeonReceipt loaded)
    {
        if (context.Session.StartDungeonElevator(loaded) is not { } scheduled) return;
        await StopAsync();
        cancellation = CancellationTokenSource.CreateLinkedTokenSource(context.Stop);
        pending = RunAsync(scheduled, cancellation.Token);
        log.Write("formal_elevator_started", new { Connection = context.Id, scheduled.InstanceId, scheduled.Map.Id });
    }

    async Task RunAsync(DungeonElevatorReceipt scheduled, CancellationToken stop)
    {
        try
        {
            for (int tick = 0; tick < 4; tick++)
            {
                await Task.Delay(TimeSpan.FromSeconds(15), stop);
                if (context.Session.TickDungeonElevator(scheduled) is not { } receipt) return;
                await context.Notify(NotiPacketType.ELEVATOR_CLEAR_TIME_CHECK, DungeonPackets.Elevator(receipt.Notice!),
                    "formal_elevator_tick", () => context.Session.CanPublishDungeonElevator(receipt));
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested || context.Stop.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or InvalidOperationException)
        {
            log.Write("formal_elevator_failed", new { Connection = context.Id, scheduled.InstanceId, Error = error.Message });
        }
    }

    async Task StopAsync()
    {
        if (cancellation is null) return;
        await cancellation.CancelAsync();
        await pending;
        cancellation.Dispose();
        cancellation = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
