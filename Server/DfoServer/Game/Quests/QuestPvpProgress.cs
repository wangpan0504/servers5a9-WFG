using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Pvp;
using DfoServer.GameWorld.Quests;

namespace DfoServer.Game.Quests;

internal static class QuestPvpProgress
{
    // 旧无身份槽和非决斗目标不进入整局来源，不因此阻止决斗，也不永久复制无关任务。
    public static ImmutableArray<ActiveMainQuest> Bindings(IReadOnlyList<ActiveMainQuest> active, QuestCatalog? catalog) =>
        catalog is null ? [] : active.Where(row => row.AcceptanceId != Guid.Empty && row.ConditionState != 0 &&
            catalog.TryGet(row.QuestId, out var quest) && Supports(quest) && QuestCoverage.HasOrdinaryDeclarations(quest)).ToImmutableArray();

    public static bool Supports(StoryQuest quest) => QuestTargetSchema.Read(quest).State == QuestParameterState.Parsed && quest.TargetKind switch
    {
        QuestTargetKind.PvpMatch or QuestTargetKind.PvpWin or QuestTargetKind.PvpLose => quest.SubType is -1 or 0,
        QuestTargetKind.PvpQuest => quest.SubType is 0 or 1 or 2 or 8,
        _ => false
    };

    public static uint Initial(StoryQuest quest) => QuestTargetSchema.Read(quest).Initial;

    public static ImmutableArray<ActiveMainQuest> Apply(IReadOnlyList<ActiveMainQuest> active, QuestCatalog catalog,
        PvpArenaResultRecord result, PvpArenaResultParticipant participant, int? grade = null)
    {
        if (result.RatingMode == PvpRatingMode.Total || participant.Outcome == PvpArenaOutcome.Cancelled ||
            participant.QuestBindings is not { IsDefaultOrEmpty: false } bindings) return active.ToImmutableArray();
        int mode = result.Mode == PvpArenaMode.Relay ? 1 : result.Mode == PvpArenaMode.Team ? 2 : result.Participants.Length == 2 ? 0 : 3;
        return active.Select(row =>
        {
            if (row.AcceptanceId == Guid.Empty || row.ConditionState == 0 || !bindings.Any(bound => bound.QuestId == row.QuestId &&
                bound.AcceptanceId == row.AcceptanceId && bound.Slot == row.Slot) || !catalog.TryGet(row.QuestId, out var quest) ||
                !Supports(quest) || !QuestCoverage.HasOrdinaryDeclarations(quest)) return row;
            if (quest.TargetKind == QuestTargetKind.PvpQuest && quest.SubType == 8)
                return grade is { } actual && actual >= quest.IntData[0] ? row with { ConditionState = 0 } : row;
            uint amount = quest.TargetKind switch
            {
                QuestTargetKind.PvpMatch => 1,
                QuestTargetKind.PvpWin when participant.Outcome == PvpArenaOutcome.Win => 1,
                QuestTargetKind.PvpLose when participant.Outcome == PvpArenaOutcome.Lose => 1,
                QuestTargetKind.PvpQuest when quest.IntData[1] == -1 || quest.IntData[1] == mode => quest.SubType switch
                {
                    0 => participant.Kills,
                    1 when participant.Outcome == PvpArenaOutcome.Win => 1,
                    2 when participant.Kills >= quest.IntData[2] => 1,
                    _ => 0
                },
                _ => 0
            };
            return row with { ConditionState = row.ConditionState - Math.Min(row.ConditionState, amount) };
        }).ToImmutableArray();
    }
}
