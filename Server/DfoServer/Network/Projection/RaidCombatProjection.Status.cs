using DfoServer.Game.Raids;
using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

internal static partial class RaidCombatProjection
{
    internal static void RestoreStatusForScene(GameConnectionContext context, Func<bool> current)
    {
        if (context.Session.RaidStatusSnapshot() is not { Battle.Kind: RaidKind.Luke } snapshot) return;
        PostStatus(context, snapshot.Battle, snapshot.View,
            () => current() && context.Session.CanPublishRaidBattle(snapshot.Actor, snapshot.View), restore: true);
    }

    internal static void PostStatus(GameConnectionContext context, AntonRaidBattle battle, AntonRaidBattleView view,
        Func<bool> current, bool restore = false)
    {
        var rows = battle.StatusSnapshot(view);
        bool Guard() => current() && battle.IsCurrent(view);
        byte[] next = RaidCombatPackets.Participation([], rows);
        context.Sender.PostPrepared(0, (ushort)NotiPacketType.RAID_DUNGEON_PARTICIPATION_INFO, 561, () =>
            !restore && context.RaidParticipationBody is { } displayed && next.AsSpan().SequenceEqual(displayed) &&
                !rows.Any(row => row.Cleared && !context.RaidParticipationRows.Any(old => old.Run == row.Run && old.Cleared)) ? null :
                RaidCombatPackets.Participation(context.RaidParticipationRows, rows, notifyClears: !restore), "raid_participation", Guard,
            allowShorter: true, written: () =>
            {
                context.RaidParticipationBody = next;
                context.RaidParticipationRows = rows;
            });
        byte[] hp = RaidCombatPackets.StatusHp(rows);
        context.Sender.Post(0, (ushort)NotiPacketType.RAID_MONSTER_HP, hp, "raid_status_hp",
            () => Guard() && (restore || context.RaidHpBody is not { } displayed || !hp.AsSpan().SequenceEqual(displayed)),
            () => context.RaidHpBody = hp);
    }
}
