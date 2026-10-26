using DfoServer.Game.Dungeons;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

// 与普通复活到期相同：一连接至多一个可取消任务，生命事实与身份仍只归Session/formal.Run。
internal sealed class TagEvildomRevivalProjection(GameConnectionContext context, ServerLog log, TimeProvider? time = null) : IAsyncDisposable
{
    readonly TimeProvider clock = time ?? TimeProvider.System;
    TagEvildomScheduledRevival? scheduled;
    CancellationTokenSource? cancellation;
    Task pending = Task.CompletedTask;

    public async Task RefreshAsync()
    {
        var wanted = context.Session.CurrentTagEvildomRevival();
        if (wanted == scheduled && !pending.IsCompleted) return;
        await StopAsync();
        if (wanted is null) return;
        scheduled = wanted;
        cancellation = CancellationTokenSource.CreateLinkedTokenSource(context.Stop);
        pending = RunAsync(wanted, cancellation.Token);
        log.Write("tag_evildom_revival_scheduled", new { Connection = context.Id, wanted.CharacterId,
            wanted.Timeout.InstanceId, wanted.Timeout.Ordinal, wanted.Timeout.DeathSerial, wanted.Timeout.UntilMilliseconds });
    }

    internal static async Task WaitUntilAsync(long untilMilliseconds, TimeProvider clock, CancellationToken stop)
    {
        if (untilMilliseconds < 0) throw new ArgumentOutOfRangeException(nameof(untilMilliseconds));
        while (true)
        {
            stop.ThrowIfCancellationRequested();
            long remaining = untilMilliseconds - clock.GetUtcNow().ToUnixTimeMilliseconds();
            if (remaining <= 0) return;
            await Task.Delay(TimeSpan.FromMilliseconds(remaining), clock, stop);
        }
    }

    // 返回时持有连接命令门禁；墙钟可能在等待门禁时回拨，必须释放后重新有界等待，不能丢掉唯一任务。
    internal static async Task<long> WaitForCommandTurnAsync(long untilMilliseconds, TimeProvider clock, SemaphoreSlim gate, CancellationToken stop)
    {
        while (true)
        {
            await WaitUntilAsync(untilMilliseconds, clock, stop);
            await gate.WaitAsync(stop);
            try
            {
                long now = clock.GetUtcNow().ToUnixTimeMilliseconds();
                if (now >= untilMilliseconds) return now;
            }
            catch
            {
                gate.Release();
                throw;
            }
            gate.Release();
        }
    }

    async Task RunAsync(TagEvildomScheduledRevival expected, CancellationToken stop)
    {
        try
        {
            long at = await WaitForCommandTurnAsync(expected.Timeout.UntilMilliseconds, clock, context.CommandGate, stop);
            try
            {
                stop.ThrowIfCancellationRequested();
                var receipt = context.Session.ReviveFormalTagEvildom(expected, at);
                if (receipt?.Result.Outcome != DungeonRunOutcome.Accepted) return;
                await context.Notify(NotiPacketType.REVIVAL_TAG_TOURNAMENT_CHARACTER, TagEvildomRunPackets.Revival(receipt.Result),
                    "tag_evildom_revived", () => !stop.IsCancellationRequested && context.Session.CanPublishTagEvildomLife(receipt));
            }
            finally { context.CommandGate.Release(); }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested || context.Stop.IsCancellationRequested) { }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            log.Write("tag_evildom_revival_failed", new { Connection = context.Id, expected.Timeout.InstanceId, Error = error.Message });
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
