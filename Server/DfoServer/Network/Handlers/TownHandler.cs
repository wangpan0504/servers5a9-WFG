using DfoServer.Network.Parsers;
using DfoServer.Infrastructure;
using DfoServer.Network.Projection;
using DfoServer.Network.Protocol;
using DfoServer.Game.Session;

namespace DfoServer.Network.Handlers;

internal sealed class TownHandler(TownProjection projection, ServerLog log, TagEvildomProjection? tagEvildom = null) : IGameCommandModule
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
        bool position = request.Opcode == (ushort)CmdPacketType.SET_USER_POSITION;
        // 631 继续只处理原有坐标同步，不改变攻击/技能的朝向与广播合同。
        bool moved = session.Move(position ? movement : movement with { Movement = 0 });
        log.Write("position", new { Connection = context.Id, Accepted = moved, Character = session.Selected?.Id, session.Selected?.Position, session.Selected?.Revision });
        if (moved && (position || session.Selected!.Position != before))
            projection.Move(context, movement.ScaledSpeed);
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
        else if (session.TravelReason == "guild-membership-required")
            await context.Reply(CmdPacketType.SET_USER_AREA, [0, 1, 0, requested.Town, requested.Area], "guild_hall_admission_denied");
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
        {
            await projection.TravelAsync(context, receipt!);
            // 出房间会改位置。入城时记下的双角色标记对不上新位置，进图按钮仍要 1393。
            if (tagEvildom is not null && context.Session.ChannelEntry?.Type == 53 &&
                context.Session.ReadTagEvildom(tagEvildom.Store, tagEvildom.Now) is { } snapshot &&
                !snapshot.State.Configuration.Teams[snapshot.State.Configuration.SelectedTeam].Empty)
            {
                var primary = new TownCharacterProjection(receipt!.Character, tagEvildom.Now);
                await tagEvildom.DeliverAsync(context, snapshot, primarySource: primary);
            }
        }
        return CommandResult.Handled;
    }

    bool BlockTownAction(GameConnectionContext context, Packet request)
    {
        if (context.Session.CheckState(UserStateRequirement.WorldReady) && !context.BlocksTownActions)
            return false;
        // 活动拥有当前场景，不能把其坐标写入城镇；门禁由对应活动的生命周期解除。
        log.Write("town_command_blocked", new { Connection = context.Id, request.Opcode, context.Session.SelectionEpoch });
        return true;
    }
}
