using DfoServer.Game.Raids;
using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

internal sealed record RaidCombatDisplayIdentity(Game.Session.GameSession Session, Game.Accounts.AccountOwners.Lease Owner,
    long SelectionEpoch, ushort Character, Guid Instance, int Phase, AntonRaidBattleState State)
{
    public static RaidCombatDisplayIdentity Capture(RaidActor actor, AntonRaidBattleView view) =>
        new(actor.Session, actor.Owner, actor.Epoch, actor.Character, view.Instance, view.Phase, view.State);
}

internal static class RaidCombatProjection
{
    public static void Notice(GameConnectionContext context, AntonRaidBattleView view, RaidActor actor)
    {
        bool Guard() => context.Session.CanPublishRaidBattle(actor, view);
        // 卢克首个Run策略尚无奖励记录，不能借安徒恩投影打开空窗口。
        if (view.State == AntonRaidBattleState.Reward && context.Session.CurrentRaidBattle(actor)?.Kind == RaidKind.Luke &&
            context.Session.RaidCombatResult(view) is null) return;
        if (context.Session.CurrentRaidBattle(actor) is { Kind: RaidKind.Luke } luke)
            PostLukeSnapshot(context, luke, view, Guard);
        PostState(context, view, actor, Guard, "raid_phase_state");
    }

    internal static void PostState(GameConnectionContext context, AntonRaidBattleView view, RaidActor actor,
        Func<bool> current, string action)
    {
        if (context.Session.CurrentRaidBattle(actor)?.Kind != RaidKind.Luke)
        {
            if (view.State == AntonRaidBattleState.Reward && context.Session.RaidCombatResult(view) is { } result)
                context.Sender.Post(0, (ushort)NotiPacketType.RAID_RESULT, RaidCombatPackets.Result(result), "raid_phase_result", current);
            context.Sender.Post(0, (ushort)NotiPacketType.RAID_STATE, RaidCombatPackets.State(view), action, current);
            return;
        }
        context.Owner.Execute(() =>
        {
            var identity = RaidCombatDisplayIdentity.Capture(actor, view);
            bool Show() => current() && context.RaidDisplay != identity;
            // 587同state/phase仍会重新安装原生动画；601只判断，587成功写帧才确认。
            // 两包在本人owner内连续入队，无网络等待；仅使用Post，不用于QueuePublished的提前guard。
            if (view.State == AntonRaidBattleState.Reward && context.Session.RaidCombatResult(view) is { } result)
                context.Sender.Post(0, (ushort)NotiPacketType.RAID_RESULT, RaidCombatPackets.Result(result), "raid_phase_result", Show);
            context.Sender.Post(0, (ushort)NotiPacketType.RAID_STATE, RaidCombatPackets.State(view), action, Show, () =>
            {
                if (actor.IsCurrent && context.Session.CurrentRaidBattle(actor)?.Instance == view.Instance)
                    context.RaidDisplay = identity;
            });
            return true;
        });
    }

    internal static void PostLukeSnapshot(GameConnectionContext context, AntonRaidBattle battle,
        AntonRaidBattleView view, Func<bool> guard)
    {
        if (battle.Kind != RaidKind.Luke || view.Instance != battle.Instance) return;
        context.Sender.Post(0, (ushort)NotiPacketType.RAID_SET_SYMBOL,
            RaidCombatPackets.Symbols(battle.SymbolSnapshot(view)), "raid_symbol_snapshot", guard);
        // 覆盖本实例全部15图；非当前phase显式Hide，防上阶段/旧团残留Open。
        // 586缺键会创建记录；不用585未知尾字段改变共享symbol7/8。
        foreach (var row in battle.DungeonSnapshot(view))
            context.Sender.Post(0, (ushort)NotiPacketType.RAID_CHANGE_DUNGEON_STATE,
                RaidCombatPackets.DungeonState(row.Id, 0, row.State), "raid_dungeon_snapshot", guard);
    }

}
