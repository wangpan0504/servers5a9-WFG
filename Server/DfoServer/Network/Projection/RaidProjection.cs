using DfoServer.Game.Characters;
using DfoServer.Game.Raids;
using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

internal static class RaidProjection
{
    public static void Directory(GameConnectionContext context, RaidDirectoryChange change, RaidActor recipient, bool enableLuke = false)
    {
        bool Guard() => recipient.Session.CanPublishRaidDirectory(recipient, change);
        if (change.Party is { } party)
        {
            if (change.Removed || party.Started is not null)
                context.Sender.Post(0, (ushort)NotiPacketType.RAID_MODIFY, RaidPackets.RemoveRecord(party.Key), "raid_record_removed", Guard);
            else
            {
                context.Sender.Post(0, (ushort)NotiPacketType.RAID_LIST, List(party, enableLuke), "raid_recruiting_record", Guard);
                context.Sender.Post(0, (ushort)NotiPacketType.RAID_MODIFY, Record(party, enableLuke), "raid_recruiting_updated", Guard);
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
    internal static IReadOnlyDictionary<ushort, byte>? ClearCounts(RaidParty party)
    {
        if (party.Started is not { } started) return null;
        foreach (var member in party.Members)
            if (member.Actor.IsCurrent && member.Actor.Session.CurrentRaidBattle(member.Actor) is { } battle &&
                battle.Instance == started.InstanceId) return battle.RosterClears();
        return null;
    }
    public static byte[] Record(RaidParty party, bool enableLuke = false) => RaidPackets.UpdateRecord(party.Key, party.TitleBytes, party.Type,
        party.Leader, CharacterName.Encoding.GetBytes(party.LeaderName), party.Members.Single(m => m.Actor.Character == party.Leader).Squad,
        party.Started is null ? (byte)0 : (byte)2, party.Started?.Phase ?? 0, CanStart(party, enableLuke), party.Guide);
    public static byte[] List(RaidParty party, bool enableLuke = false) => RaidPackets.RaidList(party.Key, party.TitleBytes, party.Type,
        party.Leader, CharacterName.Encoding.GetBytes(party.LeaderName), party.Members.Single(m => m.Actor.Character == party.Leader).Squad,
        party.Started is null ? (byte)0 : (byte)2, party.Started?.Phase ?? 0, CanStart(party, enableLuke), party.Guide);

    internal static bool CanStart(RaidParty party, bool enableLuke = false) => (party.Type is 0 or 2 || party.Type == 1 && enableLuke || party.Type == 3 && party.Guide) && party.Started is null &&
        party.Members.Length >= party.StartMinimum && party.Members.All(m => (party.Type == 2 || m.Squad != 0) && m.Actor.IsCurrent);

    public static void Cost(GameConnectionContext context, RaidCostStatus[] rows, RaidActor recipient, Func<bool>? current = null) =>
        context.Sender.Post(0, (ushort)NotiPacketType.RAID_ENTRY_COST_INFO, RaidPackets.EntryCosts(rows), "raid_entry_cost_snapshot",
            () => recipient.IsCurrent && (current?.Invoke() ?? true));

    public static void Started(GameConnectionContext context, TownProjection town, RaidStartReceipt receipt, RaidActor recipient)
    {
        var party = receipt.Party;
        int index = Enumerable.Range(0, receipt.Reservations.Length).Single(i => receipt.Reservations[i].Actor.Character == recipient.Character);
        var character = receipt.Characters[index];
        bool Guard() => recipient.IsCurrent && recipient.Session.StartedRaid?.InstanceId == party.Started!.InstanceId &&
            (party.Type != (byte)RaidKind.Luke || recipient.Session.CanChangeTownParty &&
                recipient.Session.CanPublishRaid(recipient, party) && ReferenceEquals(recipient.Session.Selected, character) &&
                receipt.Battle is { } battle && recipient.Session.CanPublishRaidBattle(recipient, battle));
        bool ready = recipient.Owner.Execute(() =>
        {
            if (!Guard()) return false;
            // 清场先入队，其后的实际提交帧不因同次迁移递增发送epoch而丢弃。
            town.QueueRaidDeparture(context, character, Guard);
            return true;
        });
        if (!ready) return;
        var plan = receipt.Reservations[index].Inventory;
        context.Sender.Post(0, (ushort)(receipt.Resync ? NotiPacketType.ITEM_LIST : NotiPacketType.UPDATE_ITEM_LIST),
            receipt.Resync ? Builders.Inventory.InventoryPackets.Full(character.Inventory) :
                Builders.Inventory.InventoryPackets.Delta(character.Inventory, Game.Inventory.Models.InventoryListType.Main, plan.ChangedSlots.ToArray()),
            receipt.Resync ? "raid_start_inventory_resync" : "raid_start_cost_committed", Guard);
        context.Sender.Post(0, (ushort)NotiPacketType.RAID_MODIFY,
            RaidPackets.UpdateRecord(party.Key, party.TitleBytes, party.Type, party.Leader, CharacterName.Encoding.GetBytes(party.LeaderName),
                party.Members.Single(m => m.Actor.Character == party.Leader).Squad, guide: party.Guide), "raid_start_record", Guard);
        foreach (var notice in receipt.Squads.Where(n => ReferenceEquals(n.Recipient.Actor.Session, recipient.Session)))
        {
            var original = notice.Guard;
            PartyProjection.Notice(context, party.Type == (byte)RaidKind.Luke
                ? notice with { Guard = () => Guard() && (original?.Invoke() ?? true) } : notice);
        }
        var rows = receipt.Characters.Select(c => new RaidCostStatus(c.Id, 2, (short)Math.Min(short.MaxValue,
            c.Inventory.Items.Where(i => i.Position.List == Game.Inventory.Models.InventoryListType.Main &&
                i.Core.ItemId == receipt.Cost.ItemId).Sum(i => (long)i.Core.Count)))).ToArray();
        Cost(context, rows, recipient, party.Type == (byte)RaidKind.Luke ? Guard : null);
        // 594无载荷，先安装同分队普通Party；其后11/12按同一普通接收器幂等确认。
        context.Sender.Post(0, (ushort)(party.Type == 2 ? NotiPacketType.PREPARE_START_FIEND_WAR : NotiPacketType.PREPARE_START_RAID), [], "raid_prepare", Guard);
        recipient.Owner.Execute(() => { if (Guard()) town.QueueRaidArrival(context, character, Guard); return true; });
        if (party.Type == 2 && receipt.Battle is { } fiendwarInitial && recipient.Session.CurrentRaidBattle(recipient) is { } fiendwarBattle)
        {
            context.Sender.Post(0, (ushort)NotiPacketType.START_FIEND_WAR, [], "fiendwar_started", Guard,
                () => context.FiendwarTimers = context.FiendwarTimers.Clear());
            RaidCombatProjection.PostFiendwarSnapshot(context, fiendwarBattle, fiendwarInitial,
                () => Guard() && recipient.Session.CanPublishRaidBattle(recipient, fiendwarInitial));
        }
        if (party.Type == (byte)RaidKind.Luke && receipt.Battle is { } initial &&
            recipient.Session.CurrentRaidBattle(recipient) is { } currentBattle)
            RaidCombatProjection.PostLukeSnapshot(context, currentBattle, initial, Guard);
        if (party.Type == (byte)RaidKind.Prey && receipt.Battle is { } preyInitial &&
            recipient.Session.CurrentRaidBattle(recipient) is { } preyBattle)
        {
            context.Sender.Post(0, (ushort)NotiPacketType.PREY_RAID, RaidCombatPackets.PreyStatus(1), "prey_started", Guard);
            RaidCombatProjection.PostPreySnapshot(context, preyBattle, preyInitial,
                () => Guard() && recipient.Session.CanPublishRaidBattle(recipient, preyInitial));
        }
        if (party.Type == (byte)RaidKind.Anton && receipt.Battle is { } antonInitial &&
            recipient.Session.CurrentRaidBattle(recipient) is { } antonBattle)
            RaidCombatProjection.PostAntonSnapshot(context, antonBattle, antonInitial,
                () => Guard() && recipient.Session.CanPublishRaidBattle(recipient, antonInitial));
        if (receipt.Battle is { } battle)
            RaidCombatProjection.PostState(context, battle, recipient, Guard, "raid_started_state");
        else context.Sender.Post(0, (ushort)NotiPacketType.RAID_STATE,
            receipt.Battle is { } anton ? RaidCombatPackets.State(anton) : RaidPackets.Started(party.Started!.Phase), "raid_started_state", Guard);
    }

    public static void Notice(GameConnectionContext context, RaidChange change, RaidActor recipient, bool enableLuke = false, TownProjection? town = null)
    {
        if (change.Removed is { } removed && ReferenceEquals(removed.Session, recipient.Session) && removed.Epoch == recipient.Epoch)
        {
            // 本人退出由Handler回复；强退通知不能在重建攻坚队后清掉新窗口。
            if (change.Kicked)
                context.Sender.Post(1, (ushort)CmdPacketType.LEAVE_RAID, [1, 1], "raid_kicked",
                    () => recipient.IsCurrent && !recipient.Session.HasCurrentRaid(recipient));
            if (change.TownReturn is { } receipt && town is not null)
            {
                bool Current() => recipient.IsCurrent && !recipient.Session.HasCurrentRaid(recipient) &&
                    ReferenceEquals(recipient.Session.Selected, receipt.Character);
                town.QueueRaidDeparture(context, receipt.Character, Current);
                town.QueueRaidArrival(context, receipt.Character, Current);
            }
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
                        TownPackets.Actor(identity.Character, identity.PvpGrade, identity.Channel, adventureName: identity.AdventureName,
                            premiumPcRoom: identity.PremiumPcRoom), "raid_member_actor",
                        // 编队可使旧成员快照失效，但仍在同团的角色身份不能一起丢弃。
                        () => recipient.Session.HasRaidKey(recipient, after.Key) &&
                            member.Actor.Session.HasRaidKey(member.Actor, after.Key) && identity.IsCurrent());
        }
        // 只入原连接的有界发送队列，慢接收者不阻塞提交者或其它成员。
        context.Sender.Post(0, (ushort)NotiPacketType.RAID_LIST, List(after, enableLuke), "raid_member_record", Guard);
        context.Sender.Post(0, (ushort)NotiPacketType.RAID_MODIFY, Record(after, enableLuke), "raid_member_record_updated", Guard);
        context.Sender.Post(0, (ushort)NotiPacketType.RAID_MODIFY,
            RaidPackets.OpenWithSquads(after.Key, Members(after), ClearCounts(after)), "raid_members_updated", Guard);
    }

    public static void Peer(GameConnectionContext context, RaidActor recipient, RaidActor peer, uint peerId, bool invite)
    {
        if (peer.Session.PublicCharacter(peer.Character) is not { } identity) return;
        bool Guard() => recipient.IsCurrent && peer.IsCurrent && recipient.Session.CanChangeTownParty && peer.Session.CanChangeTownParty;
        context.Sender.Post(0, (ushort)NotiPacketType.USERINFO,
            TownPackets.Actor(identity.Character, identity.PvpGrade, identity.Channel, adventureName: identity.AdventureName,
                premiumPcRoom: identity.PremiumPcRoom), "raid_peer_actor", Guard);
        context.Sender.Post(0, (ushort)(invite ? NotiPacketType.REQUEST_PEER : NotiPacketType.RESPONSE_PEER),
            RaidPackets.Peer(peer.Character, peerId, invite), "raid_peer", Guard);
    }
}
