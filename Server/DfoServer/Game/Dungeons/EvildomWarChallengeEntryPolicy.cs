using DfoServer.Game.Characters;
using DfoServer.GameWorld.Dungeons;

namespace DfoServer.Game.Dungeons;

// 英雄模式的独立入口合同；不读取普通魔界大战三模式、次数或战役状态。
internal static class EvildomWarChallengeEntryPolicy
{
    internal const int DungeonId = 400000105;
    internal const int WorldMap = 244;
    // 资源仍声明推荐/上限四人；正式 Party 允许一名真实队长独立创建队伍后入场。
    internal const byte PartyCount = 4;
    internal const byte Difficulty = 2;

    internal static bool AllowsPartyCount(int count) => count is >= 1 and <= PartyCount;

    internal static string? Rejection(DungeonDefinition definition, Character character, int? channelType,
        DungeonSelectionContext? selection, long epoch, byte difficulty, int partyCount, bool partyAdmission, bool specialMode)
    {
        if (!definition.IsEvildomWarChallenge || definition.MinimumLevel != 95 || definition.MinimumPartyCount != PartyCount ||
            !definition.TryGetDesignatedDifficulty(out byte fixedDifficulty) || fixedDifficulty != Difficulty ||
            !definition.DisableFatigueConsumption || !definition.EnterWithoutFatigue ||
            !definition.DisableMonsterAndClearExperience)
            return "evildom-war-challenge-resource-contract-invalid";
        if (channelType != ChannelAreaLanding.EvildomWarChallengeType ||
            character.Position is not { Town: ChannelAreaLanding.EvildomWarChallengeTown, Area: 2 })
            return "evildom-war-challenge-channel-town-required";
        if (character.Level < definition.MinimumLevel) return "evildom-war-challenge-level-required";
        if (!partyAdmission || !AllowsPartyCount(partyCount)) return "evildom-war-challenge-party-required";
        if (difficulty != fixedDifficulty || specialMode) return "evildom-war-challenge-mode-conflict";
        if (selection is null || selection.SelectionEpoch != epoch || selection.CharacterId != character.Id ||
            selection.OriginPosition != character.Position || selection.IsTutorial || selection.IsClassExperience || selection.Raid is not null)
            return "evildom-war-challenge-selection-required";
        return null;
    }
}
