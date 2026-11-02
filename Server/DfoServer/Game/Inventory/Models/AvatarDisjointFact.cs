using System.Collections.Immutable;
using DfoServer.Game.Currency;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Models;

// 一件原装扮实例的分解事实；已抽定的完整徽章增量与源详情同事务冻结。
internal sealed record AvatarDisjointFact(Guid Id, DateTimeOffset PlannedAt, TimeProvider Clock,
    AvatarDisjointRequest Request, AvatarDisjointReceipt Receipt, ImmutableArray<AvatarDisjointReward> Rewards,
    InventoryItem Source, InventorySnapshot Before, InventorySnapshot Paid, InventorySnapshot After)
{
    public bool Matches(InventoryPlan plan, InventorySnapshot inventory)
    {
        if (Id == Guid.Empty || plan.Reason != "avatar_disjoint" ||
            !ReferenceEquals(plan.Before, Before) || !ReferenceEquals(plan.After, After) ||
            Source.Uid <= 0 || Source.Position != Request.Position || Source.Core.ItemId != Request.ExpectedItemId ||
            Source.Position.List != InventoryListType.Avatar || Source.Core.Kind != ItemKind.Avatar ||
            Source.Core.Count != 1 || Source.Avatar is null || plan.BeforeIndex.Find(Source.Position) != Source ||
            Receipt.Request != Request || Rewards.IsDefaultOrEmpty || !Receipt.Rewards.SequenceEqual(Rewards) ||
            Rewards.Any(row => row.Slot < 0 || row.ItemId <= 0 || row.Count <= 0) ||
            Rewards.Select(row => row.Slot).Distinct().Count() != Rewards.Length) return false;
        var removed = Before.Items.ToBuilder(); if (!removed.Remove(Source)) return false;
        if (!Paid.Items.SequenceEqual(removed) || (Paid with { Items = Before.Items }) != Before ||
            (After with { Items = Paid.Items }) != Paid || After.Items.Any(item => item.Uid == Source.Uid)) return false;
        var paidIndex = new InventoryIndex(Paid); var afterIndex = new InventoryIndex(After);
        var rewardIds = Rewards.Select(row => row.ItemId).ToHashSet();
        foreach (var old in Paid.Items)
        {
            var current = afterIndex.Find(old.Position);
            if (!rewardIds.Contains(old.Core.ItemId)) { if (current != old) return false; }
            else if (current is null || !old.Core.IsStackable || current.Core.Count < old.Core.Count ||
                current != (old with { Core = old.Core with { Value = current.Core.Value } })) return false;
        }
        var deltas = new List<AvatarDisjointReward>();
        foreach (var item in After.Items)
        {
            var previous = paidIndex.Find(item.Position);
            if (item == previous) continue;
            if (item.Position.List != InventoryListType.Main || item.Core.Kind != ItemKind.Emblem ||
                !rewardIds.Contains(item.Core.ItemId) || previous is not null && previous.Core.ItemId != item.Core.ItemId)
                return false;
            int delta = checked(item.Core.Count - (previous?.Core.Count ?? 0)); if (delta <= 0) return false;
            deltas.Add(new(item.Position.Index, item.Core.ItemId, delta));
        }
        if (!Rewards.OrderBy(row => row.Slot).SequenceEqual(deltas.OrderBy(row => row.Slot)) ||
            inventory.Items.Length != After.Items.Length || !inventory.EpicPieces.Same(After.EpicPieces) ||
            Enum.GetValues<CurrencyKind>().Any(kind => CurrencyWallet.Get(inventory, kind) != CurrencyWallet.Get(After, kind)))
            return false;
        var actual = new InventoryIndex(inventory);
        foreach (var expected in After.Items)
        {
            var current = actual.Find(expected.Position);
            if (current is null || current.Uid <= 0 || expected.Uid > 0 && current.Uid != expected.Uid ||
                expected.Uid == 0 && plan.BeforeIndex.FindUid(current.Uid) is not null ||
                (current with { Uid = expected.Uid }) != expected) return false;
        }
        return true;
    }
}
