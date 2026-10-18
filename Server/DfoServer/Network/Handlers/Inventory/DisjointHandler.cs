using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers.Inventory;

internal sealed class DisjointHandler(InventoryDisjointService disjoint, InventoryProjection projection) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands) => commands.Register(CmdPacketType.DISJOINT_ITEM, Handle, nameof(DisjointHandler));

    async Task<CommandResult> Handle(GameConnectionContext context, Packet packet)
    {
        var request = DisjointRequests.Read(packet.Body);
        var original = context.Owner.Execute(() => (context.Session.Selected, context.Session.SelectionEpoch, context.Session.Phase,
            RejectedSource: context.Session.CaptureDisjointRejectedSource(request)));
        DisjointReceipt? receipt = null;
        var result = context.Activity?.BlocksTownActions == true ? InventoryResult.Reject(19) :
            context.Session.DisjointEquipment(request, disjoint, out receipt);
        bool CanSend() => result.Receipt is { } committed ? context.Session.CanPublishInventory(committed) : context.Owner.Execute(() =>
            context.Session.SelectionEpoch == original.SelectionEpoch && context.Session.Phase == original.Phase &&
            ReferenceEquals(context.Session.Selected, original.Selected));
        await context.Reply(CmdPacketType.DISJOINT_ITEM, DisjointPackets.Ack(result, receipt),
            result.Accepted ? "equipment_disjoint" : "equipment_disjoint_rejected", CanSend);
        if (!result.Accepted && original.RejectedSource is { } rejectedSource)
        {
            bool CanRestore() => context.Session.CanPublishDisjointRejectedSource(rejectedSource);
            await context.Notify(NotiPacketType.UPDATE_ITEM_LIST, DisjointPackets.RejectedSource(rejectedSource.Source),
                "equipment_disjoint_rejected_source", CanRestore);
            await InventoryProjection.RestoreLocksAsync(context, rejectedSource.Character.Inventory,
                [rejectedSource.Source.Position], canSend: CanRestore);
        }
        await projection.RefreshAsync(context, result);
        return CommandResult.Handled;
    }
}
