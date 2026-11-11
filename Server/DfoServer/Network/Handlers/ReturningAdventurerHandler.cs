using DfoServer.Infrastructure;
using DfoServer.Game.Session;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Network.Projection;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

internal sealed class ReturningAdventurerHandler(InventoryProjection inventory, ServerLog log) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands) => commands.Register(CmdPacketType.RETURN_SYSTEM,
        HandleAsync, nameof(ReturningAdventurerHandler), UserStateRequirement.WorldReady);

    async Task<CommandResult> HandleAsync(GameConnectionContext context, Packet request)
    {
        if (request.Body is not { } body || ReturningAdventurerRequests.Read(body) is not { } action)
        {
            log.Write("request_rejected", new { Connection = context.Id, Action = "returning_adventurer", Reason = "invalid_length" });
            return CommandResult.Handled;
        }
        // 本目录的回归列表没有popup/chat观察任务；不能把客户端观察事件当成副本或剧情完成事实。
        if (action.Action is not (1 or 3 or 4)) return CommandResult.Unhandled;
        var result = context.Session.ClaimReturningAdventurer(action.Action, action.Argument);
        log.Write(result.Accepted ? "returning_adventurer_claim" : "returning_adventurer_rejected", new { Connection = context.Id, Character = context.Session.Selected?.Id,
            action.Action, action.Argument, result.Accepted, result.Error });
        if (result.Accepted) await inventory.RefreshAsync(context, result);
        await context.Reply(CmdPacketType.RETURN_SYSTEM, AdventurerBenefitPackets.ReturningAck(),
            "returning_adventurer_ack", () => context.Owner.IsCurrent && context.Session.Ready);
        AdventurerBenefitProjection.Post(context, force: true);
        return CommandResult.Handled;
    }
}
