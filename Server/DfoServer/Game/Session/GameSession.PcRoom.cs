using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.PcRoom;

namespace DfoServer.Game.Session;

internal sealed record PcRoomView(Character Character, long Epoch, bool Active, int Day, PcRoomClaimState Claims);

internal sealed partial class GameSession
{
    public uint PcRoomBuffId { get; private set; } = 1249;
    public void ConfigurePcRoom(DfoServer.GameWorld.PcRoom.PcRoomCatalog catalog)
    {
        PcRoomBuffId = catalog.Premium.BuffId;
        ConfigureFatigueAcceleration(catalog.AccelerationPercent);
    }

    public PcRoomView? ReadPcRoomView() => owner.Execute(() =>
    {
        if (Selected is not { } character) return null;
        int day = PcRoomDay.At((timeProvider ?? TimeProvider.System).GetUtcNow());
        var claims = repository is IPcRoomStore store ? store.ReadPcRoomClaims(owner, character.Id, day) : default;
        return new PcRoomView(character, SelectionEpoch, PremiumPcRoom, day, claims);
    });

    public bool CanPublishPcRoom(PcRoomView view) => owner.Execute(() =>
        SelectionEpoch == view.Epoch && ReferenceEquals(Selected, view.Character) && PremiumPcRoom == view.Active &&
        view.Day == PcRoomDay.At((timeProvider ?? TimeProvider.System).GetUtcNow()));

    public InventoryResult ClaimPcRoom(bool rental, PcRoomRewardService service)
    {
        var clock = timeProvider ?? TimeProvider.System;
        var now = clock.GetUtcNow();
        int day = PcRoomDay.At(now);
        try
        {
            return PlanAndCommitInventory(character =>
            {
                if (repository is not IPcRoomStore store) return InventoryResult.Reject(1);
                var claims = store.ReadPcRoomClaims(owner, character.Id, day);
                if (rental ? claims.RentalClaimed : claims.LoginClaimed) return InventoryResult.Reject(2);
                var result = service.PlanClaim(character, rental, now);
                if (!result.Accepted || result.Plan is not { } plan) return result;
                long until = PcRoomDay.Next(now);
                if (debugSettings?.PremiumPcRoom != true)
                    until = Math.Min(until, character.Inventory.AccountWallet.ContractExpiry(128));
                var qualified = plan.WithAfter(plan.After, new InventoryCharacterPrecondition(character, until, clock));
                qualified = WithPcRoomQualification(character, qualified);
                qualified.PcRoomClaim = new(day, rental);
                return result with { Plan = qualified };
            }, 1, requireTown: true, allowSettlement: false, additionalGuard: () => PremiumPcRoom);
        }
        catch (PcRoomAlreadyClaimedException) { return InventoryResult.Reject(2); }
    }

    public InventoryResult RentPcRoomWeapons(IReadOnlyList<byte> indexes, PcRoomRewardService service) =>
        PlanAndCommitInventory(character =>
        {
            var clock = timeProvider ?? TimeProvider.System;
            var result = service.PlanWeapons(character, indexes, clock.GetUtcNow());
            if (result.Plan is not { } plan) return result;
            var qualified = debugSettings?.PremiumPcRoom == true ? plan : plan.WithAfter(plan.After,
                new InventoryCharacterPrecondition(character, character.Inventory.AccountWallet.ContractExpiry(128), clock));
            return result with { Plan = WithPcRoomQualification(character, qualified) };
        }, 1, requireTown: true, allowSettlement: false, additionalGuard: () => PremiumPcRoom);
}
