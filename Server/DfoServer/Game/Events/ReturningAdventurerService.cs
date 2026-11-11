using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Events;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Events;

internal sealed record ReturningMissionProgress(int Id, uint Progress, uint State);
internal sealed record ReturningAdventurerState(long Until, ushort Welcome, ushort Daily,
    ImmutableArray<ReturningMissionProgress> Missions);
internal sealed record ReturningAdventurerClaim(uint Action, uint Argument, int Day, long Until,
    ReturningAdventurerReward Reward, InventoryPlan Inventory);
internal sealed record ReturningAdventurerCommit(bool Accepted, Character? Character = null);

internal interface IReturningAdventurerStore
{
    ReturningAdventurerState ReadReturningAdventurer(AccountOwners.Lease owner, Character expected);
    ReturningAdventurerCommit ClaimReturningAdventurer(AccountOwners.Lease owner, Character expected, ReturningAdventurerClaim claim);
}

internal sealed class ReturningAdventurerService(IReturningAdventurerStore store, ReturningAdventurerCatalog catalog,
    AdventurerBenefitService benefits, ItemCatalog items, GoldLimitCatalog goldLimits, EpicPieceCatalog pieces,
    DfoServer.Game.Settings.GameDebugSettings settings, TimeProvider? time = null)
{
    readonly TimeProvider clock = time ?? TimeProvider.System;
    public ReturningAdventurerCatalog Catalog => catalog;
    public ReturningAdventurerState Read(AccountOwners.Lease owner, Character character) => store.ReadReturningAdventurer(owner, character);

    public (ReturningAdventurerClaim? Claim, InventoryResult? Inventory) Prepare(AccountOwners.Lease owner, Character character,
        uint action, uint argument)
    {
        var before = Read(owner, character);
        var now = clock.GetUtcNow();
        if (before.Until == 0 || before.Until != benefits.ReturningUntil(owner.Account, now.ToUnixTimeSeconds())) return (null, null);
        ReturningAdventurerReward? reward = action switch
        {
            1 when argument == 0 && before.Welcome == 1 => catalog.Welcome,
            3 when argument == 0 && before.Daily == 1 => catalog.Daily,
            4 when before.Missions.Any(row => row.Id == argument && row.State == 1) =>
                catalog.Missions.Single(row => row.Id == argument).Reward,
            _ => null
        };
        if (reward is null) return (null, null);
        var inventory = InventoryRewardGrantService.TryGrantBatch(character, [new(reward.ItemId, reward.Count)], items,
            goldLimits, pieces, now, shopExpiration: settings.IgnoreCashShopAbsoluteExpiration,
            ignoreUsablePeriod: settings.IgnoreCashShopUsablePeriod);
        if (!inventory.Accepted || inventory.Plan is null) return (null, inventory);
        return (new(action, argument, PcRoomTimePointDay.At(now), before.Until, reward, inventory.Plan), inventory);
    }

    public ReturningAdventurerCommit Commit(AccountOwners.Lease owner, Character character, ReturningAdventurerClaim claim) =>
        store.ClaimReturningAdventurer(owner, character, claim);
}
