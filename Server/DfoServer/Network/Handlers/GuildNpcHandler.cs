using DfoServer.Game.Guild;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Network.Projection;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

internal sealed class GuildNpcHandler(GuildNpcService service, GuildNpcProjection projection, ServerLog log,
    GuildNpcBenefitService? benefits = null, InventoryProjection? inventory = null) : IGameCommandModule
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
        GuildNpcBenefitCommit? benefit = null;
        Game.Session.GuildNpcBenefitUseResult? use = null;
        if (GuildNpcRequests.TryParse(packet.Body, out var request))
        {
            result = new(1, request!.Action);
            if (actor is not null && context.Activity?.BlocksTownActions != true)
            {
                string identity = context.Session.GuildRequestIdentity(packet.RequestSerial);
                if (request.Action == 3 && benefits is not null && inventory is not null)
                {
                    use = context.Session.BuyGuildNpcBenefit(benefits, request.NpcId, request.Argument, identity);
                    benefit = use.Commit;
                }
                else if (request.Action == 6 && benefits is not null)
                    benefit = benefits.Select(actor, request.Training, identity);
                else if (request.Action is 4 or 5 && benefits?.ItemCraft(request.Argument) == true && inventory is not null)
                {
                    use = context.Session.BuyGuildNpcBenefit(benefits, request.NpcId, request.Argument, identity, request.Action);
                    benefit = use.Commit;
                }
                else result = service.Change(actor, request.Action, request.NpcId, request.Argument, identity);
                if (benefit is not null) result = new(benefit.Error, request.Action, Replayed: benefit.Replayed);
            }
        }
        await context.Reply(CmdPacketType.GUILD_NPC_REQUEST, GuildNpcPackets.Result(result), "guild_npc_request", actor?.IsCurrent);
        service.Publish(result);
        if (use?.Inventory is { } changed) await inventory!.RefreshAsync(context, changed, refreshEquipment: false);
        if (benefit is not null)
        {
            benefits!.Publish(benefit);
            if (benefit.Success) projection.Post(context);
        }
        log.Write("guild_npc_request", new { Connection = context.Id, result.Action, result.Error,
            result.Replayed, Guild = benefit?.State?.Management.Guild.Id ?? result.State?.Management.Guild.Id,
            Npc = request?.NpcId, Benefit = request?.Action is 3 or 4 or 5 ? request.Argument : (uint?)null });
        return CommandResult.Handled;
    }
}
