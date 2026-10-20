using DfoServer.Game.Dungeons;
using DfoServer.Infrastructure;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

// 每连接仅保留一个祝福到期任务；资格仍来自 Session，通知不扣物、不扣币或重复落库。
internal sealed class DungeonRevivalProjection(GameConnectionContext context, ServerLog log, TimeProvider? time = null) : IAsyncDisposable
{
    readonly TimeProvider clock = time ?? TimeProvider.System;
    RevivalBlessingTimeout? scheduled;
    CancellationTokenSource? cancellation;
    Task pending = Task.CompletedTask;

    public async Task RefreshAsync()
    {
        var wanted = context.Session.CurrentRevivalBlessingTimeout();
        // 已发送的同一截止也不重发；续药、退出实例和换角才替换原任务。
        if (wanted == scheduled) return;
        await StopAsync();
        if (wanted is null) return;
        scheduled = wanted;
        cancellation = CancellationTokenSource.CreateLinkedTokenSource(context.Stop);
        pending = RunAsync(wanted, cancellation.Token);
        log.Write("revival_blessing_timer_started", new { Connection = context.Id, wanted.CharacterId, wanted.InstanceId,
            wanted.UntilMilliseconds });
    }

    async Task RunAsync(RevivalBlessingTimeout expected, CancellationToken stop)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                long remaining = expected.UntilMilliseconds - clock.GetUtcNow().ToUnixTimeMilliseconds();
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(0, remaining)), clock, stop);
                await context.CommandGate.WaitAsync(stop);
                try
                {
                    stop.ThrowIfCancellationRequested();
                    // 墙钟回拨时重新等待，不提前清除客户端效果。
                    if (clock.GetUtcNow().ToUnixTimeMilliseconds() < expected.UntilMilliseconds) continue;
                    bool published = false;
                    await context.Notify(NotiPacketType.REVIVAL_CHARAC_TIMEOUT, [], "revival_blessing_expired",
                        () => published = !stop.IsCancellationRequested &&
                            context.Session.CanPublishRevivalBlessingTimeout(expected, clock.GetUtcNow().ToUnixTimeMilliseconds()));
                    if (published)
                        log.Write("revival_blessing_expired", new { Connection = context.Id, expected.CharacterId, expected.InstanceId });
                    return;
                }
                finally { context.CommandGate.Release(); }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested || context.Stop.IsCancellationRequested) { }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            log.Write("revival_blessing_timeout_failed", new { Connection = context.Id, expected.CharacterId, expected.InstanceId, Error = error.Message });
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
