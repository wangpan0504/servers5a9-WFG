using DfoServer.Game.Events;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;

namespace DfoServer.Game.Session;

internal sealed record EndlessAdventureView(ushort CharacterId, long Epoch, int Day, int Earned, bool Active);

internal sealed partial class GameSession
{
    EndlessAdventureView? endlessAdventurePublished;
    public bool EndlessAdventureActive => endlessAdventure is { } policy &&
        serverEvents?.IsEndlessAdventureActive(policy.Clock.GetUtcNow()) == true && policy.Available;
    public bool EndlessAdventureWasActive => endlessAdventurePublished?.Active == true &&
        endlessAdventurePublished.Epoch == SelectionEpoch;
    public bool NeedsEndlessAdventureRefresh => endlessAdventure is { } policy &&
        (EndlessAdventureActive || EndlessAdventureWasActive) &&
        (endlessAdventurePublished is not { } shown || shown.Epoch != SelectionEpoch ||
            shown.Day != EndlessAdventurePolicy.Day(policy.Clock.GetUtcNow()) || shown.Active != EndlessAdventureActive);
    public void PublishedEndlessAdventure(EndlessAdventureView view)
    {
        if (CanPublishEndlessAdventure(view)) endlessAdventurePublished = view;
    }

    public EndlessAdventureView? ReadEndlessAdventure() => owner.Execute(() =>
    {
        if (Selected is not { } character || endlessAdventure is not { } policy || repository is not IEndlessAdventureStore store) return null;
        int day = EndlessAdventurePolicy.Day(policy.Clock.GetUtcNow());
        bool active = EndlessAdventureActive;
        return new EndlessAdventureView(character.Id, SelectionEpoch, day, active ? store.ReadEndlessAdventure(owner, day) : 0, active);
    });

    public bool CanPublishEndlessAdventure(EndlessAdventureView view) => owner.IsCurrent && owner.Execute(() =>
        SelectionEpoch == view.Epoch && Selected?.Id == view.CharacterId && EndlessAdventureActive == view.Active &&
        endlessAdventure is { } policy && EndlessAdventurePolicy.Day(policy.Clock.GetUtcNow()) == view.Day);

    public InventoryResult BuyEndlessAdventure(BuyItemRequest request, InventoryShopService shop, out ShopPurchaseReceipt? receipt)
    {
        ShopPurchaseReceipt? planned = null;
        bool Room() => EndlessAdventureActive && Selected is { } character &&
            towns.IsRoom(character.Position, DfoServer.Game.Quests.QuestEligibility.Completed(character)) &&
            character.Position.Town is not (32 or 57 or 59);
        var result = PlanAndCommitInventory(character => request is
            { NpcId: EndlessAdventurePolicy.NpcId, ShopId: EndlessAdventurePolicy.ShopId } && Room()
                ? shop.Buy(character, request, out planned,
                    requiredEvent: new(serverEvents!.Read(EndlessAdventurePolicy.EventId).Schedule, shop.Clock))
                : InventoryResult.Reject(17), 17, requireTown: true, allowSettlement: false, additionalGuard: Room);
        receipt = result.Accepted ? planned : null;
        return result;
    }
}
