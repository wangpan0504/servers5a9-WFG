using DfoServer.Game.Titles;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    internal bool IsReadyCharacter(ushort characterId)
    {
        try { return owner.Execute(() => Ready && Selected?.Id == characterId); }
        catch (InvalidOperationException error) when (error.Message == "Stale account generation") { return false; }
    }

    public InventorySnapshot? ReadPublicTitleBook(ushort characterId) => owner.Execute(() =>
        Ready && Selected is not null ? repository.ReadPublicTitleBook(owner, characterId) : null);
    public TitleAchievementResult UpdateTitleAchievements(TitleAchievementService service, TitleTriggerRequest? request = null,
        bool initializing = false)
    {
        var captured = owner.Execute(() => (Character: Selected, SelectionEpoch, Phase, Dungeon: formalDungeon));
        if (captured.Character is not { } character || !CanUpdate()) return TitleAchievementResult.Reject(request?.QuestId ?? 0);
        var result = request is { } trigger ? service.Trigger(character, trigger, DateTimeOffset.UtcNow) :
            service.Reconcile(character, DateTimeOffset.UtcNow);
        return owner.Execute(() =>
        {
            if (!CanUpdate() || SelectionEpoch != captured.SelectionEpoch || Phase != captured.Phase ||
                !ReferenceEquals(formalDungeon, captured.Dungeon) || !ReferenceEquals(Selected, character))
                return TitleAchievementResult.Reject(request?.QuestId ?? 0);
            if (!result.Accepted || result.Plan is not { } plan) return result;
            Selected = repository.CommitTitleAchievement(owner, plan);
            return result with { Receipt = new(Selected, owner.Generation, SelectionEpoch)
                {
                    Phase = Phase,
                    DungeonIdentity = formalDungeon?.Run.Snapshot.Identity,
                    PreviousQuests = character.ActiveMainQuests,
                    PreviousSkillSwitchInventory = character.SkillSwitchInventory
                } };
        });

        bool CanUpdate() => owner.Execute(() => pendingCommunication is null && pendingRental is null && Selected is not null && (initializing
            ? !Ready && Phase == SessionPhase.Unselected
            : Ready && Phase is SessionPhase.Town or SessionPhase.MapSelect or SessionPhase.Dungeon or SessionPhase.Settlement &&
              !HasPendingQuestScene && pendingQuestCompletion is null && pendingQuestLifecycle is null && !HasUncommittedQuestQuickClear &&
              !HasActiveDungeonContinuation && !HasPendingQuestReplay && !HasPendingFormalCard && formalDungeon?.Ground.HasPendingOperation != true &&
              formalDungeon?.HasPendingFatigue != true && formalDungeon?.Run.HasPendingDeath != true && formalDungeon?.Run.HasPendingSettlement != true));
    }
    public bool CanPublishTitle(InventoryCommitReceipt receipt, bool initializing = false) => owner.Execute(() =>
        (initializing ? !Ready && Phase == SessionPhase.Unselected : Ready) && receipt.Generation == owner.Generation &&
        SelectionEpoch == receipt.SelectionEpoch && Phase == receipt.Phase && Selected?.Id == receipt.Character.Id &&
        ReferenceEquals(Selected, receipt.Character) && formalDungeon?.Run.Snapshot.Identity == receipt.DungeonIdentity);
}
