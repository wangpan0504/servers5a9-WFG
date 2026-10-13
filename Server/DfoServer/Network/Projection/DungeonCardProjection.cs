using DfoServer.Game.Dungeons;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;
using DfoServer.Network.Projection.Inventory;

namespace DfoServer.Network.Projection;

// 每个连接最多一个翻牌计时任务；重复70不重置时钟，断线取消并等待收尾。
internal sealed class DungeonCardProjection(GameConnectionContext context, ServerLog log,
    InventoryProjection? inventory = null) : IAsyncDisposable
{
    CancellationTokenSource? cancellation;
    Task pending = Task.CompletedTask;

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
            await context.CommandGate.WaitAsync(stop);
            try
            {
                if (context.Session.RevealFormalDungeonCard(expected) is not { } result) return;
                var receipt = result.Receipt;
                var view = context.Session.FormalCardState(receipt) ?? throw new InvalidOperationException("自动翻牌展示已失效");
                await context.Reply(CmdPacketType.SELECT_CARD, DungeonSettlementPackets.CardSelected(view),
                    "formal_free_card_auto_reveal", () => context.Session.CanPublishCardView(receipt,view));
                if (result.Inventory is { } changed)
                    await (inventory ?? throw new InvalidOperationException("自动翻牌缺少库存投影")).RefreshAsync(context, changed);
                if (result.MailId is not null) context.Session.NotifyMailbox(receipt.Character.Id);
                log.Write("formal_free_card_revealed", new { Connection = context.Id, receipt.Snapshot.Identity });
            }
            finally { context.CommandGate.Release(); }
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
