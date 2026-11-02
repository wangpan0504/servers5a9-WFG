using DfoServer.Game.Strongway2019;
using DfoServer.Game.Characters;
using DfoServer.Game.Events;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    internal Strongway2019Admission? PrepareStrongway2019Admission(uint dungeonId, bool matched = false)
    {
        if (strongway2019Catalog is null || strongway2019AntiEvil is null || serverEvents is null) return null;
        var captured = owner.Execute(() => Ready && Selected is not null &&
            Phase is SessionPhase.Town or SessionPhase.MapSelect or SessionPhase.Settlement &&
            !HasPendingTownQuestCommit && !HasPendingQuestReplay && pendingRental is null
                ? (Character: Selected, Epoch: SelectionEpoch, Count: CurrentParty?.Invoke()?.Members.Length ?? 1)
                : (Character: (Character?)null, Epoch: 0L, Count: 0));
        if (captured.Character is not { } character) return null;
        var schedule = serverEvents.Read(ServerEventService.Strongway2019Id).Schedule;
        if (!schedule.IsActive((timeProvider ?? TimeProvider.System).GetUtcNow())) return null;
        int antiEvil = strongway2019AntiEvil(character);
        var admission = Strongway2019EntryPolicy.Prepare(strongway2019Catalog, character, ChannelEntry,
            dungeonId, captured.Count, antiEvil, schedule, (timeProvider ?? TimeProvider.System).GetUtcNow(), matched);
        return owner.Execute(() => ReferenceEquals(Selected, character) && Ready && SelectionEpoch == captured.Epoch &&
            (CurrentParty?.Invoke()?.Members.Length ?? 1) == captured.Count ? admission : null);
    }

    bool IsCurrentStrongway2019Admission(Strongway2019Admission admission) =>
        strongway2019Catalog is not null && strongway2019AntiEvil is not null && serverEvents is not null && Ready &&
        ReferenceEquals(Selected, admission.Character) &&
        admission.Schedule == serverEvents.Read(ServerEventService.Strongway2019Id).Schedule &&
        admission.AntiEvil == strongway2019AntiEvil(admission.Character) &&
        Strongway2019EntryPolicy.Prepare(strongway2019Catalog, admission.Character, ChannelEntry,
            admission.DungeonId, CurrentParty?.Invoke()?.Members.Length ?? 1, admission.AntiEvil, admission.Schedule,
            (timeProvider ?? TimeProvider.System).GetUtcNow(), admission.Mode == Strongway2019Mode.Matching) == admission;

    InventoryEventPrecondition? Strongway2019RequiredEvent(Strongway2019Admission? admission) =>
        admission is null ? null : new(admission.Schedule, timeProvider ?? TimeProvider.System);
}
