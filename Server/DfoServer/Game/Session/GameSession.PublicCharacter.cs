using DfoServer.Game.Characters;
using DfoServer.GameWorld.Channels;
using DfoServer.Game.Adventure;
using DfoServer.GameWorld.Adventure;

namespace DfoServer.Game.Session;

internal sealed record PublicCharacterSnapshot(Character Character, ChannelIdentity Channel, SessionPhase Phase,
    int AdventureLevel, string AdventureName, bool PremiumPcRoom, Func<bool> IsCurrent, Func<bool> IsUnchanged)
{
    public DfoServer.Game.Pvp.PvpDisplayGrade PvpGrade { get; init; }
    public bool ArenaSkills { get; init; }
    public ushort ReturningAdventurerType { get; init; }
    public CharacterLearningState? ArenaLearning { get; init; }
    public Character SkillCharacter => ArenaSkills ? Character with {Learning=ArenaLearning ?? DfoServer.Game.Pvp.PvpTrainingSkills.ArenaSource(Character)} : Character;
}

internal sealed partial class GameSession
{
    internal (AdventureSnapshot Snapshot, ChannelIdentity Channel, Func<bool> IsCurrent)? PublicAdventure(ushort id, AdventureCatalog catalog)
    {
        try
        {
            return owner.Execute<(AdventureSnapshot, ChannelIdentity, Func<bool>)?>(() =>
            {
                if (!Ready || Selected?.Id != id || ChannelEntry is not { } entry) return null;
                long epoch = SelectionEpoch;
                var channel = entry.Identity;
                var snapshot = ReadAdventure(catalog, DateTimeOffset.UtcNow);
                return (snapshot, channel, () => owner.IsCurrent && Ready && SelectionEpoch == epoch && Selected?.Id == id &&
                    ChannelEntry?.Identity == channel);
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
                ushort returningType = ReturningAdventurerType;
                // 发布在请求者账号锁内检查：只读目标的失效标记，不再获取目标账号锁。
                bool Current() => owner.IsCurrent && Ready && SelectionEpoch == epoch && Selected?.Id == characterId &&
                    ReturningAdventurerType == returningType;
                bool Unchanged() => Current() && Phase == phase && ReferenceEquals(Selected, character);
                return new PublicCharacterSnapshot(character, ChannelEntry?.Identity ?? default, Phase,
                    ReadAdventureLevel(), ReadAdventureName(), PremiumPcRoom, Current, Unchanged)
                    {PvpGrade=ReadPvpGrade(character), ReturningAdventurerType=returningType,
                        ArenaSkills=UsesArenaSkills,ArenaLearning=UsesArenaSkills ? ReadSkillLearning(character) : null};
            });
        }
        catch (InvalidOperationException error) when (error.Message == "Stale account generation")
        {
            return null;
        }
    }
}
