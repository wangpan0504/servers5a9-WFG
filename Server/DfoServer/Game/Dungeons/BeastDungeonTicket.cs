using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Dungeons;

internal static class BeastDungeonTicket
{
    public const int Event = 10304093;
    public const int Ordinary = 10303752;

    public static InventoryPlan? Plan(InventorySnapshot inventory, EpicPieceCatalog pieces, DateTimeOffset now)
    {
        foreach (int item in new[] { Event, Ordinary })
        {
            var result = InventoryMaterialConsumptionService.Plan(inventory, [new(item, 1)], pieces, now);
            if (result.Accepted) return result.Plan;
        }
        return null;
    }
}

internal sealed record BeastDungeonLoadingReservation(AccountOwners.Lease Owner, Character Expected,
    DungeonRunIdentity Identity, int DungeonId, BeastDungeonAdmission Admission, TimeProvider Clock);

internal sealed class BeastDungeonAdmissionRejectedException(string reason) : InvalidOperationException(reason);
