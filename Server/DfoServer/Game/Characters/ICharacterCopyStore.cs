using DfoServer.Game.Accounts;
using DfoServer.GameWorld.Towns;

namespace DfoServer.Game.Characters;

internal sealed record CharacterCopyRequest(string SourceName, ushort SourceId, long Revision,
    string NewName, Location Room, Location Return);

internal interface ICharacterCopyStore
{
    Character CopyCharacter(AccountOwners.Lease owner, CharacterCopyRequest request);
}
