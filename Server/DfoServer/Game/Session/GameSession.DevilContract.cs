using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    public InventoryResult ReportDevilEffect(byte slot, DateTimeOffset now) => MutateInventory(character =>
    {
        if (Phase != SessionPhase.Dungeon || formalDungeon is null ||
            formalDungeon.Run.Snapshot.Phase != SinglePlayerDungeonPhase.Dungeon ||
            formalDungeon.Run.HasPendingDeath || formalDungeon.Run.HasPendingSettlement)
            return InventoryResult.Reject(19);
        return DevilContractPolicy.ReportDungeonEffect(character, slot, formalDungeon.Run.Snapshot.Identity.InstanceId, now);
    }, 19, requireTown: false);
}
