using DfoServer.Infrastructure;
using DfoServer.Network.Parsers;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

internal sealed class PcRoomTimePointHandler(ServerLog log) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands) => commands.Register(
        CmdPacketType.GET_PCROOM_TIME_POINT_ITEM, HandleAsync, nameof(PcRoomTimePointHandler));

    async Task<CommandResult> HandleAsync(GameConnectionContext context, Packet request)
    {
        if (!PcRoomTimePointRequests.TryParse(request.Body, out var selection))
        {
            log.Write("pcroom_timepoint_invalid_request", new { Connection = context.Id, request.Body.Length });
            return CommandResult.Handled;
        }
        if (context.PcRoomTimePoint is { } onlineRewards)
            await onlineRewards.UseAsync(selection.Type, selection.Index);
        return CommandResult.Handled;
    }
}
