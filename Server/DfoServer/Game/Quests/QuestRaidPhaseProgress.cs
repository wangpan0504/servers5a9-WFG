using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.GameWorld.Quests;

namespace DfoServer.Game.Quests;

// 020125D0按四元组匹配阶段、攻坚类型及参与身份；普通任务使用三个9位剩余计数。
// 本叶子只接受普通安徒恩的全员目标；称号、队长及其它攻坚身份另由其所属领域接入。
internal static class QuestRaidPhaseProgress
{
    internal static bool Supports(StoryQuest quest)
    {
        if (quest.TargetKind != QuestTargetKind.RaidPhaseClear || quest.SubType is not (-1 or 0) ||
            quest.Grade == "[title]" || quest.IntData.Count is < 4 or > 12 || quest.IntData.Count % 4 != 0 ||
            QuestTargetSchema.Read(quest).State != QuestParameterState.Parsed) return false;
        for (int at = 0; at < quest.IntData.Count; at += 4)
            if (quest.IntData[at] is not (0 or 1) || quest.IntData[at + 1] != 0 ||
                quest.IntData[at + 2] is < 1 or > 511 || quest.IntData[at + 3] is not (-1 or 0)) return false;
        return true;
    }

    internal static uint Initial(StoryQuest quest)
    {
        if (!Supports(quest)) throw new NotSupportedException("安徒恩任务参数尚未接入");
        uint scalar = 0;
        for (int at = 0; at < quest.IntData.Count / 4; at++)
            scalar |= checked((uint)quest.IntData[at * 4 + 2]) << (at * 9);
        return scalar;
    }

    internal static ImmutableArray<ActiveMainQuest> Bindings(IReadOnlyList<ActiveMainQuest> active, QuestCatalog? catalog) =>
        catalog is null ? [] : active.Where(row => row.AcceptanceId != Guid.Empty && row.ConditionState != 0 &&
            catalog.TryGet(row.QuestId, out var quest) && Supports(quest) && QuestCoverage.HasOrdinaryDeclarations(quest)).ToImmutableArray();

    internal static ImmutableArray<ActiveMainQuest> Apply(IReadOnlyList<ActiveMainQuest> active,
        ImmutableArray<ActiveMainQuest> bindings, QuestCatalog catalog, int phase)
    {
        if (phase is not (0 or 1) || bindings.IsDefaultOrEmpty) return active.ToImmutableArray();
        return active.Select(row =>
        {
            if (row.AcceptanceId == Guid.Empty || row.ConditionState == 0 || !bindings.Any(bound =>
                bound.AcceptanceId == row.AcceptanceId && bound.QuestId == row.QuestId && bound.Slot == row.Slot) ||
                !catalog.TryGet(row.QuestId, out var quest) || !Supports(quest) || !QuestCoverage.HasOrdinaryDeclarations(quest)) return row;
            int channels = quest.IntData.Count / 4;
            if (row.ConditionState >> (channels * 9) != 0 || Enumerable.Range(0, channels).Any(channel =>
                (row.ConditionState >> (channel * 9) & 511) > quest.IntData[channel * 4 + 2]))
                throw new InvalidDataException("安徒恩任务剩余计数超出原声明");
            uint scalar = row.ConditionState;
            for (int channel = 0; channel < channels; channel++)
                if (quest.IntData[channel * 4] == phase && (scalar >> (channel * 9) & 511) > 0)
                    scalar -= 1u << (channel * 9);
            return row with { ConditionState = scalar };
        }).ToImmutableArray();
    }
}
