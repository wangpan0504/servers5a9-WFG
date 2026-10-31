using System.Collections.Immutable;
using DfoServer.Game.Accounts;

namespace DfoServer.Game.Characters;

internal sealed record CharacterFormerName(string Name, uint ChangedAt);

internal interface ICharacterNameHistoryStore
{
    ImmutableArray<CharacterFormerName> ReadNameHistory(AccountOwners.Lease owner, ushort characterId);
}
