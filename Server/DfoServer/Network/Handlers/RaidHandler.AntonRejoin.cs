using DfoServer.Game.Party;
using DfoServer.Game.Raids;
using DfoServer.Network.Builders;
using DfoServer.Network.Projection;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

internal sealed partial class RaidHandler
{
    async Task PublishAntonRejoinAsync(GameConnectionContext context, RaidActor actor, uint key, AntonRaidRecovery recovery)
    {
        var party = recovery.Party!;
        var raid = registry.Find(actor);
        if (raid is null || town is null || parties is null) throw new InvalidOperationException("安徒恩重返缺少正式场景依赖");
        bool Current() => actor.IsCurrent && context.Session.HasRaidKey(actor, key) &&
            context.Session.StartedRaid?.InstanceId == recovery.Instance && parties.IsCurrent(party);
        using var publication = context.Sender.QueuePublication(Current);
        // ACK只确认原成员能力已安装；27/28/29由本服场景重放负责，30仍等待真实37。
        await context.Reply(CmdPacketType.REJOIN_RAID, [1], "anton_rejoin_installed", Current);
        context.Sender.Post(0, (ushort)NotiPacketType.RAID_LIST, RaidProjection.List(raid), "anton_rejoin_record", Current);
        context.Sender.Post(0, (ushort)NotiPacketType.RAID_MODIFY, RaidProjection.Record(raid), "anton_rejoin_record_updated", Current);
        context.Sender.Post(0, (ushort)NotiPacketType.RAID_MODIFY,
            RaidPackets.OpenWithSquads(raid.Key, RaidProjection.Members(raid), RaidProjection.ClearCounts(raid)), "anton_rejoin_members", Current);
        // 幸存者只更新普通队伍成员及主机，不重建其场景或Actor。
        parties.Publish(party.Members.Where(member => recovery.Scene is null || !ReferenceEquals(member.Actor.Session, context.Session))
            .Select(member => new PartyNotice(PartyNoticeKind.Updated, member, Party: party, Guard: Current)));
        if (recovery.Scene is { } scene)
            await PartyDungeonProjection.PublishAsync([scene]);
        else if (context.Session.Selected is { } character)
        {
            town.QueueRaidDeparture(context, character, Current);
            town.QueueRaidArrival(context, character, Current);
        }
        if (context.Session.CurrentRaidBattle(actor) is { } battle)
        {
            RaidCombatProjection.PostAntonSnapshot(context, battle, battle.View, Current, restore: true);
            RaidCombatProjection.Notice(context, battle.View, actor);
        }
        log.Write("raid_rejoin_installed", new { Connection = context.Id, Self = actor.Character, Key = key.ToString("X8"),
            Reason = "local-original-scene-replay", StateRestored = true, recovery.Instance, recovery.Run,
            LoadingBarrier = recovery.Scene is null ? "not-applicable" : "original-scene-awaiting-client-37" });
    }
}
