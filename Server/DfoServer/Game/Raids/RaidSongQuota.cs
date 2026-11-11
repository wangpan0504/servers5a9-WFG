using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Raids;

// 不渝之咏只改这一材料的数量。原始奖励保持不变；某一阶段发牌时按当时余额裁，领取只向下裁。满额保留数量 0，不新增标志位。
internal static class RaidSongQuota
{
    internal const int ItemId = 10308807;

    internal static AccountMaterialDefinition? Limit(ItemCatalog? items) =>
        items is not null && items.TryGet(ItemId, out var item) ? item.AccountMaterial : null;

    internal static int Remaining(AccountMaterialBalance balance, ushort character, AccountMaterialDefinition definition, int week) =>
        AccountMaterialService.RaidRemaining(balance, character, definition, week);

    // 只裁当前这一份牌面。调用方不得把扣过的余额再传给下一阶段。
    internal static ImmutableArray<RaidRewardOffer> ClipOffersToRemaining(ImmutableArray<RaidRewardOffer> offers,
        AccountMaterialBalance balance, ushort character, AccountMaterialDefinition definition, int week)
    {
        int remaining = Remaining(balance, character, definition, week);
        return ClipOffers(offers, ref remaining);
    }

    // 已发出的牌面只向下裁。正数被裁到 0 时仍按 Grant 盖上攻坚周，持有和已获得不加。牌面本来就是 0 时不改余额。
    internal static ImmutableArray<InventoryReward> ClipGrantList(ImmutableArray<InventoryReward> grants, AccountMaterialBalance balance,
        ushort character, AccountMaterialDefinition definition, int week, out AccountMaterialBalance after)
    {
        int remaining = Remaining(balance, character, definition, week);
        var next = grants.ToArray();
        int granted = 0;
        bool touched = false;
        for (int index = 0; index < next.Length; index++)
        {
            var row = next[index];
            if (row.ItemId != ItemId || row.Count <= 0) continue;
            touched = true;
            int take = Math.Min(row.Count, remaining);
            remaining -= take;
            granted = checked(granted + take);
            if (take != row.Count) next[index] = row with { Count = take };
        }
        after = touched ? Credit(balance, character, definition, week, granted) : balance;
        return next.ToImmutableArray();
    }

    // 已保存的牌面是原牌面的不渝之咏向下裁剪，展示数量与保存的 Grants 一致，其他奖励不变。
    internal static bool IsOfferReduction(ImmutableArray<RaidRewardOffer> stored, ImmutableArray<RaidRewardOffer> original)
    {
        if (stored.IsDefault || original.IsDefault || stored.Length != original.Length) return false;
        bool reduced = false;
        for (int index = 0; index < stored.Length; index++)
        {
            var left = stored[index];
            var right = original[index];
            if (left.Kind != right.Kind || left.Card != right.Card || !SameRows(left.Display, right.Display)) return false;
            if (left.Grants.IsDefault || right.Grants.IsDefault || left.Grants.Length != right.Grants.Length) return false;
            bool offerReduced = false;
            for (int grant = 0; grant < left.Grants.Length; grant++)
            {
                var current = left.Grants[grant];
                var incoming = right.Grants[grant];
                if (current.ItemId != ItemId || incoming.ItemId != ItemId)
                {
                    if (current != incoming) return false;
                    continue;
                }
                if (current with { Count = incoming.Count } != incoming || current.Count < 0 || current.Count > incoming.Count) return false;
                if (current.Count != incoming.Count) offerReduced = true;
            }
            var expected = offerReduced ? ApplyGrants(right, left.Grants) : right;
            if (expected.LukeResult != left.LukeResult || !SameSpecs(expected.LukeAdditionalResults, left.LukeAdditionalResults) ||
                !SameSpecs(expected.PreyDisplay, left.PreyDisplay)) return false;
            if (offerReduced) reduced = true;
        }
        return reduced;
    }

    internal static RaidRewardOffer ApplyGrants(RaidRewardOffer offer, ImmutableArray<InventoryReward> grants)
    {
        var luke = grants.Length > 0 ? Retarget(offer.LukeResult, grants[0]) : offer.LukeResult;
        var additional = offer.LukeAdditionalResults;
        if (!additional.IsDefault && additional.Length == grants.Length - 1)
            additional = additional.Select((spec, index) => Retarget(spec, grants[index + 1])!).ToImmutableArray();
        var prey = offer.PreyDisplay;
        if (!prey.IsDefault && prey.Length == grants.Length)
            prey = prey.Select((spec, index) => Retarget(spec, grants[index])!).ToImmutableArray();
        return offer with { Grants = grants, LukeResult = luke, LukeAdditionalResults = additional, PreyDisplay = prey };
    }

