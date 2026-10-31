using DfoServer.Game.Characters;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

// 每连接最多一个到期任务；资格、形态及截止仍由Session的当前角色持有。
internal sealed class ConsumableDisguiseProjection(GameConnectionContext context, TownProjection town,
    ServerLog log, TimeProvider? time = null) : IAsyncDisposable
{
    readonly TimeProvider clock = time ?? TimeProvider.System;
    (long Epoch, ConsumableDisguiseAppearance Appearance)? scheduled;
    CancellationTokenSource? cancellation;
    Task pending = Task.CompletedTask;

    internal static async Task PublishAsync(GameConnectionContext context, TownProjection town,
        ConsumableDisguiseChange change, TimeProvider clock)
    {
        if (change.Phase != SessionPhase.Town) return;
        bool Guard() => !context.Stop.IsCancellationRequested && context.Session.CanPublishConsumableDisguise(change);
        await context.Notify(NotiPacketType.USERINFO, TownPackets.Actor(context.Session.ReadSkillCharacter(change.Character),
            context.Session.ReadPvpGrade(change.Character), context.Channel, clock.GetUtcNow().ToUnixTimeSeconds(),
            context.Session.ReadAdventureName(), premiumPcRoom: context.Session.PremiumPcRoom), "consumable_disguise_actor", Guard);
        context.Owner.Execute(() =>
        {
            if (Guard()) town.Refresh(context);
            return true;
        });
    }

    public async Task RefreshAsync()
    {
        if (context.Session.RefreshConsumableDisguise(clock.GetUtcNow().ToUnixTimeMilliseconds()) is { } change)
            await PublishAsync(context, town, change, clock);
        var wanted = context.Session.CurrentConsumableDisguise();
        if (wanted == scheduled) return;
        await StopAsync();
        if (wanted is null) return;
        scheduled = wanted;
        cancellation = CancellationTokenSource.CreateLinkedTokenSource(context.Stop);
        pending = RunAsync(wanted.Value, cancellation.Token);
    }

    async Task RunAsync((long Epoch, ConsumableDisguiseAppearance Appearance) expected, CancellationToken stop)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                long remaining = expected.Appearance.UntilMilliseconds - clock.GetUtcNow().ToUnixTimeMilliseconds();
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(0, remaining)), clock, stop);
                await context.CommandGate.WaitAsync(stop);
                try
                {
                    stop.ThrowIfCancellationRequested();
                    if (context.Session.CurrentConsumableDisguise() != expected) return;
                    if (clock.GetUtcNow().ToUnixTimeMilliseconds() < expected.Appearance.UntilMilliseconds) continue;
                    if (context.Session.RefreshConsumableDisguise(clock.GetUtcNow().ToUnixTimeMilliseconds()) is { } change)
                    {
                        await PublishAsync(context, town, change, clock);
                        log.Write("consumable_disguise", new { Connection = context.Id, Character = change.Character.Id, Reason = "expired" });
                    }
                    return;
                }
                finally { context.CommandGate.Release(); }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested || context.Stop.IsCancellationRequested) { }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            log.Write("consumable_disguise_failed", new { Connection = context.Id, Error = error.Message });
            context.Disconnect();
        }
    }

    async Task StopAsync()
    {
        if (cancellation is null) return;
        // 调用方可能持有CommandGate，先取消旧任务的门禁等待再收尾。
        await cancellation.CancelAsync();
        await pending;
        cancellation.Dispose();
        cancellation = null;
        scheduled = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
