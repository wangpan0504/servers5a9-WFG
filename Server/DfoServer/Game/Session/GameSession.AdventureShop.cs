using DfoServer.Game.Adventure;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    public AdventureShopResult BuyAdventure(AdventureShopService service, byte category, int item, DateTimeOffset now)
    {
        var capture = owner.Execute(() => (Character: CanPlanAdventureRepresentative() ? Selected : null, SelectionEpoch, Phase,
            Identity: formalDungeon?.Run.Snapshot.Identity, Snapshot: repository.ReadAdventure(owner, service.Catalog, now)));
        if (capture.Character is not { } character) return new(false, 3);
        var prepared = service.Buy(capture.Snapshot, character, category, item, now);
        if (prepared.Plan is not { } plan) return new(false, prepared.Error);
        return owner.Execute(() =>
        {
            if (!CanPlanAdventureRepresentative() || !ReferenceEquals(Selected, character) || SelectionEpoch != capture.SelectionEpoch ||
                Phase != capture.Phase || formalDungeon?.Run.Snapshot.Identity != capture.Identity) return new AdventureShopResult(false, 3);
            var committed = repository.CommitAdventureShop(owner, plan, service.Catalog, now);
            Selected = committed.Character;
            var inventory = plan.Inventory with
            {
                Receipt = new InventoryCommitReceipt(committed.Character, owner.Generation, SelectionEpoch)
                    { Phase = Phase, DungeonIdentity = capture.Identity, PreviousQuests = character.ActiveMainQuests }
            };
            return new AdventureShopResult(true, 0, committed.Snapshot, inventory);
        });
    }
}
