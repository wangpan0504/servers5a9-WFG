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
            if (notice.Kind is PartyNoticeKind.Updated or PartyNoticeKind.SceneChanged)
            {
                foreach (var member in party.Members)
                    if (member.Actor.Session.PublicCharacter(member.Actor.Character.Id) is { } current) Actor(current);
                Post(NotiPacketType.PARTY_MEMBER_REALTIME_INFO,
                    PartyPackets.Realtime(party.Members.Select(m => (m.Actor.Character.Id, m.Slot)).ToArray()), "party_realtime");
            }
            byte[] Endpoints() => PartyPackets.Endpoints(party.Members.Where(m => m.Actor.Session.PartyEndpoint is not null)
                .Select(m => new PartyEndpointRow(m.Actor.Character.Id, m.Actor.Session.PartyEndpoint!, m.Actor.Session.PeerAccount)).ToArray());
            if (notice.Kind is PartyNoticeKind.Updated or PartyNoticeKind.Endpoints or PartyNoticeKind.SceneChanged)
            {
                Post(NotiPacketType.USER_UDP_IP_PORT, Endpoints(), "party_endpoints");
            }
            if (notice.Kind == PartyNoticeKind.Endpoints) return;
            var channel = party.Members[0].Identity.Channel;
            Post(NotiPacketType.PARTY_INFO, PartyPackets.Information(party.Id, channel.ServerIndex, checked((byte)channel.Number),
                party.Members.Select(m => (m.Slot, m.Actor.Character.Id)).ToArray(), party.Leader,
                notice.Kind == PartyNoticeKind.Removed, party.Settings), "party_info");
            if (notice.Kind is PartyNoticeKind.Updated or PartyNoticeKind.SceneChanged)
            {
                // 名册可替换成员对象，端点和血条必须安装到最终对象；切区同样执行完整恢复。
                Post(NotiPacketType.USER_UDP_IP_PORT, Endpoints(), "party_endpoints_after_roster");
                Post(NotiPacketType.PARTY_MEMBER_REALTIME_INFO,
                    PartyPackets.Realtime(party.Members.Select(m => (m.Actor.Character.Id, m.Slot)).ToArray()), "party_realtime_after_roster");
                Post(NotiPacketType.UDP_HOST, PartyPackets.Host(party.Leader), "party_host");
            }
        }
    }
}
