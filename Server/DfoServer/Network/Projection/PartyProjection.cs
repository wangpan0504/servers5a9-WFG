using DfoServer.Game.Party;
using DfoServer.Game.Session;
using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

internal static class PartyProjection
{
    public static void Notice(GameConnectionContext context, PartyNotice notice)
    {
        bool Guard() => notice.Recipient.Actor.IsCurrent() && (notice.Guard?.Invoke() ?? true);
        void Post(NotiPacketType opcode, byte[] body, string action) => context.Sender.Post(0, (ushort)opcode, body, action, Guard);
        void Actor(PublicCharacterSnapshot identity) => Post(NotiPacketType.USERINFO,
            TownPackets.Actor(identity.Character, identity.Channel, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), identity.AdventureName,
                identity.Phase == SessionPhase.Town ? ActorSceneState.Town : ActorSceneState.Dungeon,
                identity.PremiumPcRoom), "party_actor");
        if (notice.Kind == PartyNoticeKind.Refused)
        {
            context.Sender.Post(1, (ushort)CmdPacketType.RESPONSE_PEER, PartyPackets.Failure(133), "party_refused", Guard);
            return;
        }
        if (notice.Kind is PartyNoticeKind.Invite or PartyNoticeKind.Accepted && notice.Peer is { } peer)
        {
            Actor(peer.Identity);
            Post(notice.Kind == PartyNoticeKind.Invite ? NotiPacketType.REQUEST_PEER : NotiPacketType.RESPONSE_PEER,
                PartyPackets.Peer(peer.Actor.Character.Id, 0, notice.PeerId, notice.Kind == PartyNoticeKind.Invite), "party_peer");
        }
        else if (notice.Party is { } party)
        {
            var channel = party.Members[0].Identity.Channel;
            var members = party.Members.Select(m => (m.Slot, m.Actor.Character.Id)).ToArray();
            byte[] Realtime() => PartyPackets.Realtime(party.Members.Select(m => (m.Actor.Character.Id, m.Slot)).ToArray());
            if (notice.Kind == PartyNoticeKind.SceneChanged)
            {
                // 只恢复切区者的队伍栏；重复Actor会替换客户端成员引用并触发入队音效。
                Post(NotiPacketType.PARTY_INFO, PartyPackets.Information(party.Id, channel.ServerIndex,
                    checked((byte)channel.Number), members, party.Leader, false, party.Settings), "party_scene_info");
                Post(NotiPacketType.PARTY_MEMBER_REALTIME_INFO, Realtime(), "party_scene_realtime");
                return;
            }
            if (notice.Kind == PartyNoticeKind.LeaderChanged)
            {
                Post(NotiPacketType.UDP_HOST, PartyPackets.Host(party.Leader), "party_host");
                Post(NotiPacketType.PARTY_INFO, PartyPackets.Members(party.Id, channel.ServerIndex,
                    checked((byte)channel.Number), members, party.Leader, party.Settings), "party_leader_info");
                Post(NotiPacketType.PARTY_MEMBER_REALTIME_INFO, Realtime(), "party_leader_realtime");
                return;
            }
            if (notice.Kind == PartyNoticeKind.Updated && notice.Recipient.Actor.Session.PartyDungeon is not null)
            {
                // 副本中离队只更新名册，不能重建幸存者Actor或用城镇100%覆盖战斗血蓝。
                Post(NotiPacketType.PARTY_INFO, PartyPackets.Members(party.Id, channel.ServerIndex,
                    checked((byte)channel.Number), members, party.Leader, party.Settings), "party_dungeon_members");
                Post(NotiPacketType.UDP_HOST, PartyPackets.Host(party.Leader), "party_dungeon_members_host");
                return;
            }
            if (notice.Kind == PartyNoticeKind.Updated)
            {
                foreach (var member in party.Members)
                    if (member.Actor.Session.PublicCharacter(member.Actor.Character.Id) is { } current) Actor(current);
                Post(NotiPacketType.PARTY_MEMBER_REALTIME_INFO,
                    PartyPackets.Realtime(party.Members.Select(m => (m.Actor.Character.Id, m.Slot)).ToArray()), "party_realtime");
            }
            byte[] Endpoints() => PartyPackets.Endpoints(party.Members.Where(m => m.Actor.Session.PartyEndpoint is not null)
                .Select(m => new PartyEndpointRow(m.Actor.Character.Id, m.Actor.Session.PartyEndpoint!, m.Actor.Session.PeerAccount)).ToArray());
            if (notice.Kind is PartyNoticeKind.Updated or PartyNoticeKind.Endpoints)
            {
                Post(NotiPacketType.USER_UDP_IP_PORT, Endpoints(), "party_endpoints");
            }
            if (notice.Kind == PartyNoticeKind.Endpoints) return;
            Post(NotiPacketType.PARTY_INFO, PartyPackets.Information(party.Id, channel.ServerIndex, checked((byte)channel.Number),
                party.Members.Select(m => (m.Slot, m.Actor.Character.Id)).ToArray(), party.Leader,
                notice.Kind == PartyNoticeKind.Removed, party.Settings), "party_info");
            if (notice.Kind == PartyNoticeKind.Updated)
            {
                // 成员增减后的名册可替换成员对象，端点和血条必须安装到最终对象。
                Post(NotiPacketType.USER_UDP_IP_PORT, Endpoints(), "party_endpoints_after_roster");
                Post(NotiPacketType.PARTY_MEMBER_REALTIME_INFO,
                    PartyPackets.Realtime(party.Members.Select(m => (m.Actor.Character.Id, m.Slot)).ToArray()), "party_realtime_after_roster");
                Post(NotiPacketType.UDP_HOST, PartyPackets.Host(party.Leader), "party_host");
            }
        }
    }
}
