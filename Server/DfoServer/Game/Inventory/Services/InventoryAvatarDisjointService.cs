using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Services;

internal sealed class InventoryAvatarDisjointService(ItemCatalog items, AvatarDisjointCatalog disjoint,
    GoldLimitCatalog goldLimits, EpicPieceCatalog pieces, Random? random = null, TimeProvider? clock = null)
{
    const int RequiredFreeEmblemSlots = 5;
    public TimeProvider Clock { get; } = clock ?? TimeProvider.System;

    public InventoryResult Disjoint(Character character, AvatarDisjointRequest request,
        out AvatarDisjointReceipt? receipt, out string? rejection, DateTimeOffset? utcNow = null)
    {
        receipt = null;
        rejection = "slot-or-instance";
        var before = character.Inventory;
        var source = before.Find(request.Position);
        var plannedAt = Clock.GetUtcNow();
        var now = utcNow ?? plannedAt;
        long nowUnix = now.ToUnixTimeSeconds();
        if (request.ExpectedItemId <= 0 || source is not { Core.Kind: ItemKind.Avatar, Avatar: not null } ||
            source.Core.ItemId != request.ExpectedItemId || source.Core.Count != 1 || source.Core.EquipmentLockId != 0 ||
            source.IsExpired(nowUnix) || !ItemSlotBoundService.Allows(before, request.Position, ItemKind.Avatar))
            return InventoryResult.Reject(19);

        try
        {
            rejection = "source-qualification";
            var sourceTemplate = items.Get(source.Core.ItemId);
            if (sourceTemplate.Kind != ItemKind.Avatar || sourceTemplate.DisjointForbidden)
                return InventoryResult.Reject(19);
            var table = disjoint.Get(sourceTemplate.Grade);
            if (table is null)
            {
                rejection = "grade-table";
                return InventoryResult.Reject(19);
            }

            rejection = "reward-definition";
            foreach (int itemId in table.Pools.SelectMany(pool => pool.Rewards).Select(reward => reward.ItemId).Distinct())
                if (items.Get(itemId).Kind != ItemKind.Emblem)
                    return InventoryResult.Reject(19);

            rejection = "emblem-space";
            var emblemRange = ItemSlotBoundService.Range(ItemKind.Emblem, before);
            var occupied = before.Items.Where(item => item.Position.List == InventoryListType.Main)
                .Select(item => item.Position.Index).ToHashSet();
            int free = Enumerable.Range(emblemRange.Start, emblemRange.End - emblemRange.Start + 1)
                .Count(slot => !occupied.Contains((short)slot));
            if (free < RequiredFreeEmblemSlots)
                return InventoryResult.Reject(4);

            rejection = "reward-roll";
            InventoryReward[] rewards = AvatarDisjointResultCalculator.Calculate(table, random ?? Random.Shared);
            if (rewards.Length == 0)
                return InventoryResult.Reject(19);

            rejection = "source-delete";
            var deleted = InventoryDeleteService.Plan(before, source.Position, source.Core.Value, 1);
            if (!deleted.Accepted)
                return InventoryResult.Reject(19);
            var paid = deleted.Plan!.After;

            rejection = "reward-capacity";
            var granted = InventoryRewardGrantService.TryGrantBatch(character with { Inventory = paid }, rewards,
                items, goldLimits, pieces, now);
            if (!granted.Accepted)
                return InventoryResult.Reject(granted.Error == 19 ? (ushort)19 : (ushort)4);
            var after = granted.Plan!.After;
            var received = new List<AvatarDisjointReward>();
            foreach (var reward in rewards)
            {
                int delivered = 0;
                foreach (var item in after.Items.Where(item => item.Position.List == InventoryListType.Main &&
                    item.Core.ItemId == reward.ItemId).OrderBy(item => item.Position.Index))
                {
                    int increment = item.Core.Count - (paid.Find(item.Position)?.Core.Count ?? 0);
                    if (increment <= 0)
                        continue;
                    delivered = checked(delivered + increment);
                    received.Add(new(item.Position.Index, reward.ItemId, increment));
                }
                if (delivered != reward.Count)
                    return InventoryResult.Reject(4);
            }
            if (received.Count is 0 or > byte.MaxValue)
                return InventoryResult.Reject(4);

            long? expires = source.ExpireTime > 0 ? source.ExpireTime : null;
            receipt = new(request, received.ToArray());
            rejection = null;
            return new(true, 0, new InventoryPlan(before, after, "avatar_disjoint", new(character, expires))
            {
                AvatarDisjoint = new(Guid.NewGuid(), plannedAt, Clock, request, receipt,
                    receipt.Rewards.ToImmutableArray(), source, before, paid, after)
            });
        }
        catch (Exception error) when (error is InvalidDataException or OverflowException or KeyNotFoundException)
        {
            rejection = "invalid-resource";
            return InventoryResult.Reject(19);
        }
    }
}
