using DfoServer.Game.Session;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

internal sealed class PlayerInteractionHandler(OnlineSessionDirectory online) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands) =>
        commands.Register(CmdPacketType.REQUEST_PEER, MenuAsync, nameof(PlayerInteractionHandler), UserStateRequirement.WorldReady);

    async Task<CommandResult> MenuAsync(GameConnectionContext context, Packet request)
    {
        if (!CharacterRequests.TryPeerMenu(request.Body, out ushort target)) return CommandResult.Unhandled;
        var identity = online.PublicCharacter(target);
        if (identity is null || identity.Channel != context.Channel || context.Session.CaptureSocialActor() is not { } actor)
            return CommandResult.Handled;
        await context.Notify(NotiPacketType.REQUEST_PEER, TownPackets.PeerMenu(target, identity.PartyId), "player_menu",
            () => actor.IsCurrent() && identity.IsUnchanged());
        return CommandResult.Handled;
    }
}
