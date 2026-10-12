using System.Data.Common;
using DfoServer.Game.Dungeons;
using DfoServer.Infrastructure;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

// external 的死亡回城为10秒；每个连接仅保留一个任务，断线先取消并等待再释放owner。
internal sealed class DungeonDeathProjection(GameConnectionContext context, TownProjection town, ServerLog log) : IAsyncDisposable
{
    CancellationTokenSource? cancellation;
    Task pending = Task.CompletedTask;

    public async Task StartAsync()
    {
        if (context.Session.CurrentDungeonDeathReturn() is not { } scheduled) return;
        await StopAsync();
        cancellation = CancellationTokenSource.CreateLinkedTokenSource(context.Stop);
        pending = RunAsync(scheduled, cancellation.Token);
    }

    async Task RunAsync(DungeonDeathReturnReceipt scheduled, CancellationToken stop)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(10), stop);
            if (context.Session.ReturnFromFormalDungeonOnDeath(scheduled) is not { } receipt) return;
            await town.ReturnFromFormalDungeonAsync(context, receipt, "formal_death_return_town");
            log.Write("formal_dungeon_return", new { Connection = context.Id, Reason = "death_timeout",
                receipt.Character.Id, receipt.Character.Position });
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested || context.Stop.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or InvalidOperationException or DbException)
        {
            log.Write("formal_death_return_failed", new { Connection = context.Id, scheduled.InstanceId, Error = error.Message });
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
