using DfoServer.Game.Social;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Network.Projection;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

internal sealed class SocialHandler(ISocialStore store, SocialService service, OnlineSessionDirectory online, ServerLog log) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands)
    {
        foreach (var opcode in new[] { CmdPacketType.ADD_UNITED_SERVER_FRIEND, CmdPacketType.DELETE_UNITED_SERVER_FRIEND,
            CmdPacketType.REGISITER_TO_BLACKLIST, CmdPacketType.DELETE_TO_BLACKLIST, CmdPacketType.REQUEST_BLACKLIST,
            CmdPacketType.ACCOUNT_FRIEND_ADD_REQUEST, CmdPacketType.ACCOUNT_FRIEND_ACCEPT, CmdPacketType.ACCOUNT_FRIEND_DELETE,
            CmdPacketType.ACCOUNT_FRIEND_REFUSE_CANCEL, CmdPacketType.UPDATE_ACCOUNT_FRIEND_INFO,
            CmdPacketType.UPDATE_BLACK_USER_MEMO_INFO, CmdPacketType.ACCOUNT_FRIEND_REFRESH })
            commands.Register(opcode, HandleAsync, nameof(SocialHandler), UserStateRequirement.WorldReady);
    }
    async Task<CommandResult> HandleAsync(GameConnectionContext context, Packet packet)
    {
        if (context.Session.CaptureSocialActor() is not { } actor) return CommandResult.Handled;
        var opcode = (CmdPacketType)packet.Opcode;
        if (opcode == CmdPacketType.ACCOUNT_FRIEND_REFRESH)
        {
            if (packet.Body.Length != 0) throw new InvalidDataException("好友刷新请求必须为空");
            // 登录已安装默认分组；主动刷新时仍恢复同一组信息。
            await context.Notify(NotiPacketType.ACCOUNT_FRIEND_GROUP_INFO, SocialPackets.AccountGroups(), "account_friend_groups", actor.IsCurrent);
            service.Refresh(actor.Owner.Account);
            return CommandResult.Handled;
        }
        if (opcode == CmdPacketType.UPDATE_BLACK_USER_MEMO_INFO)
        {
            var memo = SocialRequests.BlacklistMemo(packet.Body);
            var updated = store.UpdateSocialMemo(actor, memo.Name, memo.Memo, true);
            log.Write(updated.Accepted ? "social_committed" : "social_rejected", new { Connection = context.Id,
                Character = actor.Character.Id, packet.Opcode, updated.Error });
            // 012B9F00 发送后直接更新本地备注；没有1538应答消费者，回放真实列表校正结果。
            await context.Reply(CmdPacketType.REQUEST_BLACKLIST,
                SocialPackets.Blacklist(store.CharacterLinks(actor.Character.Id, true), context.Channel.ServerIndex),
                "blacklist_memo", actor.IsCurrent);
            return CommandResult.Handled;
        }
        if (opcode == CmdPacketType.REQUEST_BLACKLIST)
        {
            if (packet.Body.Length != 0) return CommandResult.Unhandled;
            await context.Reply(opcode, SocialPackets.Blacklist(store.CharacterLinks(actor.Character.Id, true), context.Channel.ServerIndex),
                "blacklist", actor.IsCurrent);
            return CommandResult.Handled;
        }
        bool friend = opcode is CmdPacketType.ADD_UNITED_SERVER_FRIEND or CmdPacketType.DELETE_UNITED_SERVER_FRIEND;
        bool block = opcode is CmdPacketType.REGISITER_TO_BLACKLIST or CmdPacketType.DELETE_TO_BLACKLIST;
        bool add = opcode is CmdPacketType.ADD_UNITED_SERVER_FRIEND or CmdPacketType.REGISITER_TO_BLACKLIST;
        var request = friend ? SocialRequests.Character(packet.Body, add) : block ? SocialRequests.Blacklist(packet.Body, add)
            : SocialRequests.Account(packet.Body, packet.Opcode);
        SocialResult result;
        bool localServer = !friend && !block || request.Server == 0 || request.Server == context.Channel.ServerIndex;
        if (!localServer) result = new(block ? (ushort)76 : (ushort)21);
        else if (friend || block)
            result = store.ChangeCharacterLink(actor, request.Name, block, !add, request.Memo, DateTimeOffset.UtcNow);
        else if (opcode == CmdPacketType.ACCOUNT_FRIEND_ADD_REQUEST)
            result = store.RequestAccountFriend(actor, request.Name, request.Message, request.Group, request.Memo, DateTimeOffset.UtcNow);
        else if (opcode == CmdPacketType.UPDATE_ACCOUNT_FRIEND_INFO)
            result = store.UpdateSocialMemo(actor, request.Name, request.Memo, false, request.Mode);
        else
            result = store.ResolveAccountFriend(actor, request.Name, opcode == CmdPacketType.ACCOUNT_FRIEND_ACCEPT,
                opcode == CmdPacketType.ACCOUNT_FRIEND_DELETE, opcode == CmdPacketType.ACCOUNT_FRIEND_REFUSE_CANCEL && request.Mode == 5,
                request.Memo, request.Group);
        byte[] ack = SocialPackets.Ack(result.Error);
        if (result.Accepted)
        {
            if (block && add)
                ack = SocialPackets.BlockAdded(store.CharacterLinks(actor.Character.Id, true).Single(row => row.Person.Id == result.Person!.Id));
            else if (block) ack = SocialPackets.Build(w => { w.Write((byte)1); w.Write(1u); PacketEncoding.WriteString(w, request.Name); });
            else if (opcode == CmdPacketType.UPDATE_ACCOUNT_FRIEND_INFO)
                ack = SocialPackets.Build(w => { w.Write((byte)1); PacketEncoding.WriteString(w, request.Name);
                    PacketEncoding.WriteString(w, request.Memo); w.Write(request.Mode); });
            else if (!friend)
                ack = SocialPackets.AccountAck(request.Name, request.Group, request.Memo,
                    opcode is CmdPacketType.ACCOUNT_FRIEND_ADD_REQUEST or CmdPacketType.ACCOUNT_FRIEND_ACCEPT,
                    opcode == CmdPacketType.ACCOUNT_FRIEND_REFUSE_CANCEL ? request.Mode : null);
        }
        log.Write(result.Accepted ? "social_committed" : "social_rejected", new { Connection = context.Id, Character = actor.Character.Id,
            packet.Opcode, result.Error, Target = result.Person?.Id });
        await context.Reply(opcode, ack, "social_result", actor.IsCurrent);
        if (result.Accepted)
        {
            if (opcode == CmdPacketType.ACCOUNT_FRIEND_DELETE)
            {
                await context.Notify(NotiPacketType.ACCOUNT_FRIEND_DELETE, SocialPackets.Name(request.Name), "account_friend_deleted", actor.IsCurrent);
                if (result.Person is { } target && online.Find(target.Account) is { } peer && peer.CaptureSocialActor() is { } other)
                    other.Session.SocialAccountDeleted?.Invoke(context.Session.ReadAdventureName(), other.IsCurrent);
            }
            service.Refresh(actor.Owner.Account);
            if (!friend && !block && result.Person is { } p) service.Refresh(p.Account);
        }
        return CommandResult.Handled;
    }
}
