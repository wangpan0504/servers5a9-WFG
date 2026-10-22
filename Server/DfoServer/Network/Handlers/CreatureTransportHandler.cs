using System.Data.Common;
using DfoServer.Game.Creatures;
using DfoServer.Game.Inventory.Models;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

internal sealed class CreatureTransportHandler(CreatureTransportService service, InventoryProjection projection, ServerLog log) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands) =>
        commands.Register(CmdPacketType.CARGO_TRANSPORT_ITEM, UseAsync, nameof(CreatureTransportHandler));

    async Task<CommandResult> UseAsync(GameConnectionContext context, Packet packet)
    {
        var captured = context.Owner.Execute(() => (context.Session.Selected, context.Session.SelectionEpoch, context.Session.Phase));
        bool valid = CreatureTransportRequests.TryRead(packet.Body, out var request) && request.Mode == 1;
        CreatureTransportResult transfer;
        try
        {
            transfer = !valid || context.BlocksTownActions ? new(InventoryResult.Reject(7)) : context.Session.TransportCreature(service, request);
        }
        catch (Exception error) when (error is DbException or InvalidOperationException or InvalidDataException or KeyNotFoundException)
        {
            log.Write("creature_transport_failed", new { Connection = context.Id, request.StoneSlot, request.PetSlot, Error = error.Message });
            transfer = new(InventoryResult.Reject(7));
        }
        var result = transfer.Inventory;
        bool Guard() => result.Receipt is { } receipt ? context.Session.CanPublishInventory(receipt) : context.Owner.Execute(() =>
            ReferenceEquals(context.Session.Selected, captured.Selected) && context.Session.SelectionEpoch == captured.SelectionEpoch && context.Session.Phase == captured.Phase);
        log.Write("creature_transport", new { Connection = context.Id, request.StoneSlot, request.PetSlot, request.Mode, request.RecipientIndex,
            result.Accepted, result.Error, transfer.RecipientId, transfer.MailId, Revision = result.Receipt?.Character.Revision });
        await context.Reply(CmdPacketType.CARGO_TRANSPORT_ITEM, CreatureTransportPackets.Ack(request, result),
            result.Accepted ? "creature_transport_committed" : "creature_transport_rejected", Guard);
        if (result.Accepted)
        {
            var source = new InventorySlot(InventoryListType.Main, (short)request.StoneSlot);
            int count = result.Receipt!.Character.Inventory.Find(source)?.Core.Count ?? 0;
            // ACK自行同步源数量；超出u16显示范围时再用完整静默库存行纠正，不截断服务端资产。
            await projection.RefreshAsync(context, result, count <= ushort.MaxValue ? new HashSet<InventorySlot> { source } : null,
                suppressMainNotice: true, refreshEquipment: false);
        }
        return CommandResult.Handled;
    }
}
