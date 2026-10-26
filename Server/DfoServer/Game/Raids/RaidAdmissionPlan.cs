using DfoServer.Game.Characters;
using DfoServer.GameWorld.Channels;

namespace DfoServer.Game.Raids;

// 已核对 enterablespecialchannel.etc 的 anton=12829，以及 anton_dimension_enter.qst
// 的 [level] 85 99、前置 3779；luke=2903，Luke_enter_01.qst 的 [level] 90 99、前置 4872。
// 名册资格位只负责显示，选角与招募必须用最新角色复核。
internal static class RaidAdmissionPlan
{
    internal const int AntonMinimumLevel = 85, AntonMaximumLevel = 99, AntonPrerequisite = 3779;

    internal const int LukeMinimumLevel = 90, LukeMaximumLevel = 99, LukePrerequisite = 4872;

    public static string? Rejection(ChannelEntry? channel, Character character)
    {
        if (channel?.Type == 0x20)
        {
            if (character.Level is < LukeMinimumLevel or > LukeMaximumLevel) return "luke-level-required";
            var lukeCompleted = DfoServer.Game.Quests.QuestEligibility.Completed(character);
            if (!lukeCompleted.Contains(CharacterRosterEntry.LukeEntryQuest)) return "luke-entry-quest-required";
            if (!lukeCompleted.Contains(LukePrerequisite)) return "luke-prerequisite-required";
            return null;
        }
        if (channel?.Type != ChannelAreaLanding.AntonRaidType) return null;
        if (character.Level is < AntonMinimumLevel or > AntonMaximumLevel) return "anton-level-required";
        var completed = DfoServer.Game.Quests.QuestEligibility.Completed(character);
        if (!completed.Contains(CharacterRosterEntry.AntonEntryQuest)) return "anton-entry-quest-required";
        if (!completed.Contains(AntonPrerequisite)) return "anton-prerequisite-required";
        return null;
    }
}

internal sealed class RaidAdmissionException(string reason) : InvalidOperationException(reason);
