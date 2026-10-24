using DfoServer.Game.Session;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

internal sealed partial class CharacterHandler
{
    async Task<CommandResult> OtherPlayerAsync(GameConnectionContext context, Packet request)
    {
        if (!CharacterRequests.TryOtherPlayer(request.Body, out ushort target)) return CommandResult.Unhandled;
        var source = context.Session.OnlineCharacter();
        long epoch = context.Session.SelectionEpoch;
        if (source is not { Ready: true } || online is null) return CommandResult.Handled;
        var other = online.PublicCharacter(target);
        if (other is null) return CommandResult.Handled;
        bool Current() => context.Session.Ready && context.Session.SelectionEpoch == epoch &&
            context.Session.Selected?.Id == source.CharacterId && other.IsUnchanged();
        await projection.OtherPlayerAsync(context, other, Current);
        return CommandResult.Handled;
    }
}
