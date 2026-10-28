using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

// 每连接一个到期任务，不依赖玩家继续发包；回城和停服均取消旧实例任务。
internal sealed class TagEvildomDeadlineProjection(GameConnectionContext context, TownProjection town,
    TagEvildomRunProjection run, ServerLog log, Func<Exception, bool> isBusy, TimeProvider? time = null) : IAsyncDisposable
{
    readonly TimeProvider clock = time ?? TimeProvider.System;
    TagEvildomScheduledReturn? scheduled;
    CancellationTokenSource? cancellation;
    Task pending = Task.CompletedTask;

    public async Task CheckAsync()
    {
        var expected = context.Session.CurrentTagEvildomReturn();
        if (expected is null || clock.GetUtcNow().ToUnixTimeSeconds() < expected.At) return;
        if (context.Session.ReturnExpiredTagEvildom(expected) is not { } receipt) return;
        bool Guard() => context.Session.CanPublishTravel(receipt);
        await context.Notify(NotiPacketType.AAS_TIPS, ServerNoticePackets.Build("再战魔界", "每日维护时间已到，返回中央竞技场。"),
            "tag_evildom_daily_return", Guard);
        await town.ReturnFromFormalDungeonAsync(context, receipt, "tag_evildom_daily_return");
        await run.ReturnAsync(context, town, receipt);
        log.Write("tag_evildom_daily_return", new { Connection = context.Id, expected.Instance, expected.At });
    }

    public async Task RefreshAsync()
    {
        var wanted = context.Session.CurrentTagEvildomReturn();
        if (wanted == scheduled && !pending.IsCompleted) return;
        await StopAsync();
        if (wanted is null) return;
        scheduled = wanted;
        cancellation = CancellationTokenSource.CreateLinkedTokenSource(context.Stop);
        pending = RunAsync(wanted, cancellation.Token);
    }

    async Task RunAsync(TagEvildomScheduledReturn expected, CancellationToken stop)
    {
        try
        {
            await TagEvildomRevivalProjection.WaitUntilAsync(checked(expected.At * 1000), clock, stop);
            while (!stop.IsCancellationRequested)
            {
                await context.CommandGate.WaitAsync(stop);
                try
                {
                    if (context.Session.CurrentTagEvildomReturn() != expected) return;
                    await CheckAsync();
                    if (context.Session.CurrentTagEvildomReturn() != expected) return;
                }
                catch (Exception error) when (isBusy(error))
                {
                    // 忙锁表示本次未提交，释放连接门禁后重试，不能断线把正常退场变成跨日未完成。
                    log.Write("tag_evildom_daily_return_busy", new { Connection = context.Id, expected.Instance });
                }
                finally { context.CommandGate.Release(); }
                // 尚有待提交终点时不丢弃其冻结奖励；待原事务完成后再回城。
                await Task.Delay(TimeSpan.FromSeconds(1), clock, stop);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested || context.Stop.IsCancellationRequested) { }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            log.Write("tag_evildom_daily_return_failed", new { Connection = context.Id, expected.Instance, Error = error.Message });
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
        scheduled = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
