using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Adventure;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Adventure;

internal sealed record AdventureShopPlan(AdventureSnapshot Before, AdventureState After, Character Expected, InventoryResult Inventory);
internal sealed record AdventureShopCommit(AdventureSnapshot Snapshot, Character Character);
internal sealed record AdventureShopResult(bool Accepted, ushort Error, AdventureSnapshot? Snapshot = null, InventoryResult? Inventory = null);

internal sealed class AdventureShopService(AdventureCatalog catalog, ItemCatalog items, GoldLimitCatalog limits, EpicPieceCatalog pieces)
{
    public AdventureCatalog Catalog => catalog;
    public (AdventureShopPlan? Plan, ushort Error) Buy(AdventureSnapshot snapshot, Character character,
        byte category, int itemId, DateTimeOffset now)
    {
        var product = catalog.Products.GetValueOrDefault((category, itemId)) ??
            catalog.Products.Values.SingleOrDefault(row => row.Category == category && row.Index == itemId);
        if (product is null) return (null, 3);
        itemId = product.ItemId;
        if (snapshot.State.Level < product.RequiredLevel) return (null, 235);
        uint purchased = snapshot.State.Purchases.GetValueOrDefault(itemId);
        if (product.Limit != 0 && purchased >= product.Limit) return (null, 19);
        if (snapshot.State.Points[category] < product.Cost) return (null, 22);
        var granted = InventoryRewardGrantService.TryGrantBatch(character, [new(itemId, 1)], items, limits, pieces, now);
        if (!granted.Accepted) return (null, granted.Error == 17 ? (ushort)204 : (ushort)3);
        var after = snapshot.State with
        {
            Points = snapshot.State.Points.SetItem(category, snapshot.State.Points[category] - product.Cost),
            Purchases = snapshot.State.Purchases.SetItem(itemId, checked(purchased + 1))
        };
        return (new(snapshot, after, character, granted), 0);
    }
}
