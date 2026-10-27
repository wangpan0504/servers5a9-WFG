using DfoServer.Game.Inventory.Models;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    long serverItemsSelectionEpoch=-1;
    public DateTimeOffset NextServiceWake(DateTimeOffset now) => itemCycles?.NextWake(now) ??
        DfoServer.GameWorld.Time.ServiceDay.NextReset(now, (timeProvider ?? TimeProvider.System).LocalTimeZone);
    public int ServerItemLimit(byte type, int id) => itemCycles?.Rules.Limits.SingleOrDefault(row => row.Type == type && row.ItemId == id)?.Maximum ?? 0;
    public InventoryResult? AdvanceServerItems(bool onSelection = false, bool initializing = false)
    {
        if (itemCycles is null) return null;
        var capture = owner.Execute(() => (Character: (initializing ? !Ready && Phase==SessionPhase.Unselected : Ready) && !HasPendingTownQuestCommit && pendingRental is null &&
            pendingConsumableUse is null && !HasPendingFormalCard && !HasActiveDungeonContinuation && !HasPendingQuestReplay && !HasPendingQuestScene &&
            formalDungeon?.PendingTutorialClear is null && formalDungeon?.PendingInventoryDiscard is null &&
            formalDungeon?.AltarSettlement is not { Stage: < 2 } &&
            formalDungeon?.HasPendingFatigue != true && formalDungeon?.Run.HasPendingDeath != true &&
            formalDungeon?.Run.HasPendingSettlement != true && formalDungeon?.Ground.HasPendingOperation != true
                ? Selected : null, SelectionEpoch, Phase));
        if (capture.Character is not { } character) return null;
        onSelection=initializing || onSelection && serverItemsSelectionEpoch!=capture.SelectionEpoch;
        var accounts = itemCycles.NeedsAccountCheck(character, onSelection) ? repository.ReadAccountItemRefills(owner) : null;
        var result = itemCycles.Maintain(character, accounts, onSelection);
        if (!result.Accepted || result.Plan is not { } plan) return result;
        return owner.Execute<InventoryResult?>(() =>
        {
            if ((initializing ? Ready || Phase!=SessionPhase.Unselected : !Ready) || SelectionEpoch != capture.SelectionEpoch ||
                Phase != capture.Phase || !ReferenceEquals(Selected, character)) return null;
            if (!plan.Changed && plan.ResetItemIds.IsEmpty)
            {
                if (initializing) serverItemsSelectionEpoch=SelectionEpoch;
                return null;
            }
            var committed = repository.CommitServerItemCycle(owner, character, plan);
            Selected = committed.Character;
            if (initializing) serverItemsSelectionEpoch=SelectionEpoch;
            return result with { MailboxChanged = committed.MailboxChanged, Receipt = new(Selected, owner.Generation, SelectionEpoch)
                { Phase = Phase, DungeonIdentity = formalDungeon?.Run.Snapshot.Identity, PreviousQuests = character.ActiveMainQuests } };
        });
    }
}
