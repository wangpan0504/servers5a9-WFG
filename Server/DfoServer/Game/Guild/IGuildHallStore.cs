using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.GameWorld.Towns;

namespace DfoServer.Game.Guild;

internal interface IGuildHallStore
{
    Character? SaveGuildHallPosition(AccountOwners.Lease owner, Character expected, Location position, Location? returnPoint, DfoServer.Game.BattlePass.GuildHallEntryFact? entry = null);
}
