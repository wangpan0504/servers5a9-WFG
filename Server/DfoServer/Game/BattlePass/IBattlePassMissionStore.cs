using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;

namespace DfoServer.Game.BattlePass;

internal sealed record BattlePassMissionCommit(bool Accepted, ushort Error, BattlePassMissionState? State = null)
{
    public static BattlePassMissionCommit Reject(ushort error = 19) => new(false, error);
}
internal interface IBattlePassMissionStore
{
    BattlePassMissionState? ReadBattlePassMissions(AccountOwners.Lease owner, DateTimeOffset now);
    BattlePassMissionCommit CommitBattlePassMission(AccountOwners.Lease owner, Character expected, long seasonRevision,
        DateTimeOffset sampledAt, byte action, ushort key, uint category, uint viewRow, TimeProvider clock);
    BattlePassMissionCommit CommitBattlePassQuickRebound(AccountOwners.Lease owner, Character expected,
        DungeonEntryPlan entry, DungeonRunIdentity identity, BattlePassQuickReboundObservation use, TimeProvider clock);
}
