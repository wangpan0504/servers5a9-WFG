using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Infrastructure;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

// 每连接最多一个周期任务；与请求共用门禁，退出时取消并等待，不持账号锁等待网络或时钟。
internal sealed class CubeContractConsumptionProjection(GameConnectionContext context, CubeContractService service,
    InventoryProjection inventory, ServerLog log, TimeProvider? time = null) : IAsyncDisposable
{
    readonly TimeProvider clock = time ?? TimeProvider.System;
    CancellationTokenSource? cancellation;
    Task pending = Task.CompletedTask;
    Guid cycle;

    CubeContractTick? Prepare() => context.Session.PrepareCubeContractTick(service, clock.GetUtcNow().ToUnixTimeSeconds(),
        clock.GetTimestamp(), clock.TimestampFrequency);

    public async Task RefreshAsync()
    {
        var wanted = Prepare();
        if (wanted is not null && cycle == wanted.CycleId && !pending.IsCompleted) return;
        await StopAsync();
        if (wanted is null) return;
        cycle = wanted.CycleId;
        cancellation = CancellationTokenSource.CreateLinkedTokenSource(context.Stop);
        pending = RunAsync(wanted.CycleId, cancellation.Token);
        log.Write("cube_contract_timer_started", new { Connection = context.Id, wanted.CycleId, wanted.InstanceId,
            wanted.CharacterId, wanted.CubeType, wanted.IntervalMilliseconds });
    }

    async Task RunAsync(Guid expectedCycle, CancellationToken stop)
    {
        try
        {
            bool retry = false;
            while (!stop.IsCancellationRequested)
            {
                var next = Prepare();
                if (next is null || next.CycleId != expectedCycle) return;
                var delay = clock.GetElapsedTime(clock.GetTimestamp(), next.DueTimestamp);
                if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
                if (retry && delay < TimeSpan.FromSeconds(1)) delay = TimeSpan.FromSeconds(1);
                await Task.Delay(delay, clock, stop);
                await context.CommandGate.WaitAsync(stop);
                try
                {
                    stop.ThrowIfCancellationRequested();
                    next = Prepare();
                    if (next is null || next.CycleId != expectedCycle) return;
                    var result = context.Session.ConsumeCubeContractTick(next, service, clock.GetUtcNow().ToUnixTimeSeconds(),
                        clock.GetTimestamp(), clock.TimestampFrequency);
                    retry = !result.Accepted;
                    if (!result.Accepted) continue;
                    await inventory.RefreshAsync(context, result, suppressMainNotice: true);
                    log.Write("cube_contract_consumed", new { Connection = context.Id, next.InstanceId, next.CharacterId,
                        next.CubeType, next.ItemId, next.Count });
                }
                finally { context.CommandGate.Release(); }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested || context.Stop.IsCancellationRequested) { }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            log.Write("cube_contract_consume_failed", new { Connection = context.Id, Cycle = expectedCycle, Error = error.Message });
            context.Disconnect();
        }
    }

    async Task StopAsync()
    {
        if (cancellation is null) return;
        await cancellation.CancelAsync();
        await pending;
        cancellation.Dispose();
        cancellation = null;
        cycle = Guid.Empty;
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
