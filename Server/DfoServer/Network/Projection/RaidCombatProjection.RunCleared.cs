using DfoServer.Game.Raids;
using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

internal static partial class RaidCombatProjection
{
    internal static void RunCleared(GameConnectionContext context, AntonRaidBattle.LukeRunCleared cleared, RaidActor actor)
    {
        // 资源规则可在同次提交中将Clear改成Hide/Close；瞬态通关必须消费原Run事实，
        // 不能仅靠最终快照或584的参与事件驱动原生通关横幅。
        context.Sender.Post(0, (ushort)NotiPacketType.RAID_CHANGE_DUNGEON_STATE,
            RaidCombatPackets.DungeonState(checked((int)cleared.Dungeon), 0, GameWorld.Raids.RaidDungeonState.Clear),
            "luke_run_cleared_state", () => context.Session.CanPublishLukeRunCleared(actor, cleared),
            () => RecordAntonDungeon(context, cleared.Instance, checked((int)cleared.Dungeon), GameWorld.Raids.RaidDungeonState.Clear));
        // 此包只有op4，不携旧位置；已提交事件不受退场/同团新revision影响。
        // 原actor、owner、选角epoch及Battle绑定仍须在写帧时成立，不能投到新角色/新团。
        context.Sender.Post(0, (ushort)NotiPacketType.RAID_DUNGEON_PARTICIPATION_INFO,
            RaidCombatPackets.RunCleared(cleared.Dungeon, cleared.Members), "luke_run_cleared",
            () => context.Session.CanPublishLukeRunCleared(actor, cleared));
        // Clear是演出事件，不能覆盖规则已经提交的Hide/Close；即使最终快照此前已去重也须恢复。
        GameWorld.Raids.RaidDungeonState? restored = null;
        context.Sender.PostPrepared(0, (ushort)NotiPacketType.RAID_CHANGE_DUNGEON_STATE, 6, () =>
        {
            var view = cleared.Battle.View;
            var row = cleared.Battle.DungeonSnapshot(view).SingleOrDefault(row => row.Id == cleared.Dungeon);
            restored = row.State;
            return row.State == GameWorld.Raids.RaidDungeonState.Clear ? null :
                RaidCombatPackets.DungeonState(row.Id, 0, row.State);
        }, "luke_run_final_state", () => context.Session.CanPublishLukeRunCleared(actor, cleared), written: () =>
        {
            if (restored is { } state) RecordAntonDungeon(context, cleared.Instance, checked((int)cleared.Dungeon), state);
        });
    }
}
