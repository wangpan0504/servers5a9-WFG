using DfoServer.Network.Parsers;
using DfoServer.Infrastructure;
using DfoServer.Network.Projection;
using DfoServer.Network.Protocol;
using DfoServer.Game.Session;
using DfoServer.Game.Town;

namespace DfoServer.Network.Handlers;

internal sealed class TownHandler(TownProjection projection, ServerLog log, TagEvildomProjection? tagEvildom = null,
    DfoServer.Game.Raids.FiendwarDungeonService? fiendwar = null,
    Func<GameConnectionContext, AreaRequest, Task<bool>>? fiendwarEncounter = null) : IGameCommandModule
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
        if (fiendwarEncounter is not null && await fiendwarEncounter(context, requested))
            return CommandResult.Handled;
        bool accepted = session.Travel(requested, out var receipt);
        log.Write("travel", new { Connection = context.Id, Accepted = accepted, Reason = session.TravelReason, Requested = requested, session.Selected?.Position, session.Selected?.Revision });
        if (accepted)
        {
            await projection.TravelAsync(context, receipt!);
            fiendwar?.Travelled(session);
            if (receipt!.BattlePassMissionAt is { } at)
            {
                try { await BattlePassMissionProjection.RefreshAsync(context, at); }
                catch (System.Data.Common.DbException error)
                {
                    // 已提交切图的后置读取失败，不能把原入场再次当成未提交命令重试。
                    throw new IOException("Committed guild hall entry publication failed", error);
                }
            }
        }
        else if (session.TravelReason is "guild-membership-required" or "evildom-war-party-required" or "luke-raid-required")
        {
            await context.Reply(CmdPacketType.SET_USER_AREA, [0, 1, 0, requested.Town, requested.Area], "town_admission_denied");
            if (session.TravelReason == "evildom-war-party-required")
                await context.Notify(NotiPacketType.AAS_TIPS, DfoServer.Network.Builders.ServerNoticePackets.Build(
                    "魔界大战", "请先创建或加入魔界大战队伍。"), "evildom_war_party_required");
            else if (session.TravelReason == "luke-raid-required")
                await context.Notify(NotiPacketType.AAS_TIPS, DfoServer.Network.Builders.ServerNoticePackets.Build(
                    "卢克攻坚队", "请先创建或加入卢克攻坚队。"), "luke_raid_required");
        }
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
