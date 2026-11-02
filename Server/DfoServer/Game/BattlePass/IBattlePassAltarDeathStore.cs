using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;

namespace DfoServer.Game.BattlePass;

internal interface IBattlePassAltarDeathStore
{
    bool CommitBattlePassAltarDeath(AccountOwners.Lease owner, Character expected,
        SinglePlayerDungeonRun run, PreparedDungeonDeath prepared);
}
