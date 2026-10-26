using DfoServer.Game.Characters;
using DfoServer.GameWorld.Channels;

namespace DfoServer.Game.Raids;

// 已核对 enterablespecialchannel.etc 的 anton=12829，以及 anton_dimension_enter.qst
// 的 [level] 85 99、前置 3779。名册资格位只负责显示，选角必须用最新持久角色复核。
internal static class RaidAdmissionPlan
{
    internal const int AntonMinimumLevel = 85, AntonMaximumLevel = 99, AntonPrerequisite = 3779;

    public static string? Rejection(ChannelEntry? channel, Character character)
    {
        if (channel?.Type != ChannelAreaLanding.AntonRaidType) return null;
        if (character.Level is < AntonMinimumLevel or > AntonMaximumLevel) return "anton-level-required";
        var completed = DfoServer.Game.Quests.QuestEligibility.Completed(character);
        if (!completed.Contains(CharacterRosterEntry.AntonEntryQuest)) return "anton-entry-quest-required";
        if (!completed.Contains(AntonPrerequisite)) return "anton-prerequisite-required";
        return null;
    }
}

internal sealed class RaidAdmissionException(string reason) : InvalidOperationException(reason);
