using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Items;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection.Inventory;

// 仅保存连接已发视图，次数与角色的唯一真源仍是 Session.Selected.Inventory。
internal sealed class DailyRetainedBoosterProjection(GameConnectionContext context, BoosterCatalog catalog,
    ServerLog log, TimeProvider? time = null, Func<Task>? afterDailyReset = null) : IAsyncDisposable
{
    readonly TimeProvider clock = time ?? TimeProvider.System;
    readonly HashSet<int> everSent = [];
    (long Epoch, int Day, (int ItemId, byte Used)[] Rows)? announced;
    CancellationTokenSource? cancellation;
    Task pending = Task.CompletedTask;
    long timerEpoch = -1;

    public Task InitializeAsync(Character character, Func<bool>? canSend = null) => PublishAsync(character, false, canSend);

    public Task CommittedAsync(InventoryCommitReceipt receipt) =>
        PublishAsync(receipt.Character, true, () => context.Session.CanPublishInventory(receipt));

    async Task PublishAsync(Character character, bool requireReady, Func<bool>? canSend = null)
    {
        var capture = context.Owner.Execute(() => (Epoch: context.Session.SelectionEpoch, Phase: context.Session.Phase));
        int day = ShopPurchaseCount.Today(clock.GetUtcNow());
        // 回拨不能把已经公布的后一个服务日当成历史日清空；恢复墙钟后重新核对。
        if (announced is { } prior && prior.Epoch == capture.Epoch && day < prior.Day) return;
        var rows = everSent.Concat(catalog.DailyRetainedTemplateIds)
            .Concat(character.Inventory.DailyBoosterUses.Select(row => row.ItemId)).Distinct().Order()
            .Select(id => (ItemId: id, Used: DailyBoosterUsage.Effective(character.Inventory.DailyBoosterUses, id, day))).ToArray();
        bool Guard() => !context.Stop.IsCancellationRequested && context.Owner.Execute(() =>
            context.Session.SelectionEpoch == capture.Epoch && context.Session.Phase == capture.Phase &&
            ReferenceEquals(context.Session.Selected, character) && (!requireReady || context.Session.Ready) &&
            ShopPurchaseCount.Today(clock.GetUtcNow()) == day && (canSend?.Invoke() ?? true));
        if (!Guard()) return;
        if (announced is { } previous && previous.Epoch == capture.Epoch && previous.Day == day && previous.Rows.SequenceEqual(rows)) return;
        // 空包只表达空视图；显式零条目负责覆盖旧角色、旧服务日及历史已发 key。
        foreach (var chunk in rows.Length == 0 ? new[] { Array.Empty<(int ItemId, byte Used)>() } : rows.Chunk(byte.MaxValue))
        {
            bool sent = false;
            await context.Notify(NotiPacketType.SET_USABLE_COUNT_INFO, DailyBoosterPackets.Used(chunk),
                "daily_booster_used", () => sent = Guard());
            if (!sent) return;
            foreach (var row in chunk) everSent.Add(row.ItemId);
        }
        if (Guard()) announced = (capture.Epoch, day, rows);
    }

    public async Task RefreshAsync()
    {
        var capture = context.Owner.Execute(() => (Character: context.Session.Ready ? context.Session.Selected : null,
            Epoch: context.Session.SelectionEpoch));
        if (capture.Character is null)
        {
            await StopAsync();
            return;
        }
        await PublishAsync(capture.Character, true);
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
                    var selected = context.Owner.Execute(() => context.Session.Ready && context.Session.SelectionEpoch == epoch
                        ? context.Session.Selected : null);
                    if (selected is null) return;
                    await PublishAsync(selected, true);
                    if (afterDailyReset is not null) await afterDailyReset();
                    // 复用连接已有的服务日计时任务；好感度日次数仍从当前角色快照投影，不建平行账本。
                    if (!selected.Inventory.NpcFavor.IsEmpty)
                    {
                        int day = ShopPurchaseCount.Today(clock.GetUtcNow());
                        await context.Notify(NotiPacketType.NPC_FAVOR_POINT_INFO,
                            NpcFavorPackets.Snapshot(selected.Inventory.NpcFavor, clock.GetUtcNow()), "npc_favor_daily_reset",
                            () => !stop.IsCancellationRequested && context.Owner.Execute(() => context.Session.Ready &&
                                context.Session.SelectionEpoch == epoch && ReferenceEquals(context.Session.Selected, selected) &&
                                ShopPurchaseCount.Today(clock.GetUtcNow()) == day));
                    }
                }
                finally { context.CommandGate.Release(); }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested || context.Stop.IsCancellationRequested) { }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            log.Write("daily_booster_projection_failed", new { Connection = context.Id, Epoch = epoch, Error = error.Message });
            context.Disconnect();
        }
    }

    async Task StopAsync()
    {
        if (cancellation is null) return;
        // Refresh 可能持有 CommandGate；取消必须先解除旧任务的门禁等待再等待收尾。
        await cancellation.CancelAsync();
        await pending;
        cancellation.Dispose();
        cancellation = null;
        timerEpoch = -1;
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
