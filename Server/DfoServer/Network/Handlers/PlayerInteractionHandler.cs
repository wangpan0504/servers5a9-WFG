using System.Buffers.Binary;
using DfoServer.Game.Party;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Network.Protocol;
using DfoServer.Network.Projection;

namespace DfoServer.Network.Handlers;

internal sealed class PlayerInteractionHandler(PartyService parties, OnlineSessionDirectory online, ServerLog log) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands)
    {
        commands.Register(CmdPacketType.SET_UDP_IP_PORT, EndpointAsync, nameof(PlayerInteractionHandler));
        commands.Register(CmdPacketType.REMOTE_PEER_PACKET, DirectoryAsync, nameof(PlayerInteractionHandler), UserStateRequirement.WorldReady);
        commands.Register(CmdPacketType.REQUEST_PEER, RequestAsync, nameof(PlayerInteractionHandler), UserStateRequirement.WorldReady);
        commands.Register(CmdPacketType.RESPONSE_PEER, ResponseAsync, nameof(PlayerInteractionHandler), UserStateRequirement.WorldReady);
        commands.Register(CmdPacketType.SET_PARTY_INFO, InformationAsync, nameof(PlayerInteractionHandler), UserStateRequirement.WorldReady);
        commands.Register(CmdPacketType.LEAVE_PARTY, LeaveAsync, nameof(PlayerInteractionHandler), UserStateRequirement.WorldReady);
        commands.Register(CmdPacketType.WALKOUT_PARTY_MEMBER, LeaveAsync, nameof(PlayerInteractionHandler), UserStateRequirement.WorldReady);
        commands.Register(CmdPacketType.CHANGE_HOST, TransferLeaderAsync, nameof(PlayerInteractionHandler), UserStateRequirement.WorldReady);
    }
    Task<CommandResult> DirectoryAsync(GameConnectionContext context, Packet request)
    {
        if (request.Body.Length < 3 || request.Body[2] != 1 || context.PartyDirectory is null)
            return Task.FromResult(CommandResult.Unhandled);
        var query = PartyRequests.Directory(request.Body);
        // 路由头不是寻址授权；只能查询本连接当前角色的目录。
        if (context.Session.Selected?.Id != query.Actor || context.Session.Phase != SessionPhase.Town)
            return Task.FromResult(CommandResult.Handled);
        context.PartyDirectory.Query(query);
        return Task.FromResult(CommandResult.Handled);
    }
    async Task<CommandResult> InformationAsync(GameConnectionContext context, Packet request)
    {
        var (operation, settings) = PartyRequests.Information(request.Body);
        var result = parties.SetInformation(context.Session, operation, settings, context.BlocksTownActions);
        log.Write(result.Accepted ? "party_information_updated" : "party_rejected", new { Connection = context.Id,
            Operation = operation, settings.Capacity, settings.Dungeon, result.Error });
        if (!result.Accepted)
            await context.Reply(CmdPacketType.SET_PARTY_INFO, SocialPackets.Ack(result.Error), "party_information_rejected");
        else parties.Publish(result.Notices);
        return CommandResult.Handled;
    }
    Task<CommandResult> EndpointAsync(GameConnectionContext context, Packet request)
    {
        var endpoint = PartyRequests.Endpoint(request.Body);
        context.Session.SetPartyEndpoint(endpoint);
        log.Write("party_endpoint", new { Connection = context.Id, endpoint.Nat, endpoint.Port, endpoint.Mtu });
        parties.Publish(parties.EndpointChanged(context.Session));
        return Task.FromResult(CommandResult.Handled);
    }
    async Task<CommandResult> RequestAsync(GameConnectionContext context, Packet request)
    {
        if (CharacterRequests.TryPeerMenu(request.Body, out ushort target))
        {
            var identity = online.PublicCharacter(target);
            if (identity is null || identity.Channel != context.Channel || context.Session.CaptureSocialActor() is not { } actor)
                return CommandResult.Handled;
            ushort partyId = parties.PartyId(target);
            await context.Notify(NotiPacketType.REQUEST_PEER, TownPackets.PeerMenu(target, partyId), "player_menu",
                () => actor.IsCurrent() && identity.IsUnchanged() && parties.PartyId(target) == partyId);
            return CommandResult.Handled;
        }
        var body = request.Body;
        if (body.Length < 3 || body[2] != 0) return CommandResult.Unhandled;
        if (body.Length != 13) throw new InvalidDataException("组队邀请请求长度错误");
        target = BinaryPrimitives.ReadUInt16LittleEndian(body);
        uint peer = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(3));
        var result = online.FindReadyCharacter(target) is { } other ? parties.Invite(context.Session, other, peer, context.BlocksTownActions) : new PartyResult(3, []);
        log.Write(result.Accepted ? "party_invited" : "party_rejected", new { Connection = context.Id, Target = target, result.Error });
        var replied = context.Reply(CmdPacketType.REQUEST_PEER, result.Accepted ? [1] : PartyPackets.Failure(result.Error), "party_invite_result");
        parties.Publish(result.Notices);
        await replied;
        return CommandResult.Handled;
    }
    async Task<CommandResult> ResponseAsync(GameConnectionContext context, Packet request)
    {
        var body = request.Body;
        if (body.Length < 3 || body[2] != 0) return CommandResult.Unhandled;
        if (body.Length != 7 && (body.Length != 9 || BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(7)) != 0x85))
            throw new InvalidDataException("组队回应长度或拒绝标记错误");
        ushort target = BinaryPrimitives.ReadUInt16LittleEndian(body);
        var result = parties.Respond(context.Session, target, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(3)), body.Length == 7, context.BlocksTownActions);
        log.Write(result.Accepted ? "party_responded" : "party_rejected", new { Connection = context.Id, Target = target,
            Accept = body.Length == 7, result.Error });
        var replied = body.Length == 7 || !result.Accepted
            ? context.Reply(CmdPacketType.RESPONSE_PEER, result.Accepted ? PartyPackets.Response(target) : PartyPackets.Failure(result.Error), "party_response_result")
            : Task.CompletedTask;
        parties.Publish(result.Notices);
        await replied;
        return CommandResult.Handled;
    }
    async Task<CommandResult> TransferLeaderAsync(GameConnectionContext context, Packet request)
    {
        byte slot = PartyRequests.Leader(request.Body);
        var result = parties.TransferLeader(context.Session, slot, context.BlocksTownActions);
        log.Write(result.Accepted ? "party_leader_changed" : "party_rejected", new { Connection = context.Id,
            request.Opcode, TargetSlot = slot, result.Error });
        // ACK121成功分支已确认；失败提示的业务含义尚未确认，不借用其它命令错误码。
        if (!result.Accepted) return CommandResult.Handled;
        var replied = context.Reply(CmdPacketType.CHANGE_HOST, [1], "party_leader_result");
        parties.Publish(result.Notices);
        await replied;
        return CommandResult.Handled;
    }
    async Task<CommandResult> LeaveAsync(GameConnectionContext context, Packet request)
    {
        bool kick = request.Opcode == (ushort)CmdPacketType.WALKOUT_PARTY_MEMBER;
        if (request.Body.Length != (kick ? 1 : 0)) throw new InvalidDataException("离队请求长度错误");
        var result = parties.Leave(context.Session, kick ? request.Body[0] : null, context.BlocksTownActions);
        log.Write(result.Accepted ? "party_left" : "party_rejected", new { Connection = context.Id, request.Opcode, result.Error });
        var replied = context.Reply((CmdPacketType)request.Opcode, SocialPackets.Ack(result.Error), "party_leave_result");
        parties.Publish(result.Notices);
        await replied;
        return CommandResult.Handled;
    }
}
