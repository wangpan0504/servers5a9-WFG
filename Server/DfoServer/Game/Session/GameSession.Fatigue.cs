using DfoServer.Game.Characters;

namespace DfoServer.Game.Session;

internal sealed record FatigueDailyResetReceipt(Character Before, Character Character, long Generation, long Epoch, SessionPhase Phase);

internal sealed partial class GameSession
{
    public bool CanAdvanceServiceDay() => owner.Execute(() => Ready && Selected is not null &&
        pendingRental is null && pendingConsumableUse is null && !HasPendingTownQuestCommit && !HasPendingFormalCard &&
        !HasPendingQuestScene && !HasActiveDungeonContinuation && !HasPendingQuestReplay &&
        formalDungeon?.PendingTutorialClear is null && formalDungeon?.PendingInventoryDiscard is null &&
        formalDungeon?.AltarSettlement is not { Stage: < 2 } && formalDungeon?.HasPendingFatigue != true &&
        formalDungeon?.Ground.HasPendingOperation != true && formalDungeon?.Run.HasPendingDeath != true &&
        formalDungeon?.Run.HasPendingSettlement != true);

    public FatigueDailyResetReceipt? AdvanceFatigueDay(DateTimeOffset now) => owner.Execute<FatigueDailyResetReceipt?>(() =>
    {
        if (!Ready || Selected is not { Fatigue: { } fatigue } expected ||
            pendingRental is not null || pendingConsumableUse is not null || HasPendingTownQuestCommit || HasPendingFormalCard ||
            HasPendingQuestScene || HasActiveDungeonContinuation || HasPendingQuestReplay ||
            formalDungeon?.PendingTutorialClear is not null || formalDungeon?.PendingInventoryDiscard is not null ||
            formalDungeon?.AltarSettlement is { Stage: < 2 } ||
            formalDungeon?.HasPendingFatigue == true || formalDungeon?.Ground.HasPendingOperation == true ||
            formalDungeon?.Run.HasPendingDeath == true || formalDungeon?.Run.HasPendingSettlement == true) return null;
        int day = FatigueDailyReset.Day(now, (timeProvider ?? TimeProvider.System).LocalTimeZone);
        if (day <= fatigue.ResetDay) return null;
        Selected = repository.ResetDailyFatigue(owner, expected, day, now);
        return new(expected, Selected, owner.Generation, SelectionEpoch, Phase);
    });

    public bool CanPublishFatigueDay(FatigueDailyResetReceipt receipt) => owner.Execute(() =>
        Ready && owner.Generation == receipt.Generation && SelectionEpoch == receipt.Epoch && Phase == receipt.Phase &&
        Selected is { } selected && selected.Id == receipt.Character.Id && selected.Fatigue == receipt.Character.Fatigue);
}
