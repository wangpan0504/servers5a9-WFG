using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;

namespace DfoServer.Game.Dungeons;

internal interface IEvildomWarGuideStore
{
    bool TryBindEvildomWarGuide(AccountOwners.Lease owner, Character expected, int week);
}
