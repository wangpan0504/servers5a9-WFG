using System.Collections.Immutable;

namespace DfoServer.Game.Inventory.Models;

internal sealed record MonsterCardUpgradeMaterial(InventoryItem Item, int Quantity);

// 仅原服务的成功分支生成；失败耗材是原业务结果，不伪装为卡片等级提升。
internal sealed record MonsterCardUpgradeFact(Guid Id, DateTimeOffset PlannedAt, TimeProvider Clock,
    MonsterCardUpgradeRequest Request, MonsterCardUpgradeReceipt Receipt, InventoryItem Target,
    ImmutableArray<MonsterCardUpgradeMaterial> Materials, InventorySnapshot Before,
    InventorySnapshot Paid, InventorySnapshot After)
{
    public bool Matches(InventoryPlan plan, InventorySnapshot inventory)
    {
        if (Id == Guid.Empty || plan.Reason != "monster_card_upgrade" || !Receipt.Upgraded ||
            !ReferenceEquals(plan.Before, Before) || !ReferenceEquals(plan.After, After) ||
            Request.MaterialSlots.IsDefaultOrEmpty || Request.MaterialSlots.Length > 4 || Materials.IsDefaultOrEmpty ||
            Target.Uid <= 0 || Target.Core.Count < 1 || !Target.Core.IsStackable ||
            Target.Position != new InventorySlot(InventoryListType.Main, Request.TargetSlot) ||
            plan.BeforeIndex.Find(Target.Position) != Target || Receipt.ItemId != Target.Core.ItemId ||
            Target.Core.EnchantUpgrade == byte.MaxValue || Receipt.Level != Target.Core.EnchantUpgrade + 1 ||
            Materials.Select(row => row.Item.Position).Distinct().Count() != Materials.Length) return false;
        var groups = Request.MaterialSlots.GroupBy(slot => slot).ToArray();
        if (groups.Length != Materials.Length) return false;
        var paidItems = Before.Items.ToBuilder();
        foreach (var material in Materials)
        {
            var item = material.Item;
            var group = groups.SingleOrDefault(row => row.Key == item.Position.Index);
            if (item.Uid <= 0 || item.Position.List != InventoryListType.Main || group is null ||
                material.Quantity != group.Count() || material.Quantity <= 0 || material.Quantity > item.Core.Count ||
                plan.BeforeIndex.Find(item.Position) != item) return false;
            int index = paidItems.IndexOf(item); if (index < 0) return false;
            if (item.Core.Count == material.Quantity) paidItems.RemoveAt(index);
            else paidItems[index] = item with { Core = item.Core with { Value = item.Core.Value - material.Quantity } };
        }
        if (!Paid.Items.SequenceEqual(paidItems) || (Paid with { Items = Before.Items }) != Before ||
            (After with { Items = Paid.Items }) != Paid) return false;
        var paidTarget = Paid.Find(Target.Position);
        if (paidTarget is null || paidTarget.Uid != Target.Uid || paidTarget.Core.Count < 1) return false;
        var upgraded = Target with { Core = Target.Core with { Value = 1, EnchantUpgrade = Receipt.Level } };
        if (paidTarget.Core.Count == 1)
        {
            if (!After.Items.SequenceEqual(Paid.Items.Replace(paidTarget, upgraded))) return false;
        }
        else
        {
            var remaining = Paid.Items.Replace(paidTarget,
                paidTarget with { Core = paidTarget.Core with { Value = paidTarget.Core.Value - 1 } });
            var afterIndex = new InventoryIndex(After); long granted = 0;
            foreach (var item in remaining)
            {
                var current = afterIndex.FindUid(item.Uid);
                if (current is null || current.Core.Count < item.Core.Count ||
                    current != (item with { Core = item.Core with { Value = current.Core.Value } })) return false;
                long delta = current.Core.Count - item.Core.Count;
                // 已有堆的UID、位置和完整核心已在上方复验；奖励合堆沿公共规则保留该堆的排序锁。
                if (delta != 0 && (current.Core with { Value = 1, SortLockFlag = upgraded.Core.SortLockFlag }) != upgraded.Core) return false;
                granted += delta;
            }
            foreach (var item in After.Items.Where(item => !remaining.Any(old => old.Uid == item.Uid)))
            {
                if (item.Uid != 0 || item.Core != upgraded.Core || item.Avatar != upgraded.Avatar ||
                    item.Creature != upgraded.Creature) return false;
                granted += item.Core.Count;
            }
            if (granted != 1) return false;
        }
        if (inventory.Items.Length != After.Items.Length) return false;
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
