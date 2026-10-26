using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

internal sealed class NpcFavorHandler(NpcFavorService service, InventoryProjection inventory, ServerLog log) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands) => commands.Register(CmdPacketType.REQUEST_NPC_FAVOR_OPERATION,
        OperateAsync, nameof(NpcFavorHandler));

    async Task<CommandResult> OperateAsync(GameConnectionContext context, Packet packet)
    {
        var capture = context.Owner.Execute(() => (context.Session.Selected, context.Session.SelectionEpoch, context.Session.Phase));
        NpcFavorRequest? request = null;
        InventoryResult result;
        try
        {
            request = NpcFavorRequests.Read(packet.Body);
            result = context.BlocksTownActions ? InventoryResult.Reject(19) :
                context.Session.PlanAndCommitInventory(character => service.Plan(character, request), 19);
        }
        catch (InvalidDataException error)
        {
            log.Write("npc_favor_failed", new { Connection = context.Id, Error = error.Message });
            result = InventoryResult.Reject(19);
        }
        bool Guard() => result.Receipt is { } receipt ? context.Session.CanPublishInventory(receipt) : context.Owner.Execute(() =>
            ReferenceEquals(context.Session.Selected, capture.Selected) && context.Session.SelectionEpoch == capture.SelectionEpoch &&
            context.Session.Phase == capture.Phase);
        log.Write("npc_favor_operation", new { Connection = context.Id, request?.Operation, request?.NpcId,
            result.Accepted, result.Error, Revision = result.Receipt?.Character.Revision });
        if (result.Receipt is not { } committed || request is null)
        {
            await context.Reply(CmdPacketType.REQUEST_NPC_FAVOR_OPERATION, NpcFavorPackets.Rejected(result.Error), "npc_favor_rejected", Guard);
            return CommandResult.Handled;
        }
        byte[] body;
        if (request.Operation == 1) body = NpcFavorPackets.Color(request);
        else
        {
            // owner编号来自已提交快照的实际变化，不重读资源或重新掷骰。
            var row = committed.Character.Inventory.NpcFavor.Single(row =>
                result.Plan!.Before.NpcFavor.SingleOrDefault(old => old.NpcId == row.NpcId) != row);
            int previous = result.Plan!.Before.NpcFavor.SingleOrDefault(old => old.NpcId == row.NpcId)?.Points ?? 0;
            body = NpcFavorPackets.Gift(request, row, row.Points - previous);
        }
        await context.Reply(CmdPacketType.REQUEST_NPC_FAVOR_OPERATION, body, "npc_favor_committed", Guard);
        // ACK814不消费源物品：按已提交库存回报钱包或物品数量，不产生获得播报。
        await inventory.RefreshAsync(context, result, refreshEquipment: false, suppressMainNotice: true);
        return CommandResult.Handled;
    }
}
