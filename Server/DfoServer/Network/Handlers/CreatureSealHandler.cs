using DfoServer.Game.Creatures;
using DfoServer.Game.Inventory.Models;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

internal sealed class CreatureSealHandler(CreatureSealService service, InventoryProjection projection, ServerLog log) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands) => commands.Register(CmdPacketType.SEAL_CREATURE, Handle, nameof(CreatureSealHandler));

    async Task<CommandResult> Handle(GameConnectionContext context, Packet packet)
    {
        var captured = context.Owner.Execute(() => (context.Session.Selected, context.Session.SelectionEpoch, context.Session.Phase));
        bool SameSelection() => context.Owner.Execute(() => ReferenceEquals(captured.Selected, context.Session.Selected) &&
            captured.SelectionEpoch == context.Session.SelectionEpoch && captured.Phase == context.Session.Phase);
        CreatureSealRequest request;
        try { request = CreatureSealRequests.Read(packet.Body); }
        catch (InvalidDataException)
        {
            await context.Reply(CmdPacketType.SEAL_CREATURE, [0, 19, 0], "creature_seal_malformed", SameSelection);
            return CommandResult.Handled;
        }
        var result = context.BlocksTownActions ? InventoryResult.Reject(19) : context.Session.PlanAndCommitInventory(
            character => service.Plan(character, request.PetSlot, request.PetItemId, request.SourceSlot), 19,
            usedItem: new(InventoryListType.Main, request.SourceSlot), useTarget: new(InventoryListType.Pet, request.PetSlot));
        bool Guard() => result.Receipt is { } receipt ? context.Session.CanPublishInventory(receipt) : SameSelection();
        log.Write("creature_seal", new { Connection = context.Id, request.PetSlot, request.PetItemId, request.SourceSlot,
            result.Accepted, result.Error, Revision = result.Receipt?.Character.Revision });
        // ACK会清空宠物和工具槽，先回ACK，再投影新胶囊，避免工具槽被产物复用时误清掉产物。
        await context.Reply(CmdPacketType.SEAL_CREATURE, CreatureSealPackets.Ack(request, result),
            result.Accepted ? "creature_seal_committed" : "creature_seal_rejected", Guard);
        await projection.RefreshAsync(context, result, refreshEquipment: false);
        return CommandResult.Handled;
    }
}
