using DfoServer.Infrastructure;
using DfoServer.Network.Parsers.Inventory;
using DfoServer.Network.Projection;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers.Inventory;

internal sealed class ConsumableDisguiseHandler(TownProjection town, ServerLog log, TimeProvider? time = null) : IGameCommandModule
{
    readonly TimeProvider clock = time ?? TimeProvider.System;

    public void Register(GameCommandRegistry commands)
    {
        commands.Register(CmdPacketType.DISGUISE_REQUEST, Apply, nameof(ConsumableDisguiseHandler));
        commands.Register(CmdPacketType.DISGUISE_CANCEL, Cancel, nameof(ConsumableDisguiseHandler));
    }

    async Task<CommandResult> Apply(GameConnectionContext context, Packet packet)
    {
        var request = ConsumableDisguiseRequests.Read(packet.Body);
        var result = context.Session.ApplyConsumableDisguise(request.Type, request.Seconds, request.ItemId,
            clock.GetUtcNow().ToUnixTimeMilliseconds(), context.BlocksTownActions);
        log.Write("consumable_disguise", new { Connection = context.Id, request.ItemId, Accepted = result is not null,
            Character = result?.Character.Id, Reason = result is null ? "no_matching_committed_use" : "activated" });
        if (result is not null) await ConsumableDisguiseProjection.PublishAsync(context, town, result, clock);
        // 已证的消费者为USERINFO Actor，不为没有原生ACK合同的211制造成功包。
        return CommandResult.Handled;
    }

    async Task<CommandResult> Cancel(GameConnectionContext context, Packet packet)
    {
        ConsumableDisguiseRequests.Cancel(packet.Body);
        var result = context.Session.CancelConsumableDisguise();
        if (result is not null)
        {
            log.Write("consumable_disguise", new { Connection = context.Id, Character = result.Character.Id, Reason = "cancelled" });
            await ConsumableDisguiseProjection.PublishAsync(context, town, result, clock);
        }
        return CommandResult.Handled;
    }
}
