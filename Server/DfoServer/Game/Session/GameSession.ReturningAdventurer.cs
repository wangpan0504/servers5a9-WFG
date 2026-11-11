using DfoServer.Game.Characters;
using DfoServer.Game.Events;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Game.Session;

internal sealed record ReturningAdventurerDisplay(ReturningAdventurerState State, Func<bool> IsCurrent);

internal sealed partial class GameSession
{
    Character? returningProgressCharacter;
    ReturningAdventurerState? returningProgressState;
    long returningProgressEpoch = -1;
    int returningProgressDay;

    public ReturningAdventurerDisplay? PlanReturningAdventurerProgress(bool force = false) => owner.Execute(() =>
    {
        if (!Ready || Selected is not { } character || debugSettings?.ReturningAdventurers is not { } service) return null;
        long epoch = SelectionEpoch;
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        int day = PcRoomTimePointDay.At(now);
        long until = debugSettings.AdventurerBenefits!.ReturningUntil(owner.Account, now.ToUnixTimeSeconds());
        if (!force && ReferenceEquals(returningProgressCharacter, character) && returningProgressEpoch == epoch &&
            returningProgressDay == day && returningProgressState?.Until == until) return null;
        var state = service.Read(owner, character);
        bool changed = force || returningProgressState is null || returningProgressState.Until != state.Until ||
            returningProgressState.Welcome != state.Welcome || returningProgressState.Daily != state.Daily ||
            !returningProgressState.Missions.SequenceEqual(state.Missions) || returningProgressEpoch != epoch;
        returningProgressCharacter = character;
        if (changed) returningProgressState = state;
        returningProgressEpoch = epoch; returningProgressDay = day;
        if (!changed || state.Until == 0) return null;
        bool Current() => owner.IsCurrent && Ready && SelectionEpoch == epoch && Selected?.Id == character.Id &&
            ReferenceEquals(returningProgressState, state) && debugSettings.AdventurerBenefits.ReturningUntil(owner.Account,
                (timeProvider ?? TimeProvider.System).GetUtcNow().ToUnixTimeSeconds()) == state.Until;
        return new ReturningAdventurerDisplay(state, Current);
    });

    public InventoryResult ClaimReturningAdventurer(uint action, uint argument)
    {
        if (debugSettings?.ReturningAdventurers is not { } service) return InventoryResult.Reject();
        var capture = CaptureInventoryPlan(requireTown: false);
        if (capture is null) return InventoryResult.Reject();
        var prepared = service.Prepare(owner, capture.Character, action, argument);
        if (prepared.Claim is null) return prepared.Inventory ?? InventoryResult.Reject();
        return owner.Execute(() =>
        {
            if (!InventoryPlanIsCurrent(capture, requireTown: false)) return InventoryResult.Reject();
            var result = service.Commit(owner, capture.Character, prepared.Claim);
            if (!result.Accepted || result.Character is null) return InventoryResult.Reject();
            Selected = result.Character;
            returningProgressCharacter = null;
            return prepared.Inventory! with { Receipt = InventoryReceipt(capture.Character) };
        });
    }
}
