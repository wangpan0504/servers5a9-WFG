using DfoServer.Game.Characters;
using DfoServer.Game.Raids;
using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

internal static class RaidProjection
{
    public static void Directory(GameConnectionContext context, RaidDirectoryChange change, RaidActor recipient)
    {
        bool Guard() => recipient.Session.CanPublishRaidDirectory(recipient, change);
        if (change.Party is { } party)
        {
            if (change.Removed)
                context.Sender.Post(0, (ushort)NotiPacketType.RAID_MODIFY, RaidPackets.RemoveRecord(party.Key), "raid_record_removed", Guard);
            else
            {
                context.Sender.Post(0, (ushort)NotiPacketType.RAID_LIST, List(party), "raid_recruiting_record", Guard);
                context.Sender.Post(0, (ushort)NotiPacketType.RAID_MODIFY, Record(party), "raid_recruiting_updated", Guard);
            }
        }
        else if (change.Waiting is { } row)
        {
            context.Sender.Post(0, (ushort)NotiPacketType.RAID_WAITING_MODIFY,
                RaidPackets.WaitingChanged(row.Actor.Character, row.Type, !change.Removed), "raid_waiting_updated", Guard);
            // 完整快照带版本守卫；并发登记使旧快照失效时，由最新变更携带完整列表，避免覆盖较新的增量。
            var rows = recipient.Session.RaidWaitingSnapshot(recipient, row.Type);
            context.Sender.Post(0, (ushort)NotiPacketType.RAID_WAITING_LIST,
                RaidPackets.WaitingList(rows.Select(w => (w.Actor.Character, w.Type)).ToArray()), "raid_waiting_snapshot",
                () => recipient.Session.CanPublishRaidWaiting(recipient, row.Type, rows));
        }
    }
    public static (ushort Character, byte[] Name, byte Squad)[] Members(RaidParty party) =>
        party.Members.OrderBy(m => m.Slot).Select(m => (m.Actor.Character, CharacterName.Encoding.GetBytes(m.Actor.Name), m.Squad)).ToArray();
    public static byte[] Record(RaidParty party) => RaidPackets.UpdateRecord(party.Key, party.TitleBytes, party.Type,
        party.Leader, CharacterName.Encoding.GetBytes(party.LeaderName), party.Members.Single(m => m.Actor.Character == party.Leader).Squad);
    public static byte[] List(RaidParty party) => RaidPackets.RaidList(party.Key, party.TitleBytes, party.Type,
        party.Leader, CharacterName.Encoding.GetBytes(party.LeaderName), party.Members.Single(m => m.Actor.Character == party.Leader).Squad);

    public static void Notice(GameConnectionContext context, RaidChange change, RaidActor recipient)
    {
        if (change.Removed is { } removed && ReferenceEquals(removed.Session, recipient.Session) && removed.Epoch == recipient.Epoch)
        {
            // 本人退出由Handler回复；强退通知不能在重建攻坚队后清掉新窗口。
            if (change.Kicked)
                context.Sender.Post(1, (ushort)CmdPacketType.LEAVE_RAID, [1, 1], "raid_kicked",
                    () => recipient.IsCurrent && !recipient.Session.HasCurrentRaid(recipient));
            return;
        }
        if (change.After is not { } after) return;
        bool Guard() => recipient.Session.CanPublishRaid(recipient, after);
        // 加入只由双账号提交结束后的招募处理器发布；清理/继任不在owner锁内读取其它成员。
        if (after.Members.Length > change.Before.Members.Length)
        {
            bool existing = change.Before.Members.Any(m => ReferenceEquals(m.Actor.Session, recipient.Session) && m.Actor.Epoch == recipient.Epoch);
            foreach (var member in after.Members.Where(m => !ReferenceEquals(m.Actor.Session, recipient.Session) &&
                (!existing || !change.Before.Members.Any(old => ReferenceEquals(old.Actor.Session, m.Actor.Session) && old.Actor.Epoch == m.Actor.Epoch))))
                if (member.Actor.Session.PublicCharacter(member.Actor.Character) is { } identity)
                    context.Sender.Post(0, (ushort)NotiPacketType.USERINFO,
                        TownPackets.Actor(identity.Character, identity.Channel, adventureName: identity.AdventureName,
                            premiumPcRoom: identity.PremiumPcRoom), "raid_member_actor",
                        // 编队可使旧成员快照失效，但仍在同团的角色身份不能一起丢弃。
                        () => recipient.Session.HasRaidKey(recipient, after.Key) &&
                            member.Actor.Session.HasRaidKey(member.Actor, after.Key) && identity.IsCurrent());
        }
        // 只入原连接的有界发送队列，慢接收者不阻塞提交者或其它成员。
        context.Sender.Post(0, (ushort)NotiPacketType.RAID_LIST, List(after), "raid_member_record", Guard);
        context.Sender.Post(0, (ushort)NotiPacketType.RAID_MODIFY, Record(after), "raid_member_record_updated", Guard);
        context.Sender.Post(0, (ushort)NotiPacketType.RAID_MODIFY,
            RaidPackets.OpenWithSquads(after.Key, Members(after)), "raid_members_updated", Guard);
    }

    public static void Peer(GameConnectionContext context, RaidActor recipient, RaidActor peer, uint peerId, bool invite)
    {
        if (peer.Session.PublicCharacter(peer.Character) is not { } identity) return;
        bool Guard() => recipient.IsCurrent && peer.IsCurrent && recipient.Session.CanChangeTownParty && peer.Session.CanChangeTownParty;
        context.Sender.Post(0, (ushort)NotiPacketType.USERINFO,
            TownPackets.Actor(identity.Character, identity.Channel, adventureName: identity.AdventureName,
                premiumPcRoom: identity.PremiumPcRoom), "raid_peer_actor", Guard);
        context.Sender.Post(0, (ushort)(invite ? NotiPacketType.REQUEST_PEER : NotiPacketType.RESPONSE_PEER),
            RaidPackets.Peer(peer.Character, peerId, invite), "raid_peer", Guard);
    }
}