    internal static bool IsSongCapSettlement(string storedJson, string incomingJson)
    {
        try
        {
            var stored = JsonSerializer.Deserialize<PreparedRaidReward>(storedJson);
            var incoming = JsonSerializer.Deserialize<PreparedRaidReward>(incomingJson);
            if (stored is null || incoming is null || stored.Instance != incoming.Instance || stored.Phase != incoming.Phase ||
                stored.Kind != incoming.Kind || stored.Card != incoming.Card || stored.Character != incoming.Character ||
                stored.Source != incoming.Source || stored.FinalPhase != incoming.FinalPhase || stored.Week != incoming.Week ||
                stored.RaidType != incoming.RaidType)
                return false;
            return IsGrantReduction(stored.Grants, incoming.Grants);
        }
        catch (JsonException) { return false; }
    }

    internal static int PositiveAmount(ImmutableArray<InventoryReward> grants)
    {
        int amount = 0;
        foreach (var row in grants)
        {
            if (row.ItemId != ItemId) continue;
            if (row.Count < 0) throw new InvalidDataException("不渝之咏结算数量无效");
            amount = checked(amount + row.Count);
        }
        return amount;
    }

    static ImmutableArray<RaidRewardOffer> ClipOffers(ImmutableArray<RaidRewardOffer> offers, ref int remaining)
    {
        if (offers.IsDefaultOrEmpty) return offers;
        var next = offers.ToArray();
        bool changed = false;
        for (int index = 0; index < next.Length; index++)
        {
            var clipped = ClipOffer(next[index], ref remaining);
            if (ReferenceEquals(clipped, next[index])) continue;
            next[index] = clipped;
            changed = true;
        }
        return changed ? next.ToImmutableArray() : offers;
    }

    static RaidRewardOffer ClipOffer(RaidRewardOffer offer, ref int remaining)
    {
        if (offer.Grants.IsDefaultOrEmpty) return offer;
        var grants = offer.Grants.ToArray();
        bool changed = false;
        for (int index = 0; index < grants.Length; index++)
        {
            var row = grants[index];
            if (row.ItemId != ItemId || row.Count <= 0) continue;
            int take = Math.Min(row.Count, Math.Max(0, remaining));
            remaining -= take;
            if (take == row.Count) continue;
            grants[index] = row with { Count = take };
            changed = true;
        }
        return changed ? ApplyGrants(offer, grants.ToImmutableArray()) : offer;
    }

    static LukeRewardResultSpec? Retarget(LukeRewardResultSpec? spec, InventoryReward grant)
    {
        if (spec is null || grant.ItemId != ItemId || spec.ItemId != (uint)ItemId) return spec;
        ushort count = checked((ushort)grant.Count);
        return spec.DisplayNumber == count ? spec : spec with { DisplayNumber = count };
    }

    static AccountMaterialBalance Credit(AccountMaterialBalance balance, ushort character, AccountMaterialDefinition definition, int week, int granted)
    {
        var next = balance with { Balance = checked(balance.Balance + granted) };
        if (definition.WeeklyAccountLimit != 0 || definition.WeeklyCharacterLimit != 0)
            next = next.EarnRaid(character, week, granted);
        next.Validate();
        return next;
    }

    static bool IsGrantReduction(ImmutableArray<InventoryReward> stored, ImmutableArray<InventoryReward> incoming)
    {
        if (stored.IsDefault || incoming.IsDefault || stored.Length != incoming.Length) return false;
        bool reduced = false;
        for (int index = 0; index < stored.Length; index++)
        {
            var left = stored[index];
            var right = incoming[index];
            if (left.ItemId != ItemId || right.ItemId != ItemId)
            {
                if (left != right) return false;
                continue;
            }
            if (left with { Count = right.Count } != right || left.Count < 0 || left.Count > right.Count) return false;
            if (left.Count != right.Count) reduced = true;
        }
        return reduced;
    }

    static bool SameRows(ImmutableArray<RaidRewardDisplayRow> left, ImmutableArray<RaidRewardDisplayRow> right) =>
        (left.IsDefault ? 0 : left.Length) == (right.IsDefault ? 0 : right.Length) && (left.IsDefault || left.SequenceEqual(right));

    static bool SameSpecs(ImmutableArray<LukeRewardResultSpec> left, ImmutableArray<LukeRewardResultSpec> right) =>
        (left.IsDefault ? 0 : left.Length) == (right.IsDefault ? 0 : right.Length) && (left.IsDefault || left.SequenceEqual(right));
}
