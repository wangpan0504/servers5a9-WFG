using DfoServer.Game.BattlePass;
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
        // 每次发布都扫一次过期：覆盖 06:00 唤醒和冷启动后首次补跑。无变化则 Commit 是空操作。
        // 6 点可能没开服 → 本钩子不能当唯一手段，选角仍必扫。A 不追溯：ExpireTime=0 不动。
        var expiry = context.Session.ExpireInventory();
        if (expiry is { Accepted: true, Plan.Changed: true })
        {
            if (inventory is not null) await inventory.RefreshAsync(context, expiry);
            if (expiry.Receipt is { } expired)
                log.Write("item_expired", new { Connection = context.Id, Character = expired.Character.Id,
                    expired.Character.Revision, Slots = expiry.Plan.ChangedSlots.Length });
        }
        await BossSelectionBuffProjection.PostAsync(context);
        await EndlessAdventureProjection.PublishAsync(context);
        if (context.Session.NeedsEndlessAdventureRefresh) return false;
        if (context.DailyAttendance is { } attendance) await attendance.RefreshAsync();
        town.PublishLukeChannelCards(context);
        var now = clock.GetUtcNow();
        long selectedEpoch = context.Session.SelectionEpoch;
        int localDay = FatigueDailyReset.Day(now, clock.LocalTimeZone);
        int legacyDay = ShopPurchaseCount.Today(now);
        if (legacyPublished is { } legacy && legacy.Epoch == selectedEpoch && legacy.Day < legacyDay)
        {
            await town.QuestCycleAsync(context);
            await town.LegacyServiceDayAsync(context);
            RaidClearProjection.Refresh(context);
        }
        if (!await PublishBattlePassAsync(now)) return false;
        if (localPublished is { } local && local.Epoch == selectedEpoch && local.Day < localDay)
            await town.RefreshAssaultDailyAsync(context);
        if (legacyPublished is not { } oldLegacy || oldLegacy.Epoch != selectedEpoch || oldLegacy.Day <= legacyDay)
            legacyPublished = (selectedEpoch, legacyDay);
        if (localPublished is not { } oldLocal || oldLocal.Epoch != selectedEpoch || oldLocal.Day <= localDay)
            localPublished = (selectedEpoch, localDay);
        return true;
    }

    // 进度仍从原账号季读取，不触发登录来源；只补尚未实际投影的期段。
    async Task<bool> PublishBattlePassAsync(DateTimeOffset now)
    {
        if (town.BattlePassMissions is not { } policy || policy.Active(now).IsEmpty) return true;
        // 日切先到开始边界时也沿事件入口完整初始化，避免先单发1801、随后整组再发1801。
        if (context.ServerEvents is { } events) await events.PublishBattlePassOpeningAsync();
        int day = BattlePassDay(policy, now);
        if (BattlePassPublished(policy, day)) return true;
        var current = context.Session.ReadBattlePassMissionState(now);
        if (current is null || !current.State.Active) return false;
        await BattlePassMissionProjection.MissionsAsync(context, current.State, current.Receipt);
        return BattlePassPublished(policy, day) && BattlePassDay(policy, clock.GetUtcNow()) == day;
    }

    bool BattlePassPublished(BattlePassMissionPolicy policy, int day) => context.Owner.Execute(() =>
        context.BattlePassMissionDisplay is { } shown && shown.Epoch == context.Session.SelectionEpoch &&
        shown.Period >= policy.Start.AddDays(day).ToUnixTimeSeconds());

    static int BattlePassDay(BattlePassMissionPolicy policy, DateTimeOffset now) => now < policy.Start ? -1 :
        now >= policy.End ? policy.Days : checked((int)((now - policy.Start).Ticks / TimeSpan.TicksPerDay));

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
                if (town.BattlePassMissions is { } policy && now < policy.End)
                {
                    var boundary = policy.Start.AddDays(BattlePassDay(policy, now) + 1);
                    // 发布采样后才跨界时立即补刷，不能直接排到下一日。
                    if (!policy.Active(now).IsEmpty && !BattlePassPublished(policy, BattlePassDay(policy, now))) next = now;
                    else if (boundary < next) next = boundary;
                }
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
