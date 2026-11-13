using DfoServer.Game.Characters;
using DfoServer.GameWorld.Channels;
namespace DfoServer.Game.Dungeons;

internal enum RevisedBeastDifficulty : byte
{
    King = 0,
    Nightmare = 1,
    TheBeast = 2
}

internal readonly record struct RevisedBeastAdmission(bool Accepted, RevisedBeastDifficulty Difficulty, string Reason)
{
    public static RevisedBeastAdmission Check(ChannelEntry? channel, Character character, int dungeonId, byte difficulty, DateTimeOffset now)
    {
        if (!BeastChannel.IsEntry(channel)) return new(false, default, "channel");
        if (!BeastChannel.Eligible(character.Level, character.CompletedQuests.Contains(RevisedBeastSelection.EntryQuest)))
            return new(false, default, "qualification");
        if (!RevisedBeastSelection.IsDaily(dungeonId, now)) return new(false, default, "rotation");
        if (!RevisedBeastSelection.IsDifficulty(difficulty)) return new(false, default, "difficulty");
        return new(true, (RevisedBeastDifficulty)difficulty, "accepted");
    }
}
