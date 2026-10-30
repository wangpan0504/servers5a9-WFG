using DfoServer.Game.Dungeons;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;
using DfoServer.Network.Projection.Inventory;

namespace DfoServer.Network.Projection;

// 每个连接最多一个翻牌计时任务；重复70不重置时钟，断线取消并等待收尾。
internal sealed class DungeonCardProjection(GameConnectionContext context, ServerLog log,
    InventoryProjection? inventory = null, PartyDungeonService? partyDungeons = null) : IAsyncDisposable
{
    CancellationTokenSource? cancellation;
    Task pending = Task.CompletedTask;

    internal static async Task PublishSelectionAsync(GameConnectionContext context, FormalDungeonReceipt receipt,
        DungeonCardView view, DungeonCardView?[]? snapshot, string action)
    {
        var template = context.PartyCardTemplate;
        bool ordered = snapshot is not null && context.DispatchGates?.HasShared == true;
        bool Current() => (ordered ? context.Session.CanPublishPartyCards(receipt) : context.Session.CanPublishCardView(receipt, view)) &&
            (snapshot is null || template is not null && ReferenceEquals(context.Session.PartyDungeon, template.Instance) &&
                (ordered ? template.Instance.CanPublishCardSnapshot(snapshot) : template.Instance.IsCardSnapshotCurrent(snapshot)));
        if (snapshot is not null && template is not null)
            await context.Notify(NotiPacketType.CLEAR_DUNGEON_REWARD,
                DungeonSettlementPackets.PartyCardReward(template.Body, receipt.PartySlot, snapshot), "party_card_reward", Current);
        await context.Reply(CmdPacketType.SELECT_CARD, DungeonSettlementPackets.CardSelected(view, snapshot), action, Current);
    }

    public async Task StartAsync(FormalDungeonReceipt receipt)
    {
        if (!context.Session.ScheduleFormalDungeonCard(receipt)) return;
        await StopAsync();
        cancellation = CancellationTokenSource.CreateLinkedTokenSource(context.Stop);
        pending = RevealAsync(receipt, cancellation.Token);
    }

    async Task RevealAsync(FormalDungeonReceipt expected, CancellationToken stop)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(4), stop);
            using (var gates = await DispatchGateScope.EnterAsync(context, stop))
            {
                using var publication = !gates.HasShared ? null : context.Sender.QueuePublication(() => context.Owner.IsCurrent && !context.Stop.IsCancellationRequested);
                if (context.Session.RevealFormalDungeonCard(expected) is not { } result) return;
                var receipt = result.Receipt;
                var view = context.Session.FormalCardState(receipt) ?? throw new InvalidOperationException("自动翻牌展示已失效");
                var instance = context.Session.PartyDungeon;
                var snapshot = instance?.RecordCard(context.Session, view);
                await PublishSelectionAsync(context, receipt, view, snapshot, "formal_free_card_auto_reveal");
                if (instance is not null && snapshot is not null)
                    partyDungeons?.PublishSettlementCard(instance, context.Session, snapshot);
                if (result.Inventory is { } changed)
                    await (inventory ?? throw new InvalidOperationException("自动翻牌缺少库存投影")).RefreshAsync(context, changed);
                if (result.MailId is not null) context.Session.NotifyMailbox(receipt.Character.Id);
                log.Write("formal_free_card_revealed", new { Connection = context.Id, receipt.Snapshot.Identity });
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested || context.Stop.IsCancellationRequested) { }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            log.Write("formal_free_card_failed", new { Connection = context.Id, expected.Snapshot.Identity, Error = error.Message });
        }
    }

    async Task StopAsync()
    {
        if (cancellation is null) return;
        await cancellation.CancelAsync();
        await pending;
        cancellation.Dispose();
        cancellation = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
