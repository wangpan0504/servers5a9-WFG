using DfoServer.Game.Characters;
using DfoServer.GameWorld.Channels;
using DfoServer.Game.Adventure;
using DfoServer.GameWorld.Adventure;

namespace DfoServer.Game.Session;

internal sealed record PublicCharacterSnapshot(Character Character, ChannelIdentity Channel, SessionPhase Phase,
    int AdventureLevel, string AdventureName, bool PremiumPcRoom, Func<bool> IsCurrent, Func<bool> IsUnchanged);

internal sealed partial class GameSession
{
    internal (AdventureSnapshot Snapshot, Func<bool> IsCurrent)? PublicAdventure(ushort id, AdventureCatalog catalog)
    {
        try
        {
            return owner.Execute<(AdventureSnapshot, Func<bool>)?>(() =>
            {
                if (!Ready || Selected?.Id != id) return null;
                long epoch = SelectionEpoch;
                var snapshot = ReadAdventure(catalog, DateTimeOffset.UtcNow);
                return (snapshot, () => owner.IsCurrent && Ready && SelectionEpoch == epoch && Selected?.Id == id);
            });
        }
        catch (InvalidOperationException) when (!owner.IsCurrent) { return null; }
    }

    internal PublicCharacterSnapshot? PublicCharacter(ushort characterId)
    {
        try
        {
            return owner.Execute(() =>
            {
                if (!Ready || Selected is not { } character || character.Id != characterId) return null;
                long epoch = SelectionEpoch;
                var phase = Phase;
                // 发布在请求者账号锁内检查：只读目标的失效标记，不再获取目标账号锁。
                bool Current() => owner.IsCurrent && Ready && SelectionEpoch == epoch && Selected?.Id == characterId;
                bool Unchanged() => Current() && Phase == phase && ReferenceEquals(Selected, character);
                return new PublicCharacterSnapshot(character, ChannelEntry?.Identity ?? default, Phase,
                    ReadAdventureLevel(), ReadAdventureName(), PremiumPcRoom, Current, Unchanged);
            });
        }
        catch (InvalidOperationException error) when (error.Message == "Stale account generation")
        {
            return null;
        }
    }
}
