using DfoServer.Game.Dungeons;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

// 每个共享魔兽实例仅租用一个等待任务；退出转移租约，继续原deadline。
internal sealed class BeastDeadlineProjection(GameConnectionContext context, ServerLog log,
    Func<Exception, bool> isBusy, TimeProvider? time = null) : IAsyncDisposable
{
    BeastRunReceipt? scheduled;
    BeastDeadlineLease? lease;
    CancellationTokenSource? cancellation;
    Task pending = Task.CompletedTask;
    (Guid Instance, int Stage, int Completed, BeastTerminal Terminal)? published;
    internal Func<Task>? ResumePendingDeath { get; set; }

    internal async Task RefreshAsync()
    {
        var receipt = context.Session.CaptureBeastRun();
        if (receipt is not null)
        {
            var view = receipt.Progress.View();
            var identity = (view.InstanceId, view.Stage, view.CompletedStages, view.Terminal);
            if (published != identity && context.Session.CanPublishBeastRun(receipt))
            {
                log.Write("formal_beast_progress", new { Connection = context.Id, View = view });
                published = identity;
            }
        }
        if (receipt is not null && ReferenceEquals(scheduled?.Progress, receipt.Progress)) return;
        await StopAsync();
        if (receipt is null || context.Session.Selected is not { } character ||
            receipt.Progress.TrySchedule(character.Id) is not { } acquired) return;
        scheduled = receipt;
        lease = acquired;
        cancellation = CancellationTokenSource.CreateLinkedTokenSource(context.Stop, acquired.Stop);
        pending = RunAsync(receipt, acquired, cancellation.Token);
    }

    async Task RunAsync(BeastRunReceipt receipt, BeastDeadlineLease acquired, CancellationToken stop)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                int remaining = receipt.Progress.View().RemainingMilliseconds;
                if (remaining > 0) await Task.Delay(TimeSpan.FromMilliseconds(remaining), time ?? TimeProvider.System, stop);
                using (await DispatchGateScope.EnterAsync(context, stop))
                {
                    if (!context.Session.CanPublishBeastRun(receipt)) return;
                    try
                    {
                        if (context.Session.PartyDungeon is { } party)
                        {
                            using var peers = await PartyDungeonProjection.AcquirePeersAsync(context, party.Members, stop);
                            await PartyDungeonProjection.PublishAsync(party.TickBeastDeadline(context.Session));
                        }
                        else
                        {
                            if (ResumePendingDeath is not null) await ResumePendingDeath();
                            context.Session.ExpireBeastRun(receipt);
                            await RefreshAsync();
                        }
                    }
                    catch (Exception error) when (isBusy(error))
                    {
                        log.Write("formal_beast_deadline_busy", new { Connection = context.Id, receipt.Progress.View().InstanceId });
                    }
                }
                if (receipt.Progress.View().Terminal != BeastTerminal.Active) return;
                // 已接受的原事务尚未完成时保留候选，门禁外短暂等待后恢复，不制造新死亡。
                await Task.Delay(TimeSpan.FromSeconds(1), time ?? TimeProvider.System, stop);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            log.Write("formal_beast_deadline_failed", new { Connection = context.Id, Error = error.Message });
            context.Disconnect();
        }
        finally { receipt.Progress.Release(acquired); }
    }

    internal async Task StopAsync()
    {
        if (cancellation is null) return;
        await cancellation.CancelAsync();
        await pending;
        cancellation.Dispose();
        cancellation = null;
        lease?.Dispose();
        lease = null;
        scheduled = null;
    }

    public ValueTask DisposeAsync() => new(StopAsync());
}
