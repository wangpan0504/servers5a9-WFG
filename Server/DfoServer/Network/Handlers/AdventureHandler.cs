using DfoServer.Game.Session;
using DfoServer.Game.Adventure;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Network.Projection;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

internal sealed partial class AdventureHandler(AdventureProjection projection, AdventureRepresentativeService representatives) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands)
    {
        commands.Register(CmdPacketType.REQUEST_ADVENTURE_INFO, Information, nameof(AdventureHandler));
        commands.Register(CmdPacketType.GET_REPRESENT_CHARAC_JOB, Candidates, nameof(AdventureHandler));
        commands.Register(CmdPacketType.SET_REPRESENT_CHARAC_JOB, Set, nameof(AdventureHandler));
        commands.Register(CmdPacketType.REMOVE_REPRESENT_CHARAC_JOB, Remove, nameof(AdventureHandler));
    }

    async Task<CommandResult> Information(GameConnectionContext context, Packet packet)
    {
        ushort target = AdventureRequests.Target(packet.Body);
        bool selection = context.Session.CheckState(UserState.CharacterSelect);
        bool ownActor = context.Session.Ready && context.Session.Selected is { } selected && (target == 0 || target == selected.Id);
        if (!(selection && target == 0 || ownActor))
            await context.Reply(CmdPacketType.REQUEST_ADVENTURE_INFO, AdventurePackets.Ack(3), "adventure_information_rejected");
        else await projection.InformationAsync(context, ack: true);
        return CommandResult.Handled;
    }
}
