using DfoServer.Game.Skills;
using DfoServer.Infrastructure;
using DfoServer.Network.Parsers.Inventory;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers.Inventory;

internal sealed class SkillSwitchInventoryHandler(
    SkillSwitchInventoryService service,
    ServerLog log) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands) =>
        commands.Register(
            CmdPacketType.SKILL_SWITCH_INVENTORY,
            HandleAsync,
            nameof(SkillSwitchInventoryHandler));

    async Task<CommandResult> HandleAsync(GameConnectionContext context, Packet packet)
    {
        SkillSwitchInventoryRequest request = SkillSwitchInventoryRequests.Parse(packet.Body);
        SkillSwitchInventoryResult result =
            context.Session.UpdateSkillSwitchInventory(request, service);
        if (result.Receipt is null)
        {
            log.Write("skill_switch_inventory_rejected", new
            {
                Connection = context.Id,
                result.Failure,
                Reason = "session_state"
            });
            // 空 ACK 只解除客户端请求门禁；没有当前角色回执时不发布任何登记快照。
            await context.Reply(
                CmdPacketType.SKILL_SWITCH_INVENTORY,
                Network.Builders.Inventory.SkillSwitchInventoryPackets.Ack(),
                "skill_switch_inventory_session_rejected");
            return CommandResult.Handled;
        }
        if (!result.Accepted)
            log.Write("skill_switch_inventory_rejected", new
            {
                Connection = context.Id,
                result.Failure,
                request.Kind,
                request.SkillId,
                request.EquipmentPart,
                request.ListType,
                request.Slot
            });
        await SkillSwitchInventoryProjection.PublishCommandAsync(context, request, result);
        return CommandResult.Handled;
    }
}
