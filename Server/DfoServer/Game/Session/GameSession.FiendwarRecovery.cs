using DfoServer.Game.Raids;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    internal void RecordFiendwarDeparture(RaidActor actor, AntonRaidBattle battle, bool disconnected)
    {
        if (battle.Kind != RaidKind.Fiendwar) return;
        if (repository is not IFiendwarRecoveryStore store)
            throw new InvalidOperationException("漩涡缺少原票恢复存储");
        DateTimeOffset? until = disconnected && battle.CanCaptureLukeRejoin(actor)
            ? battle.FiendwarRecoveryNow.AddSeconds(300) : null;
        store.RecordFiendwarDeparture(actor, battle.Instance, until);
    }

    internal bool ChangeFiendwarRejoin(RaidActor original, RaidActor replacement, AntonRaidBattle battle, bool undo = false) =>
        battle.Kind != RaidKind.Fiendwar || repository is IFiendwarRecoveryStore store &&
            store.ChangeFiendwarRejoin(original, replacement, battle.Instance, battle.FiendwarRecoveryNow, undo);
}
