using DfoServer.Game.Raids;
using DfoServer.Game.BattlePass;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    public BattlePassMissionSessionState? ReadBattlePassRaidCompletionState(RaidActor actor,AntonRaidBattleView view)=>owner.Execute(() =>
    {
        if(!Ready || Selected is null || !MatchesRaidCombatActor(actor) || raidCombat is not {} raid || raid.Actor!=actor ||
            raid.Battle.Kind!=RaidKind.Luke || !raid.Battle.IsCurrent(view) || view.State is not (AntonRaidBattleState.Reward or AntonRaidBattleState.Completed) ||
            raid.Battle.LukeEligibility(actor.Character,view.Phase) is not { CountsAsClear:true } ||
            repository is not IBattlePassMissionStore store ||
            store.ReadBattlePassMissions(owner,(timeProvider??TimeProvider.System).GetUtcNow()) is not {} state || !state.Active)return null;
        return new BattlePassMissionSessionState(state,InventoryReceipt(Selected));
    });
}
