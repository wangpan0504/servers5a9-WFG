using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.GameWorld.Quests;

namespace DfoServer.Game.Quests;

// 这些目标读取角色的实际领域状态；客户端事件只触发重算，不签发等级或职业变化。
internal static class QuestStateProgress
{
    public static byte? Type(StoryQuest quest) => quest.SubType == -1 ? quest.Condition switch
    {
        "[clear quest]" when quest.IntData is [> 0 and < 30000] => 12,
        "[level up]" when quest.IntData is [>= 1 and <= 95] => 31,
        "[first grow]" when quest.IntData.Count == 0 => 39,
        "[awakening]" when quest.IntData is [1 or 2] => 40,
        _ => null
    } : null;

    public static uint Scalar(Character character, StoryQuest quest) => Type(quest) switch
    {
        12 => QuestEligibility.Completed(character).Contains(quest.IntData[0]) ? 0u : 1u,
        31 => character.Level >= quest.IntData[0] ? 0u : 1u,
        39 => (character.GrowType & 15) != 0 ? 0u : 1u,
        40 => character.GrowType >> 4 >= quest.IntData[0] ? 0u : 1u,
        _ => throw new NotSupportedException("此目标尚未接入角色状态重算")
    };

    public static ImmutableArray<ActiveMainQuest> Refresh(IReadOnlyList<ActiveMainQuest> active, Character character, QuestCatalog quests) =>
        active.Select(row => row.AcceptanceId != Guid.Empty && quests.TryGet(row.QuestId,out var quest) && Type(quest) is not null &&
            QuestCoverage.HasOrdinaryDeclarations(quest) ? row with { ConditionState = Scalar(character,quest) } : row).ToImmutableArray();
}
