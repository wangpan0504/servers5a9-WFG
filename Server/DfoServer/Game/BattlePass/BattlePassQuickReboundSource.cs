using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.BattlePass;

namespace DfoServer.Game.BattlePass;

internal sealed record BattlePassQuickReboundObservation(byte Job, ushort SkillId, ushort Level, bool? ChecksumMatches = true);

// 每日ID5（raw4、参数190、需求1、100XP）绑定正式副本内已习得受身蹲伏技能的CMD38施放通知。
internal static class BattlePassQuickReboundSource
{
    public const ushort SkillId = 190;

    public static bool Matches(BattlePassMission definition) => definition is
        { Id: 5, RawTypeCode: 4, RawParameter: SkillId, RequiredCount: 1, Experience: 100 };

    public static bool Eligible(Character character, DungeonEntryPlan entry, DungeonRunIdentity identity, BattlePassQuickReboundObservation use) =>
        character.Id != 0 &&
        identity.CharacterId == character.Id &&
        identity.InstanceId != Guid.Empty &&
        !entry.IsPractice &&
        !entry.IsTrainingRoom &&
        !entry.IsTutorial &&
        !entry.IsClassExperience &&
        !entry.IsQuestReplay &&
        use.Job == character.Job &&
        use.SkillId == SkillId &&
        use.Level > 0 &&
        use.ChecksumMatches != false &&
        HasLearnedQuickRebound(character);

    public static bool HasLearnedQuickRebound(Character character) =>
        character.Skills.Any(row => row.Id == SkillId && row.Level > 0) ||
        character.Learning.Trees.Any(tree => tree.Context == character.Learning.ActiveContext &&
            tree.Skills.Any(row => row.Id == SkillId && row.Level > 0));

    public static string SourceKey(string seasonKey, long periodKey)
    {
        byte[] naturalKey = JsonSerializer.SerializeToUtf8Bytes(new[]
        {
            "quick-rebound-skill/v1",
            seasonKey,
            periodKey.ToString(CultureInfo.InvariantCulture)
        });
        return Convert.ToHexString(SHA256.HashData(naturalKey).AsSpan(0, 16)).ToLowerInvariant();
    }
}
