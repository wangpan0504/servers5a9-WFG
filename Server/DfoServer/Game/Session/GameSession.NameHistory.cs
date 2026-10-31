using System.Collections.Immutable;
using DfoServer.Game.Characters;

namespace DfoServer.Game.Session;

internal sealed record CharacterNameHistorySnapshot(PublicCharacterSnapshot Target, ImmutableArray<CharacterFormerName> Names);

internal sealed partial class GameSession
{
    internal CharacterNameHistorySnapshot? NameHistory(ushort characterId)
    {
        try
        {
            return owner.Execute(() =>
            {
                if (repository is not ICharacterNameHistoryStore store || PublicCharacter(characterId) is not { } target) return null;
                return new CharacterNameHistorySnapshot(target, store.ReadNameHistory(owner, characterId));
            });
        }
        catch (InvalidOperationException) when (!owner.IsCurrent) { return null; }
    }
}
