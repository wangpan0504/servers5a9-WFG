using DfoServer.Game.Creatures;
using DfoServer.Infrastructure;
using DfoServer.Network.Parsers.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

// 每连接一个可取消时钟；累计量由Session绑定角色、选角epoch及宠物UID，资产仍只来自Selected。
internal sealed class CreatureRuntimeProjection(GameConnectionContext context, CreatureService service,
    CreatureProjection projection, ServerLog log) : IAsyncDisposable
{
    CancellationTokenSource? cancellation;
    Task pending = Task.CompletedTask;
    Guid cycle;

    public async Task BeforeCommandAsync(Packet packet)
    {
        bool force = packet.Opcode is (ushort)CmdPacketType.EXIT or (ushort)CmdPacketType.RETURN_SELECT_CHARACTER or
            (ushort)CmdPacketType.RENAME_CREATURE or (ushort)CmdPacketType.USE_CREATURE_EVOLUTION_ITEM or
            (ushort)CmdPacketType.CREATURE_EXP_BOOK or (ushort)CmdPacketType.SET_PLAY_RESULT;
        if (packet.Opcode == (ushort)CmdPacketType.USE_STACKABLE)
            force = packet.Body.Length == 15 && packet.Body[2] == 7;
        if (packet.Opcode == (ushort)CmdPacketType.MOVE_ITEMSPACE)
        {
            var move = InventoryRequests.Move(packet.Body);
            force = move.Source.List == Game.Inventory.Models.InventoryListType.Equipment && move.Source.Index is >= 26 and <= 29 ||
                move.Destination.List == Game.Inventory.Models.InventoryListType.Equipment && move.Destination.Index is >= 26 and <= 29;
        }
        if (force) await FlushAsync();
    }

    public async Task FlushAsync(bool publish = true)
    {
        var ticket = context.Session.PrepareCreatureTick(service);
        if (ticket is null) return;
        var result = context.Session.SettleCreatureTick(ticket, service, force: true);
        if (publish) await projection.ChangedAsync(context, result);
    }

    public async Task RefreshAsync()
    {
        var selected = context.Owner.Execute(() => context.Session.Ready ? context.Session.Selected : null);
        var pet = selected is null ? null : service.Equipped(selected, service.Clock.GetUtcNow().ToUnixTimeSeconds());
        if (pet?.Creature is { Mode: 0 } && service.Catalog.Get(pet.Core.ItemId) is { UsesExperienceItems: true })
        {
            var normalized = context.Session.PlanAndCommitInventory(character => service.Normalize(character,
                service.Clock.GetUtcNow().ToUnixTimeSeconds()), 123, requireTown: false);
            await projection.ChangedAsync(context, normalized);
        }
        var wanted = context.Session.PrepareCreatureTick(service);
        if (wanted?.Revive == true)
        {
            await FlushAsync();
            wanted = context.Session.PrepareCreatureTick(service);
        }
        if (wanted is not null && cycle == wanted.CycleId && !pending.IsCompleted) return;
        await StopAsync();
        if (wanted is null) return;
        cycle = wanted.CycleId;
        cancellation = CancellationTokenSource.CreateLinkedTokenSource(context.Stop);
        pending = RunAsync(cycle, cancellation.Token);
    }

    async Task RunAsync(Guid expected, CancellationToken stop)
    {
        try
        {
            bool retry = false;
            while (!stop.IsCancellationRequested)
            {
                CreatureClockTicket? next;
                await context.CommandGate.WaitAsync(stop);
                try { next = context.Session.PrepareCreatureTick(service); }
                finally { context.CommandGate.Release(); }
                if (next is null || next.CycleId != expected) return;
                var delay = service.Clock.GetElapsedTime(service.Clock.GetTimestamp(), next.DueTimestamp);
                if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
                if (retry && delay < TimeSpan.FromSeconds(1)) delay = TimeSpan.FromSeconds(1);
                await Task.Delay(delay, service.Clock, stop);
                await context.CommandGate.WaitAsync(stop);
                try
                {
                    stop.ThrowIfCancellationRequested();
                    next = context.Session.PrepareCreatureTick(service);
                    if (next is null || next.CycleId != expected) return;
                    var result = context.Session.SettleCreatureTick(next, service);
                    retry = !result.Accepted;
                    if (result.Accepted) await projection.ChangedAsync(context, result);
                }
                finally { context.CommandGate.Release(); }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested || context.Stop.IsCancellationRequested) { }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            log.Write("creature_clock_failed", new { Connection = context.Id, Cycle = expected, Error = error.Message });
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
        cycle = Guid.Empty;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        try { await FlushAsync(publish: false); }
        catch (InvalidOperationException) when (!context.Owner.IsCurrent) { }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            log.Write("creature_clock_close_failed", new { Connection = context.Id, Error = error.Message });
        }
    }
}
