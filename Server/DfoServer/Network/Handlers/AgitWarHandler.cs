using DfoServer.Network.Protocol;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;

namespace DfoServer.Network.Handlers;

internal sealed class AgitWarHandler : IGameCommandModule
{
    public void Register(GameCommandRegistry commands) => commands.Register(CmdPacketType.AGIT_WAR_INFO, InformationAsync, nameof(AgitWarHandler));
    static async Task<CommandResult> InformationAsync(GameConnectionContext context, Packet request)
    {
        if (context.Session.Selected is not { } character || !AgitWarRequests.TryQueryMode(request.Body, out uint mode))
            return CommandResult.Unhandled;
        uint guildId = character.Guild?.Id ?? 0u;
        await context.Reply(CmdPacketType.AGIT_WAR_INFO, AgitWarPackets.Information(mode, guildId), "scene_event_ack");
        return CommandResult.Handled;
    }
}
