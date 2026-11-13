using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers.Inventory;

internal sealed class CharmChargingHandler(InventoryCharmChargingService service, InventoryProjection projection,
    ServerLog log) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands) =>
        commands.Register(CmdPacketType.CHARGING_CHARM_ENERGY, Handle, nameof(CharmChargingHandler));

    async Task<CommandResult> Handle(GameConnectionContext context, Packet packet)
    {
        var before = context.Owner.Execute(() => (context.Session.Selected, context.Session.SelectionEpoch, context.Session.Phase));
        InventoryResult result;
        bool parsed = CharmChargingRequests.TryRead(packet.Body, out var request);
        try
        {
            result = !parsed || context.Activity?.BlocksTownActions == true ? InventoryResult.Reject(1) with { RejectReason = "invalid_charm_charging_request" } :
                context.Session.PlanAndCommitInventory(character => service.Plan(character, request.Target, request.Source),
                    1, requireTown: false, usedItem: request.Source, useTarget: request.Target);
        }
        catch (Exception error) when (error is InvalidDataException or NotSupportedException or KeyNotFoundException)
        {
            log.Write("charm_charging_rejected", new { Connection = context.Id, Reason = "invalid_charging_resource", Error = error.Message });
            result = InventoryResult.Reject(1) with { RejectReason = "invalid_charging_resource" };
        }
        bool Guard() => result.Receipt is { } receipt ? context.Session.CanPublishInventory(receipt) : context.Owner.Execute(() =>
            ReferenceEquals(context.Session.Selected, before.Selected) && context.Session.SelectionEpoch == before.SelectionEpoch &&
            context.Session.Phase == before.Phase);
        log.Write("charm_charging", new { Connection = context.Id, Character = before.Selected?.Id,
            Target = request.Target.Index, Source = request.Source.Index, result.Accepted, result.Error, result.RejectReason,
            Energy = result.Receipt?.Index.Find(request.Target)?.Core.Durability, Revision = result.Receipt?.Character.Revision });
        await context.Reply(CmdPacketType.CHARGING_CHARM_ENERGY, CharmChargingPackets.Result(request, result),
            result.Accepted ? "charm_charging_committed" : "charm_charging_rejected", Guard);
        // ACK只播报实际恢复量、不改物品；用完整核心差量同时更新源堆叠和目标能量。
        await projection.RefreshAsync(context, result, refreshEquipment: false);
        return CommandResult.Handled;
    }
}
