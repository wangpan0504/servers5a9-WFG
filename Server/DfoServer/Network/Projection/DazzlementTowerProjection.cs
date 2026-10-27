using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

// 每连接只保存最后公布的周期；资格仍从原持久准入账本读取。
internal sealed class DazzlementTowerProjection(GameConnectionContext context, ServerLog log, TimeProvider? time = null) : IAsyncDisposable
{
    readonly TimeProvider clock = time ?? TimeProvider.System;
    (long Epoch, DazzlementTowerAdmission Period, bool Bound)? announced;
    CancellationTokenSource? cancellation;
    Task pending = Task.CompletedTask;
    long timerEpoch = -1;
    public bool HasBindings => announced is { Bound: true };

    public static Task BindingsAsync(GameConnectionContext context, Func<bool> guard) => StateAsync(context, guard, true);

    public static async Task StateAsync(GameConnectionContext context, Func<bool>? guard = null, bool onlyForSelection = false)
    {
        if (context.Session.CaptureDazzlementTower(onlyForSelection) is not { } notice) return;
        bool CanSend() => (guard?.Invoke() ?? true) && context.Session.CanPublishDazzlementTower(notice);
        await context.Notify(NotiPacketType.TOWER_OF_DAZZLEMENT_ENTER_CHARAC_LIST, DazzlementTowerPackets.Bindings(notice.Binding),
            "dazzlement_tower_bindings", CanSend);
        await context.Notify(NotiPacketType.TOWER_OF_DAZZLEMENT_INFO, DazzlementTowerPackets.State(notice.UsedToday),
            "dazzlement_tower_state", CanSend);
        if (CanSend() && context.Dazzlement is { } projection)
            projection.announced = (notice.Epoch, notice.Binding.Period, !notice.Binding.Bindings.IsEmpty);
    }

    public async Task RefreshAsync()
    {
        var capture = context.Owner.Execute(() => (context.Session.Ready, Epoch: context.Session.SelectionEpoch));
        if (!capture.Ready)
        {
            await StopAsync();
            announced = null;
            return;
        }
        var period = DazzlementTowerAdmission.At(clock.GetUtcNow());
        // 回拨不把已公布的新服务日还原成可入场；热路径相同周期不重复查库。
        if (announced is not { } previous || previous.Epoch != capture.Epoch || previous.Bound && previous.Period.Day < period.Day)
            await StateAsync(context);
        if (announced is not { Bound: true } current || current.Epoch != capture.Epoch)
        {
            await StopAsync();
            return;
        }
        if (timerEpoch == capture.Epoch && !pending.IsCompleted) return;
        await StopAsync();
        timerEpoch = capture.Epoch;
        cancellation = CancellationTokenSource.CreateLinkedTokenSource(context.Stop);
        pending = RunAsync(capture.Epoch, cancellation.Token);
    }

    async Task RunAsync(long epoch, CancellationToken stop)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var now = clock.GetUtcNow();
                await Task.Delay(DailyBoosterUsage.NextReset(now) - now, clock, stop);
                await context.CommandGate.WaitAsync(stop);
                try
                {
                    stop.ThrowIfCancellationRequested();
                    if (!context.Owner.Execute(() => context.Session.Ready && context.Session.SelectionEpoch == epoch)) return;
                    await StateAsync(context);
                    if (announced is not { Bound: true }) return;
                }
                finally { context.CommandGate.Release(); }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested || context.Stop.IsCancellationRequested) { }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            log.Write("dazzlement_tower_projection_failed", new { Connection = context.Id, Epoch = epoch, Error = error.Message });
            context.Disconnect();
        }
    }

    async Task StopAsync()
    {
        if (cancellation is null) return;
        // 持CommandGate收尾时先取消等待，旧epoch不能发布或解除新计时器。
        await cancellation.CancelAsync();
        await pending;
        cancellation.Dispose();
        cancellation = null;
        timerEpoch = -1;
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
