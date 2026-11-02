using System.Collections.Immutable;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Models;

internal sealed record EmblemCompoundMaterial(InventoryItem Item, int Quantity, int Grade);
internal sealed record EmblemCompoundReward(int ItemId, int Grade, int Rarity);

// 保留原扣源后的快照与已抽定奖励；同ID叠回不通过最终净增加量推定成功。
internal sealed record EmblemCompoundFact(Guid Id, DateTimeOffset PlannedAt, TimeProvider Clock,
    EmblemCompoundRequest Request, EmblemCompoundReceipt Receipt,
    ImmutableArray<EmblemCompoundMaterial> Materials, EmblemCompoundReward Reward,
    InventorySnapshot Before, InventorySnapshot Paid, InventorySnapshot After)
{
    public bool Matches(InventoryPlan plan, InventorySnapshot inventory)
    {
        if (Id == Guid.Empty || plan.Reason != "emblem_compound" ||
            !ReferenceEquals(plan.Before, Before) || !ReferenceEquals(plan.After, After) ||
            Request.Inputs.Length is < 2 or > 5 || Materials.IsDefaultOrEmpty ||
            Materials.Select(row => row.Item.Position).Distinct().Count() != Materials.Length ||
            Receipt.InputCount != Request.Inputs.Length || Receipt.RewardItemId != Reward.ItemId ||
            Receipt.RewardCount <= 0 || Reward.Grade <= 0 || Reward.Rarity <= 0 ||
            Receipt.MinimumGrade != Materials.Min(row => row.Grade)) return false;
        var groups = Request.Inputs.GroupBy(row => row.Position).ToArray();
        if (groups.Length != Materials.Length) return false;
        var expectedPaid = Before.Items.ToBuilder();
        foreach (var material in Materials)
        {
            var item = material.Item;
            var group = groups.SingleOrDefault(group => group.Key == item.Position);
            if (item.Uid <= 0 || item.Core.Kind != ItemKind.Emblem || material.Grade <= 0 ||
                material.Quantity <= 0 || material.Quantity > item.Core.Count ||
                group is null || group.Count() != material.Quantity || group.Any(row => row.ItemId != item.Core.ItemId) ||
                plan.BeforeIndex.Find(item.Position) != item) return false;
            int index = expectedPaid.IndexOf(item);
            if (index < 0) return false;
            if (item.Core.Count == material.Quantity) expectedPaid.RemoveAt(index);
            else expectedPaid[index] = item with { Core = item.Core with { Value = item.Core.Value - material.Quantity } };
        }
        if (!Paid.Items.SequenceEqual(expectedPaid) || (Paid with { Items = Before.Items }) != Before ||
            (After with { Items = Paid.Items }) != Paid) return false;
        var paidIndex = new InventoryIndex(Paid);
        var afterIndex = new InventoryIndex(After);
        if (Paid.Items.Where(item => item.Core.ItemId != Reward.ItemId).Any(item => afterIndex.Find(item.Position) != item) ||
            After.Items.Where(item => item.Core.ItemId != Reward.ItemId).Any(item => paidIndex.Find(item.Position) != item) ||
            After.Items.Where(item => item.Core.ItemId == Reward.ItemId).Sum(item => (long)item.Core.Count) -
            Paid.Items.Where(item => item.Core.ItemId == Reward.ItemId).Sum(item => (long)item.Core.Count) != Receipt.RewardCount)
            return false;
        foreach (var old in Paid.Items.Where(item => item.Core.ItemId == Reward.ItemId))
        {
            var current = afterIndex.FindUid(old.Uid);
            if (current is null || current.Core.Count < old.Core.Count ||
                current != (old with { Core = old.Core with { Value = current.Core.Value } })) return false;
        }
        if (inventory.Items.Length != After.Items.Length) return false;
        var actual = new InventoryIndex(inventory);
        foreach (var expected in After.Items)
        {
            var current = actual.Find(expected.Position);
            if (current is null || current.Uid <= 0 ||
                expected.Uid > 0 && current.Uid != expected.Uid ||
                expected.Uid == 0 && plan.BeforeIndex.FindUid(current.Uid) is not null ||
                (current with { Uid = expected.Uid }) != expected) return false;
        }
        return true;
    }
}
