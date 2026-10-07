using DfoServer.Network.Parsers;
using DfoServer.Infrastructure;
using DfoServer.Network.Projection;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

internal sealed class TownHandler(TownProjection projection, ServerLog log) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands)
    {
        commands.Register(CmdPacketType.SET_USER_POSITION, MoveAsync, nameof(TownHandler));
        commands.Register(CmdPacketType.USER_STATE_MOTION, MoveAsync, nameof(TownHandler));
        commands.Register(CmdPacketType.SET_USER_AREA, TravelAsync, nameof(TownHandler));
        commands.Register(CmdPacketType.PREV_VILLAGE, LeaveRoomAsync, nameof(TownHandler));
    }

    Task<CommandResult> MoveAsync(GameConnectionContext context, Packet request)
    {
        var session = context.Session;
        var movement = TownRequests.Position(request.Opcode, request.Body);
        if (BlockTownAction(context, request))
            return Task.FromResult(CommandResult.Handled);
        var before = session.Selected?.Position;
        bool moved = session.Move(movement);
        log.Write("position", new { Connection = context.Id, Accepted = moved, Character = session.Selected?.Id, session.Selected?.Position, session.Selected?.Revision });
        if (moved && session.Selected!.Position != before)
            projection.Move(context);
        return Task.FromResult(CommandResult.Handled);
    }

    async Task<CommandResult> TravelAsync(GameConnectionContext context, Packet request)
    {
        var session = context.Session;
        var requested = TownRequests.Area(request.Body);
        if (BlockTownAction(context, request))
            return CommandResult.Handled;
        bool accepted = session.Travel(requested, out var receipt);
        log.Write("travel", new { Connection = context.Id, Accepted = accepted, Reason = session.TravelReason, Requested = requested, session.Selected?.Position, session.Selected?.Revision });
        if (accepted)
            await projection.TravelAsync(context, receipt!);
        return CommandResult.Handled;
    }

    async Task<CommandResult> LeaveRoomAsync(GameConnectionContext context, Packet request)
    {
        SessionRequests.Empty(request.Body);
        if (BlockTownAction(context, request))
            return CommandResult.Handled;
        var session = context.Session;
        bool accepted = session.LeaveRoom(out var receipt);
        log.Write("room_exit", new { Connection = context.Id, Accepted = accepted, Reason = session.TravelReason, session.Selected?.Position, session.Selected?.Revision });
        if (accepted)
            await projection.TravelAsync(context, receipt!);
        return CommandResult.Handled;
    }

    bool BlockTownAction(GameConnectionContext context, Packet request)
    {
        if (!context.BlocksTownActions)
            return false;
        // 活动拥有当前场景，不能把其坐标写入城镇；门禁由对应活动的生命周期解除。
        log.Write("town_command_blocked", new { Connection = context.Id, request.Opcode, context.Session.SelectionEpoch });
        return true;
    }
}
