using DfoServer.Game.Raids;
using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

internal static partial class RaidCombatProjection
{
    internal static void RestoreStatusForScene(GameConnectionContext context, Func<bool> current)
    {
        if (context.Session.RaidStatusSnapshot() is not { Battle.Kind: RaidKind.Luke } snapshot) return;
        // 加载前的583只能缓存符号，尚未就绪的地图效果物件收不到onChangeRaidSymbol；30后恢复原值供ACT重评。
        context.Sender.Post(0, (ushort)NotiPacketType.RAID_SET_SYMBOL,
            RaidCombatPackets.Symbols(snapshot.Battle.SymbolSnapshot(snapshot.View)), "raid_scene_symbols",
            () => current() && context.Session.CanPublishRaidBattle(snapshot.Actor, snapshot.View));
        PostStatus(context, snapshot.Battle, snapshot.View,
            () => current() && context.Session.CanPublishRaidBattle(snapshot.Actor, snapshot.View), restore: true);
    }

    internal static void PostStatus(GameConnectionContext context, AntonRaidBattle battle, AntonRaidBattleView view,
        Func<bool> current, bool restore = false)
    {
        var rows = battle.StatusSnapshot(view);
        bool Guard() => current() && battle.IsCurrent(view);
        // Luke独立发布op4；普通普雷已有586/op5原生提示，584只静默同步参与位置。
        bool notifyClears = !restore && battle.Kind != RaidKind.Luke && (battle.Kind != RaidKind.Prey || battle.PreyGuide);
        byte[] next = RaidCombatPackets.Participation([], rows);
        context.Sender.PostPrepared(0, (ushort)NotiPacketType.RAID_DUNGEON_PARTICIPATION_INFO, 561, () =>
            !restore && context.RaidParticipationBody is { } displayed && next.AsSpan().SequenceEqual(displayed) &&
                (!notifyClears || !rows.Any(row => row.Cleared && !context.RaidParticipationRows.Any(old => old.Run == row.Run && old.Cleared))) ? null :
                RaidCombatPackets.Participation(context.RaidParticipationRows, rows, notifyClears: notifyClears), "raid_participation", Guard,
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
