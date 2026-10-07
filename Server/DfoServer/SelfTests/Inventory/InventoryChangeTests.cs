using System.Collections.Immutable;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;
using DfoServer.Network.Builders.Inventory;

namespace DfoServer.Testing.Inventory;

internal static class InventoryChangeTests
{
    public static void Run()
    {
        int count = TestCheck.Count;
        var random = new Random(1701);
        foreach (int size in new[] { 0, 1, 8, 64, 512 })
        {
            var before = new InventorySnapshot(Enumerable.Range(0, size).Select(i =>
                new InventoryItem(i + 1, new(InventoryListType.Main, (short)(i + 9)), new(1000 + i, ItemKind.Material, 3))).ToImmutableArray());
            for (int round = 0; round < 20; round++)
            {
                var items = before.Items.Where(_ => random.Next(4) != 0).Select(item => random.Next(2) == 0 ? item :
                    item with { Core = item.Core with { SortLockFlag = 1, Rune = 3, EnchantCardId = 17 } }).ToList();
                items.Add(new(0, new(InventoryListType.Main, (short)(size + 9)), new(2001, ItemKind.Material, 4)));
                items.Reverse();
                var after = before with { Items = items.ToImmutableArray(), Gold = 23 };
                var plan = new InventoryPlan(before, after, "synthetic");
                var expected = before.Items.Select(item => item.Position).Union(after.Items.Select(item => item.Position))
                    .Where(slot => before.Find(slot) != after.Find(slot)).OrderBy(slot => slot.List).ThenBy(slot => slot.Index).ToArray();
                TestCheck.That(plan.ChangedSlots.SequenceEqual(expected), "索引差量与原逐槽算法一致，含删除、追加、完整核心和顺序");
                TestCheck.That(plan.ChangedSlots == plan.ChangedSlots && plan.ChangedCurrencies.SequenceEqual([CurrencyKind.Gold]), "变化集合复用同一不可变数组");
                TestCheck.That(plan.BeforeIndex.FindUid(size) == before.Items.LastOrDefault() && plan.AfterIndex.FindUid(0) is null,
                    "UID 索引保留原快照，临时 UID 不参与实例查找");
                if (size == 8)
                    TestCheck.That(InventoryPackets.Delta(after, InventoryListType.Main, expected, 0).SequenceEqual(
                        InventoryPackets.Delta(after, InventoryListType.Main, expected, 0, plan.AfterIndex)), "索引不改变差量 wire");
            }
            var unchanged = new InventoryPlan(before, before with { Items = before.Items.Reverse().ToImmutableArray() }, "same");
            TestCheck.That(!unchanged.Changed && unchanged.ChangedSlots.IsEmpty, "快照顺序调整不生成资产变化");
        }
        Console.WriteLine($"PASS {TestCheck.Count - count} inventory change/index assertions");
    }
}
