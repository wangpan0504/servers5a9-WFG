using DfoServer.Game.Guild;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Network.Projection;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

internal sealed class GuildTeleportHandler(GuildTeleportService service, TownProjection town, ServerLog log) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands) =>
        commands.Register(CmdPacketType.PARTY_TELEPORT_CONFIRM, ConfirmAsync, nameof(GuildTeleportHandler));
    public void PrepareContext(GameConnectionContext context)
    {
        context.Session.GuildTeleportBlocked = () => context.BlocksTownActions;
        context.Session.GuildTeleportStatusArrived = notice =>
        {
            var peer = notice.Offer.Peers.Single(value => ReferenceEquals(value.Identity.Session, context.Session));
            context.Sender.Post(0, (ushort)NotiPacketType.PARTY_TELEPORT_STATUS, GuildTeleportPackets.Status(notice),
                "guild_teleport_status", peer.Identity.IsCurrent);
        };
        context.Session.GuildTeleportTravelArrived = async receipt =>
        {
            long epoch = receipt.SelectionEpoch;
            bool Current() => context.Owner.IsCurrent && context.Session.Ready && context.Session.SelectionEpoch == epoch &&
                context.Session.Selected?.Id == receipt.Character.Id;
            using var publication = context.Sender.QueuePublication(Current);
            await town.TravelAsync(context, receipt, auraTeleport: true);
        };
    }
    public async Task<CommandResult> BeginAsync(GameConnectionContext context, Packet packet)
    {
        byte requestedTown = packet.Body.Length > 0 ? packet.Body[0] : (byte)0;
        var result = new GuildTeleportResult(7);
        if (!context.BlocksTownActions && GuildTeleportRequests.TryBegin(packet.Body, out var request))
            result = service.Begin(context.Session, request!, context.Session.GuildRequestIdentity(packet.RequestSerial));
        if (result.Success && !result.Replayed && result.Offer!.Peers.Length == 1) result = service.Complete(result.Offer);
        await context.Reply(CmdPacketType.PARTY_TELEPORT, result.Success ? [1] : GuildTeleportPackets.Rejected(requestedTown),
            "guild_teleport_begin");
        if (result.Success)
        {
            if (!result.Replayed && result.Offer!.Peers.Length > 1) service.Activate(result.Offer);
            await service.PublishAsync(result);
        }
        log.Write("guild_teleport_begin", new { Connection = context.Id, result.Error, result.Replayed,
            Participants = result.Offer?.Peers.Length, Guild = result.Offer?.Entitlement.GuildId });
        return CommandResult.Handled;
    }
    async Task<CommandResult> ConfirmAsync(GameConnectionContext context, Packet packet)
    {
        if (packet.Body.Length != 1 || context.BlocksTownActions) return CommandResult.Handled;
        var result = service.Confirm(context.Session, packet.Body[0], packet.RequestSerial);
        await service.PublishAsync(result);
        log.Write("guild_teleport_confirmation", new { Connection = context.Id, result.Error,
            Answer = packet.Body[0], Committed = result.Travels is not null });
        return CommandResult.Handled;
    }
}
