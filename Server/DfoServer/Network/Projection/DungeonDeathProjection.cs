using System.Data.Common;
using DfoServer.Game.Dungeons;
using DfoServer.Infrastructure;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

// external 的死亡回城为10秒；每个连接仅保留一个任务，断线先取消并等待再释放owner。
internal sealed class DungeonDeathProjection(GameConnectionContext context, TownProjection town, ServerLog log, TimeProvider? time = null) : IAsyncDisposable
{
    public static readonly TimeSpan ReturnDelay = TimeSpan.FromSeconds(10);
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
            // 计时经注入的 TimeProvider，回归可按比例缩短等待；生产未注入时使用系统时钟。
            await Task.Delay(ReturnDelay, time ?? TimeProvider.System, stop);
            if (context.Session.ReturnFromFormalDungeonOnDeath(scheduled) is not { } receipt) return;
            if (context.LostLand is not null) await context.LostLand.StopAsync();
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
