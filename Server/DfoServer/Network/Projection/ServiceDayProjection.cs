using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

// 只管理当前选角的到点唤醒；各业务仍从原Session和持久化owner推进，不复制在线资格。
internal sealed class ServiceDayProjection(GameConnectionContext context, ServerLog log, TownProjection town, TimeProvider? time = null,
    Inventory.InventoryProjection? inventory = null, DfoServer.Game.Pvp.PvpMissionBook? missions = null, PvpMissionProjection? missionProjection = null) : IAsyncDisposable
{
    readonly TimeProvider clock = time ?? TimeProvider.System;
    CancellationTokenSource? cancellation;
    Task pending = Task.CompletedTask;
    long epoch = -1;
    (long Epoch, int Day)? localPublished;
    (long Epoch, int Day)? legacyPublished;
    int? missionDay;
    long missionEpoch = -1;
    public async Task<bool> PublishAsync()
    {
        if (!context.Session.CanAdvanceServiceDay()) return false;
        if (context.Session.AdvanceFatigueDay(clock.GetUtcNow()) is { } receipt)
        {
            var current = context.Session.FatigueForClient(receipt.Character);
            context.Sender.Post(0, (ushort)NotiPacketType.FATIGUE, FatiguePackets.Current(current),
            "fatigue_daily_reset", () => context.Session.CanPublishFatigueDay(receipt) && context.Owner.Execute(() =>
                context.Session.Selected is { } selected && context.Session.FatigueForClient(selected) == current));
            log.Write("fatigue_daily_reset", new { Connection = context.Id, Character = receipt.Character.Id,
                Day = receipt.Character.Fatigue!.ResetDay, PreviousRemaining = receipt.Before.Fatigue!.Remaining,
                Remaining = receipt.Character.Fatigue.Remaining, receipt.Character.Fatigue.Maximum, receipt.Character.Revision });
        }
        if (missions is not null)
        {
            if (missionEpoch == context.Session.SelectionEpoch && missionDay != missions.Period.Day && missionProjection is not null)
            {
                var reward = context.Session.RefreshPvpMissions(missions);
                if (missions.Capture(context.Session) is { Daily.Length: 0 } || !missionProjection.QueueRewards(context, reward) ||
                    !missionProjection.Queue(context, update: true)) return false;
            }
            missionDay = missions.Period.Day;
            missionEpoch = context.Session.SelectionEpoch;
        }
        var cycle = context.Session.AdvanceServerItems(epoch != context.Session.SelectionEpoch);
        if (cycle is { Accepted: true, Receipt: { } committed, Plan: { } plan })
        {
            if (inventory is not null) await inventory.RefreshAsync(context, cycle);
            if (cycle.MailboxChanged) context.Session.NotifyMailbox(committed.Character.Id);
            if (plan.Changed) log.Write("server_item_cycle", new { Connection = context.Id, Character = committed.Character.Id,
                Day = committed.Character.Inventory.ServerItems.RefillDay, committed.Character.Revision, MailCount = plan.DailyItemMails.Length });
        }
        var now = clock.GetUtcNow();
        long selectedEpoch = context.Session.SelectionEpoch;
        int localDay = FatigueDailyReset.Day(now, clock.LocalTimeZone);
        int legacyDay = ShopPurchaseCount.Today(now);
        if (legacyPublished is { } legacy && legacy.Epoch == selectedEpoch && legacy.Day < legacyDay)
        {
            await town.QuestCycleAsync(context);
            await town.LegacyServiceDayAsync(context);
        }
        if (localPublished is { } local && local.Epoch == selectedEpoch && local.Day < localDay)
            await town.AssaultServiceDayAsync(context);
        if (legacyPublished is not { } oldLegacy || oldLegacy.Epoch != selectedEpoch || oldLegacy.Day <= legacyDay)
            legacyPublished = (selectedEpoch, legacyDay);
        if (localPublished is not { } oldLocal || oldLocal.Epoch != selectedEpoch || oldLocal.Day <= localDay)
            localPublished = (selectedEpoch, localDay);
        return true;
    }

    public async Task RefreshAsync()
    {
        await PublishAsync();
        var selection = context.Owner.Execute(() => (context.Session.Ready, context.Session.SelectionEpoch));
        if (!selection.Ready) { await StopAsync(); return; }
        if (selection.SelectionEpoch == epoch && !pending.IsCompleted) return;
        await StopAsync();
        epoch = selection.SelectionEpoch;
        cancellation = CancellationTokenSource.CreateLinkedTokenSource(context.Stop);
        pending = RunAsync(epoch, cancellation.Token);
    }

    async Task RunAsync(long selectedEpoch, CancellationToken stop)
    {
        try
        {
            int retrySeconds = 0;
            while (!stop.IsCancellationRequested)
            {
                var now = clock.GetUtcNow();
                var next = context.Session.NextServiceWake(now);
                var missionReset = DfoServer.GameWorld.Time.ServiceDay.NextReset(now, clock.LocalTimeZone);
                if (missions is not null && missionReset < next) next = missionReset;
                var legacy = DailyBoosterUsage.NextReset(now);
                if (legacy < next) next = legacy;
                var delay = retrySeconds == 0 ? next - now : TimeSpan.FromSeconds(retrySeconds);
                await Task.Delay(delay, clock, stop);
                await context.CommandGate.WaitAsync(stop);
                try
                {
                    stop.ThrowIfCancellationRequested();
                    if (!context.Owner.Execute(() => context.Session.Ready && context.Session.SelectionEpoch == selectedEpoch)) return;
                    retrySeconds = await PublishAsync() ? 0 : Math.Min(retrySeconds == 0 ? 1 : retrySeconds * 2, 30);
                }
                finally { context.CommandGate.Release(); }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested || context.Stop.IsCancellationRequested) { }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            log.Write("fatigue_daily_reset_failed", new { Connection = context.Id, Epoch = selectedEpoch, Error = error.Message });
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
        epoch = -1;
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
