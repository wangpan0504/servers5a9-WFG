using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.PrivilegeServices;

internal sealed class PrivilegeServiceRewards(PrivilegeServiceCatalog catalog, ItemCatalog items, GoldLimitCatalog goldLimits,
    EpicPieceCatalog pieces, TimeProvider? clock = null)
{
    readonly TimeProvider time = clock ?? TimeProvider.System;

    public InventoryResult Claim(Character character, uint mode, uint value)
    {
        var now = time.GetUtcNow();
        var before = character.Inventory;
        var state = before.PrivilegeService;
        if (!state.IsActive(now.ToUnixTimeSeconds())) return InventoryResult.Reject(19);
        int day = PrivilegeServicePolicy.Day(now);
        state = state.ObserveLogin(now.ToUnixTimeSeconds(), day);
        CashShopBonus reward;
        if (mode == 0 && value == 0)
        {
            if (state.DailyClaimDay >= day || !catalog.DailyRewards.TryGetValue(state.RewardSeason, out var daily))
                return InventoryResult.Reject(19);
            reward = daily;
            state = state with { DailyClaimDay = day };
        }
        else if (mode == 1 && value <= int.MaxValue &&
            catalog.AccumulatedRewards.TryGetValue(state.RewardSeason, out var accumulated))
        {
            int index = -1;
            for (int row = 0; row < accumulated.Length; row++)
                if (accumulated[row].Days == (int)value) { index = row; break; }
            if (index < 0 || state.LoginDays < accumulated[index].Days || (state.ClaimedMilestones & (1u << index)) != 0)
                return InventoryResult.Reject(19);
            reward = new(accumulated[index].ItemId, accumulated[index].Count);
            state = state with { ClaimedMilestones = state.ClaimedMilestones | (1u << index) };
        }
        else return InventoryResult.Reject(19);
        var grant = InventoryRewardGrantService.TryGrantBatch(character, [new(reward.ItemId, reward.Count)],
            items, goldLimits, pieces, now);
        if (!grant.Accepted) return grant with { RejectReason = "privilege_reward_capacity" };
        var deadline = FatigueDailyReset.NextReset(now, TimeZoneInfo.Local).ToUnixTimeSeconds();
        return new(true, 0, new(before, grant.Plan!.After with { PrivilegeService = state }, "privilege_login_reward",
            new(character, Math.Min(deadline, state.ExpiresAt), time)));
    }
}
