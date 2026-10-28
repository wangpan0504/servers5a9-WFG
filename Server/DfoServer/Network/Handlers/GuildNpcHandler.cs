using DfoServer.Game.Guild;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Network.Projection;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

internal sealed class GuildNpcHandler(GuildNpcService service, GuildNpcProjection projection, ServerLog log) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands)
    {
        commands.Register(CmdPacketType.GUILD_NPC_REQUEST, ChangeAsync, nameof(GuildNpcHandler));
        commands.Register(CmdPacketType.GUILD_UPGRADE_POPUP_INFO, QueryAsync, nameof(GuildNpcHandler));
    }

    async Task<CommandResult> QueryAsync(GameConnectionContext context, Packet packet)
    {
        var actor = context.Session.CaptureGuildManagement();
        bool accepted = packet.Body.Length == 0 && actor is not null && service.Read(actor) is not null;
        await context.Reply(CmdPacketType.GUILD_UPGRADE_POPUP_INFO, accepted ? [1] : [0, 1, 0],
            "guild_npc_query", actor?.IsCurrent);
        if (accepted) projection.Post(context);
        return CommandResult.Handled;
    }
    async Task<CommandResult> ChangeAsync(GameConnectionContext context, Packet packet)
    {
        var actor = context.Session.CaptureGuildManagement();
        var result = new GuildNpcCommit(1, 0);
        if (GuildNpcRequests.TryParse(packet.Body, out var request))
        {
            result = new(1, request!.Action);
            if (actor is not null && context.Activity?.BlocksTownActions != true)
                result = service.Change(actor, request.Action, request.NpcId, request.Argument,
                    context.Session.GuildRequestIdentity(packet.RequestSerial));
        }
        await context.Reply(CmdPacketType.GUILD_NPC_REQUEST, GuildNpcPackets.Result(result), "guild_npc_request", actor?.IsCurrent);
        service.Publish(result);
        log.Write("guild_npc_request", new { Connection = context.Id, result.Action, result.Error,
            result.Replayed, Guild = result.State?.Management.Guild.Id });
        return CommandResult.Handled;
    }
}
