using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers.Inventory;

internal sealed class EnchantmentHandler(InventoryEnchantmentService service, InventoryProjection projection, ServerLog log) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands) =>
        commands.Register(CmdPacketType.ENCHANT_BY_BEAD, Handle, nameof(EnchantmentHandler));

    async Task<CommandResult> Handle(GameConnectionContext context, Packet packet)
    {
        EnchantmentRequest request;
        try { request = EnchantmentRequests.Read(packet.Body); }
        catch (InvalidDataException)
        {
            await context.Reply(CmdPacketType.ENCHANT_BY_BEAD, CargoPackets.Ack(InventoryResult.Reject(InventoryEnchantmentService.Rejection)),
                "enchantment_malformed");
            return CommandResult.Handled;
        }
        var capture = context.Owner.Execute(() => (context.Session.Selected, context.Session.SelectionEpoch, context.Session.Phase));
        var result = context.Activity?.BlocksTownActions == true ? InventoryResult.Reject(InventoryEnchantmentService.Rejection) :
            context.Session.PlanAndCommitInventory(character => service.Plan(character, request, service.Clock.GetUtcNow().ToUnixTimeSeconds()),
                InventoryEnchantmentService.Rejection, requireTown: false);
        bool Guard() => result.Receipt is { } receipt ? context.Session.CanPublishInventory(receipt) :
            context.Owner.Execute(() => ReferenceEquals(capture.Selected, context.Session.Selected) &&
                capture.SelectionEpoch == context.Session.SelectionEpoch && capture.Phase == context.Session.Phase);
        log.Write("enchantment", new { Connection = context.Id, SourceList = request.Source.List, SourceSlot = request.Source.Index,
            TargetList = request.Target.List, TargetSlot = request.Target.Index, result.Accepted, Revision = result.Receipt?.Character.Revision });
        await context.Reply(CmdPacketType.ENCHANT_BY_BEAD, EnchantmentPackets.Ack(result, request),
            result.Accepted ? "enchantment_committed" : "enchantment_rejected", Guard);
        await projection.RefreshAsync(context, result);
        return CommandResult.Handled;
    }
}
