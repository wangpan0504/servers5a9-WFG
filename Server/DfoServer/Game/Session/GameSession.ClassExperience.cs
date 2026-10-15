using DfoServer.Game.Dungeons;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    bool HasClassExperienceContext => CurrentDungeonSelection?.IsClassExperience == true || formalDungeon?.Entry.IsClassExperience == true;

    public DungeonSelectionReceipt? BeginClassExperienceSelection() => owner.Execute(() =>
    {
        if (pendingCommunication is not null || !Ready || Selected is null || Phase is not (SessionPhase.Town or SessionPhase.MapSelect) ||
            formalDungeon is not null || HasPendingQuestReplay || HasActiveDungeonContinuation ||
            pendingQuestCompletion is not null || pendingQuestLifecycle is not null || pendingQuestQuickClear is not null ||
            CurrentDungeonSelection?.IsTutorial == true || Selected.Tutorial is { Completed: false })
            return null;
        if (CurrentDungeonSelection?.IsClassExperience != true)
            CurrentDungeonSelection = new(++dungeonSelectionId, SelectionEpoch, Selected.Id, Selected.Position, Selected.Position)
                { IsClassExperience = true };
        ValidateDungeonSelection();
        SetState(SessionPhase.MapSelect);
        return new DungeonSelectionReceipt(Selected, owner.Generation, SelectionEpoch, dungeonSelectionId);
    });
}
