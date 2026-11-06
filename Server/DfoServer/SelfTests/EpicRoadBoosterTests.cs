using System.Text.Json;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing.Inventory;

internal static class EpicRoadBoosterTests
{
    // 逐项来自两外盒及四子盒的原始资源；保留顺序，不把子盒替换为整套装备。
    static readonly (int Parent, int Child, int[] Leaves)[] Choices =
    [
        (490013718, 490013720, [100312640, 100300946, 100322533]),
        (490013718, 490013721, [100312641, 100300947, 100322534]),
        (490013719, 490013722, [100344897, 100353080, 100390020]),
        (490013719, 490013723, [100344898, 100353081, 100390021])
    ];

    public static void RunResources(TownCatalog towns, CharacterCatalog characters, ItemResources resources, DateTimeOffset now)
    {
        int checks = Count;
        var service = new InventoryBoosterService(resources.Items, resources.Boosters, resources.GoldLimits,
            resources.EpicPieces, contracts: resources.Contracts);
        var failures = new List<string>();
        foreach (int parentId in Choices.Select(row => row.Parent).Distinct())
            CheckDefinition(parentId, Choices.Where(row => row.Parent == parentId).Select(row => row.Child).ToArray(), false);
        foreach (var entry in Choices)
        {
            CheckDefinition(entry.Child, entry.Leaves, true);
            var parentTemplate = resources.Items.Get(entry.Parent);
            var parentRoles = BoosterNativeSelectionTests.ResourceRoles(towns, characters, parentTemplate, now).ToArray();
            That(parentRoles.Length > 0, "星河外盒至少有一个真实资源合法职业 " + entry.Parent);
            foreach (var legal in parentRoles)
            {
                var role = Role(legal, entry.Parent);
                CheckParent(role, entry.Child, "普通容量");
            }

            var sample = Role(parentRoles[0], entry.Parent);
            var existingChild = new InventoryItem(10003, Bag(66),
                InventoryRewardGrantService.Create(resources.Items.Get(entry.Child), 5, now));
            var stacked = sample with { Inventory = sample.Inventory with { Items = sample.Inventory.Items.SetItem(0,
                sample.Inventory.Items[0] with { Core = sample.Inventory.Items[0].Core with { Value = 3 } }).Add(existingChild) } };
            string stackedBefore = JsonSerializer.Serialize(stacked);
            var batch = service.Open(stacked, new(3, 2, new(0, 0, [entry.Child])), out var batchReceipt, now);
            That(batch.Accepted && batch.Plan is not null && batchReceipt is not null,
                "星河父盒批量选择仍按普通消耗品计数发放，实际error=" + batch.Error);
            var expectedSource = stacked.Inventory.Find(Bag(3))! with
                { Core = stacked.Inventory.Find(Bag(3))!.Core with { Value = 1 } };
            var expectedStack = existingChild with { Core = existingChild.Core with { Value = 7 } };
            That(batch.Plan!.After.Find(Bag(3)) == expectedSource && batch.Plan.After.Find(Bag(66)) == expectedStack &&
                batch.Plan.After.Items.Length == stacked.Inventory.Items.Length &&
                batch.Plan.After.Find(Bag(4)) == stacked.Inventory.Find(Bag(4)) &&
                batch.Plan.After.Find(Bag(65)) == stacked.Inventory.Find(Bag(65)) &&
                batchReceipt!.Rewards.SequenceEqual(new[] { new BoosterGrantedItem(entry.Child, 2) }) &&
                JsonSerializer.Serialize(stacked) == stackedBefore,
                "星河父盒一次开两个仅扣两个源，合并既有子盒堆并保留UID、完整核心、哨兵和输入 " + entry.Parent + ":" + entry.Child);
            var equipmentRange = ItemSlotBoundService.Range(ItemKind.Equipment, sample.Inventory);
            var fullEquipment = sample with { Inventory = sample.Inventory with { Items = sample.Inventory.Items.AddRange(
                Enumerable.Range(equipmentRange.Start, equipmentRange.End - equipmentRange.Start + 1).Select(slot =>
                    new InventoryItem(20000 + slot, Bag((short)slot),
                        InventoryRewardGrantService.Create(resources.Items.Get(entry.Leaves[0]), utcNow: now)))) } };
            CheckParent(fullEquipment, entry.Child, "装备页满不妨碍领取消耗品子盒");

            var consumableRange = ItemSlotBoundService.Range(ItemKind.Consumable, sample.Inventory);
            var sentinel = sample.Inventory.Find(Bag(65))!;
            var fullConsumables = sample with { Inventory = sample.Inventory with { Items =
                [sample.Inventory.Find(Bag(3))!, sample.Inventory.Find(Bag(4))!,
                    .. Enumerable.Range(consumableRange.Start, consumableRange.End - consumableRange.Start + 1)
                        .Select(slot => new InventoryItem(30000 + slot, Bag((short)slot), sentinel.Core))] } };
            string fullBefore = JsonSerializer.Serialize(fullConsumables);
            var denied = service.Open(fullConsumables, Select(3, entry.Child), out var deniedReceipt, now);
            That(JsonSerializer.Serialize(fullConsumables) == fullBefore,
                "星河消耗品满页规划不改变输入 " + entry.Parent + ":" + entry.Child);
            if (denied.Accepted || denied.Error != 4 || denied.Plan is not null || deniedReceipt is not null)
                failures.Add($"{entry.Parent}:{entry.Child} 消耗品页满应拒绝4且无计划，实际accepted={denied.Accepted}/error={denied.Error}");

            // 子盒另一次真实选择才生成装备；不把父盒未提交的UID=0结果当成合法源实例。
            var childTemplate = resources.Items.Get(entry.Child);
            var childRoles = BoosterNativeSelectionTests.ResourceRoles(towns, characters, childTemplate, now).ToArray();
            That(childRoles.Length > 0, "星河子盒至少有一个真实资源合法职业 " + entry.Child);
            foreach (var legal in childRoles)
                foreach (int leafId in entry.Leaves)
                    CheckLeaf(Role(legal, entry.Child), entry.Child, leafId);
        }
        That(failures.Count == 0, "星河两外盒四选项和十二装备合法请求应完整成功：" + string.Join(" | ", failures));
        Console.WriteLine($"PASS {Count - checks} 星河外盒四选项、完整子盒与十二装备实例限制断言（非实机）");

        void CheckDefinition(int sourceId, int[] expected, bool equipment)
        {
            var definition = resources.Boosters.Get(sourceId);
            That(definition is { Available: true, SetsDisjointInstanceType: true, SetsEpicExchangeInstanceType: true,
                    Selection: { SelectionCount: 1, Categories.Length: 1 } },
                "星河真实选择盒保留双旗、选择一项和唯一分类 " + sourceId);
            var category = definition.Selection!.Categories.Single();
            That(category.Category == 0 && category.Subcategory == 0 &&
                category.Rewards.Select(row => row.ItemId).SequenceEqual(expected) &&
                category.Rewards.All(row => row.Count == 1 && row.Upgrade == 0 && row.Refinement == 0 &&
                    row.UpgradeChances.IsEmpty),
                "星河真实候选保持完整顺序、各一件及原始装备分类 " + sourceId);
            That(resources.Items.Get(sourceId) is { Kind: ItemKind.Consumable } source &&
                source.AttachType.Contains("[account]", StringComparison.OrdinalIgnoreCase) &&
                expected.All(id => resources.Items.Get(id).Kind == (equipment ? ItemKind.Equipment : ItemKind.Consumable)),
                "星河源盒账号绑定，候选保持真实模板Kind " + sourceId);
            That(service.UsePlaces(sourceId) is { Village: true, SeriaRoom: true, Dungeon: false, PendingRule: null },
                "星河盒子使用地点仍为村庄或赛丽亚 " + sourceId);
        }

        Character Role(Character legal, int sourceId)
        {
            var source = InventoryRewardGrantService.Create(resources.Items.Get(sourceId), utcNow: now);
            var sentinel = InventoryRewardGrantService.Create(resources.Items.Get(1106), utcNow: now);
            return legal with { Inventory = new([
                new(10000, Bag(3), source), new(10001, Bag(4), source), new(10002, Bag(65), sentinel)]) { Gold = 12345 } };
        }

        void CheckParent(Character role, int childId, string scenario)
        {
            string before = JsonSerializer.Serialize(role);
            int parentId = role.Inventory.Find(Bag(3))!.Core.ItemId;
            var opened = service.Open(role, Select(3, childId), out var receipt, now);
            That(JsonSerializer.Serialize(role) == before, "星河外盒规划不修改输入角色 " + parentId + ":" + childId);
            if (!opened.Accepted || opened.Plan is null || receipt is null)
            {
                failures.Add($"{parentId}:{childId} {scenario}/job={role.Job} 应成功，实际error={opened.Error}");
                return;
            }
            var after = opened.Plan.After;
            var granted = after.Items.Single(item => item.Core.ItemId == childId);
            var expected = InventoryRewardGrantService.Create(resources.Items.Get(childId), utcNow: now);
            That(InventoryCargoService.AllowsInsert(expected, InventoryListType.AccountCargo, resources.Items.Get(childId)),
                "星河子盒按自身账号绑定模板允许账号仓，不借父盒实例补绑定 " + childId);
            var range = ItemSlotBoundService.Range(ItemKind.Consumable, role.Inventory);
            That(granted.Uid == 0 && granted.Core == expected && granted.Core.Count == 1 &&
                granted.Position.List == InventoryListType.Main && granted.Position.Index >= range.Start && granted.Position.Index <= range.End &&
                granted.Core is { DisjointForbidden: false, DestructionSoulForbidden: false, EpicExchangeOrigin: null, Extension119: 0 } &&
                after.Find(Bag(3)) is null && after.Find(Bag(4)) == role.Inventory.Find(Bag(4)) &&
                after.Items.Length == role.Inventory.Items.Length && after.Gold == role.Inventory.Gold &&
                role.Inventory.Items.Where(item => item.Position != Bag(3)).All(item => after.Find(item.Position) == item) &&
                receipt.Rewards.SequenceEqual(new[] { new BoosterGrantedItem(childId, 1) }) && opened.Plan.OverflowItems.IsEmpty &&
                opened.Plan.ChangedCurrencies.IsEmpty && opened.Plan.ChangedEpicPieces.IsEmpty,
                "星河外盒只消费指定一盒，生成完整消耗品子盒，不代开或传播装备限制 " + parentId + ":" + childId + ":" + scenario);
        }

        void CheckLeaf(Character role, int childId, int leafId)
        {
            string before = JsonSerializer.Serialize(role);
            var opened = service.Open(role, Select(3, leafId), out var receipt, now);
            That(JsonSerializer.Serialize(role) == before, "星河子盒规划不修改输入角色 " + childId + ":" + leafId);
            if (!opened.Accepted || opened.Plan is null || receipt is null)
            {
                failures.Add($"{childId}:{leafId} job={role.Job} 子盒应成功，实际error={opened.Error}");
                return;
            }
            var after = opened.Plan.After;
            var granted = after.Items.Single(item => item.Core.ItemId == leafId);
            var plain = InventoryRewardGrantService.Create(resources.Items.Get(leafId), utcNow: now);
            That(!InventoryCargoService.AllowsInsert(plain, InventoryListType.AccountCargo, resources.Items.Get(leafId)),
                "星河最终装备按自身不可交易模板拒绝账号仓，不继承源礼盒的账号绑定 " + leafId);
            var range = ItemSlotBoundService.Range(ItemKind.Equipment, role.Inventory);
            // 这里只核两项资源声明；毁装灵魂说明与其独立合同另行核定，不反推或抹去该实例字段。
            var unmarked = granted.Core with { DisjointForbidden = false, DestructionSoulForbidden = false,
                EpicExchangeOrigin = null, Extension119 = 0 };
            That(granted.Uid == 0 && unmarked == plain && granted.Core.DisjointForbidden &&
                granted.Core.EpicExchangeOrigin == new EpicExchangeOrigin(1, childId) && granted.Core.Extension119 == 0 &&
                granted.Position.List == InventoryListType.Main && granted.Position.Index >= range.Start && granted.Position.Index <= range.End &&
                after.Find(Bag(3)) is null && after.Find(Bag(4)) == role.Inventory.Find(Bag(4)) &&
                after.Find(Bag(65)) == role.Inventory.Find(Bag(65)) && after.Items.Length == 3 && after.Gold == role.Inventory.Gold &&
                receipt.Rewards.SequenceEqual(new[] { new BoosterGrantedItem(leafId, 1) }) && opened.Plan.OverflowItems.IsEmpty,
                "星河子盒每次仅选原池一件，最终装备保留来源、禁止分解和史诗交换且其它核心保持 " + childId + ":" + leafId);
        }
    }

    static BoosterOpenRequest Select(short slot, int id) => new(slot, 1, new(0, 0, [id]));
    static InventorySlot Bag(short slot) => new(InventoryListType.Main, slot);
}
