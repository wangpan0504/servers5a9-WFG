using System.Collections.Immutable;

namespace DfoServer.Game.Inventory.Models;

internal sealed record MonsterCardBindMaterial(InventoryItem Item, int Quantity, int Rarity);

// 冻结原合成器、逐源张数和已抽定结果；同ID奖励叠回不能用最终净变化推定合成次数。
internal sealed record MonsterCardBindFact(Guid Id, DateTimeOffset PlannedAt, TimeProvider Clock,
    MonsterCardBindRequest Request, MonsterCardBindReceipt Receipt, InventoryItem Synthesizer,
    int BindType, ImmutableArray<MonsterCardBindMaterial> Materials, int RewardItemId,
    InventorySnapshot Before, InventorySnapshot Paid, InventorySnapshot After)
{
    public bool Matches(InventoryPlan plan, InventorySnapshot inventory)
    {
        bool sphere = BindType is >= 3 and <= 5;
        if (Id == Guid.Empty || plan.Reason != "monster_card_bind" ||
            !ReferenceEquals(plan.Before, Before) || !ReferenceEquals(plan.After, After) ||
            Request.CardSlots.IsDefaultOrEmpty || Request.CardSlots.Length is < 2 or > 5 ||
            !sphere && Request.CardSlots.Length != 2 || Materials.IsDefaultOrEmpty || BindType < 0 ||
            Synthesizer.Uid <= 0 || Synthesizer.Core.Count < 1 || !Synthesizer.Core.IsStackable ||
            Synthesizer.Position != Request.SynthesizerPosition || plan.BeforeIndex.Find(Synthesizer.Position) != Synthesizer ||
            Receipt.SynthesizerSlot != Request.SynthesizerSlot || Receipt.BindType != BindType ||
            Receipt.InputRarity < 0 || Receipt.OutputRarity < 0 ||
            Receipt.RarityUpgraded != (Receipt.OutputRarity != Receipt.InputRarity) ||
            !Receipt.DeletedSlots.SequenceEqual(Request.CardSlots) || RewardItemId <= 0 ||
            Materials.Select(row => row.Item.Position).Distinct().Count() != Materials.Length) return false;
        var groups = Request.CardSlots.GroupBy(slot => slot).ToArray();
        if (groups.Length != Materials.Length) return false;
        var paidItems = Before.Items.ToBuilder();
        int synthesizerIndex = paidItems.IndexOf(Synthesizer);
        if (synthesizerIndex < 0) return false;
        if (Synthesizer.Core.Count == 1) paidItems.RemoveAt(synthesizerIndex);
        else paidItems[synthesizerIndex] = Synthesizer with { Core = Synthesizer.Core with { Value = Synthesizer.Core.Value - 1 } };
        foreach (var material in Materials)
        {
            var item = material.Item;
            var group = groups.SingleOrDefault(row => row.Key == item.Position.Index);
            if (item.Uid <= 0 || item.Position.List != InventoryListType.Main || item.Position == Synthesizer.Position ||
                !item.Core.IsStackable || material.Rarity != Receipt.InputRarity || group is null ||
                material.Quantity != group.Count() || material.Quantity <= 0 || material.Quantity > item.Core.Count ||
                plan.BeforeIndex.Find(item.Position) != item) return false;
            int index = paidItems.IndexOf(item); if (index < 0) return false;
            if (item.Core.Count == material.Quantity) paidItems.RemoveAt(index);
            else paidItems[index] = item with { Core = item.Core with { Value = item.Core.Value - material.Quantity } };
        }
        if (!Paid.Items.SequenceEqual(paidItems) || (Paid with { Items = Before.Items }) != Before ||
            (After with { Items = Paid.Items }) != Paid) return false;
        var paidIndex = new InventoryIndex(Paid);
        var afterIndex = new InventoryIndex(After);
        if (Paid.Items.Where(item => item.Core.ItemId != RewardItemId).Any(item => afterIndex.Find(item.Position) != item) ||
            After.Items.Where(item => item.Core.ItemId != RewardItemId).Any(item => paidIndex.Find(item.Position) != item) ||
            After.Items.Where(item => item.Core.ItemId == RewardItemId).Sum(item => (long)item.Core.Count) -
            Paid.Items.Where(item => item.Core.ItemId == RewardItemId).Sum(item => (long)item.Core.Count) != 1) return false;
        foreach (var old in Paid.Items.Where(item => item.Core.ItemId == RewardItemId))
        {
            var current = afterIndex.FindUid(old.Uid);
            if (current is null || current.Core.Count < old.Core.Count ||
                current != (old with { Core = old.Core with { Value = current.Core.Value } })) return false;
        }
        var rewards = After.Items.Where(item => item.Core.ItemId == RewardItemId && paidIndex.Find(item.Position) != item)
            .Select(item => new MonsterCardBindReward(item.Position.Index, item.Core.ItemId, item.Core.Count, 0));
        if (Receipt.Rewards.IsDefaultOrEmpty || !Receipt.Rewards.SequenceEqual(rewards) || inventory.Items.Length != After.Items.Length) return false;
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
