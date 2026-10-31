using System.Collections.Concurrent;
using System.Reflection;
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

internal static class BoosterDisjointNestedSelectionTests
{
    const int SourceId = 490011785;
    static readonly int[] ChildIds = [490011783, 490011826, 490011827, 490011828];

    public static void RunResources(TownCatalog towns, CharacterCatalog characters, ItemResources resources, DateTimeOffset now)
    {
        int checks = Count;
        RunOrdinaryParents(towns, resources);
        var items = resources.Items;
        var definition = resources.Boosters.Get(SourceId);
        That(definition.Available && definition.SetsDisjointInstanceType && !definition.SetsEpicExchangeInstanceType &&
            definition.Selection is { SelectionCount: 1, Categories.Length: 1 },
            "806 真实任意部位盒保留单次根级分解声明、选择1与唯一分类");
        var group = definition.Selection!.Categories.Single();
        That(group.Category == 0 && group.Subcategory == 0 && group.Rewards.Select(row => row.ItemId).SequenceEqual(ChildIds) &&
            group.Rewards.All(row => row.Count == 1 && row.Upgrade == 0 && row.Refinement == 0 && row.UpgradeChances.IsEmpty),
            "806 真实任意部位盒保留武器、防具、首饰、特殊装备四子盒的原顺序与各1个");
        var sourceTemplate = items.Get(SourceId);
        That(sourceTemplate.Kind == ItemKind.Consumable && items.Get(1106).Kind == ItemKind.Consumable &&
            ItemSlotBoundService.Allows(InventorySnapshot.Empty, Bag(3), sourceTemplate.Kind) &&
            ItemSlotBoundService.Allows(InventorySnapshot.Empty, Bag(65), ItemKind.Consumable),
            "806 源盒和哨兵使用真实消耗品模板及合法槽位，不假定源可堆叠2个");
        foreach (int id in ChildIds)
            That(items.Get(id).Kind == ItemKind.Consumable && resources.Boosters.Get(id) is { Available: true, Selection: not null },
                "806 四个实际候选均为真实可用自选消耗品子盒 " + id);

        var roles = BoosterNativeSelectionTests.ResourceRoles(towns, characters, sourceTemplate, now)
            .Select(role => role with { Inventory = new([
                new(10000, Bag(3), InventoryRewardGrantService.Create(sourceTemplate, utcNow: now)),
                new(10001, Bag(65), InventoryRewardGrantService.Create(items.Get(1106), utcNow: now))]) { Gold = 12345 } })
            .ToArray();
        That(roles.Length > 0, "806 真实源资格至少存在一个合法职业");
        RunBoundaries(resources, definition, roles[0], now);
        Console.WriteLine($"PASS {Count - checks} 806 真实源、四候选与非选择/史诗/特殊owner拒绝边界断言");

        var service = new InventoryBoosterService(items, resources.Boosters, resources.GoldLimits, resources.EpicPieces,
            contracts: resources.Contracts);
        var denied = new Dictionary<int, ushort>();
        int openings = 0;
        foreach (var baseRole in roles)
            foreach (byte level in new[] { baseRole.Level, (byte)95 }.Distinct().Where(level => level <= sourceTemplate.MaximumLevel))
                foreach (int id in ChildIds)
                {
                    var role = baseRole with { Level = level };
                    string before = JsonSerializer.Serialize(role);
                    var result = service.Open(role, Select(id), out var receipt, now);
                    That(JsonSerializer.Serialize(role) == before, "806 规划无论成功或拒绝均不修改输入角色 " + id);
                    if (!result.Accepted || result.Plan is null || receipt is null)
                    {
                        denied.TryAdd(id, result.Error);
                        continue;
                    }
                    openings++;
                    var expectedCore = InventoryRewardGrantService.Create(items.Get(id), utcNow: now);
                    var expected = role.Inventory with { Items = [role.Inventory.Items[1], new(0, Bag(66), expectedCore)] };
                    That(JsonSerializer.Serialize(result.Plan.After) == JsonSerializer.Serialize(expected) &&
                        result.Plan.Before == role.Inventory && result.Plan.OverflowItems.IsEmpty &&
                        result.Plan.ChangedSlots.SequenceEqual(new[] { Bag(3), Bag(66) }) &&
                        result.Plan.ChangedCurrencies.IsEmpty && result.Plan.ChangedEpicPieces.IsEmpty &&
                        receipt.Rewards.SequenceEqual(new[] { new BoosterGrantedItem(id, 1) }),
                        "806 各合法职业/等级领取完整单个子盒，耗尽源且保留哨兵、金币与全部其他资产 " + id + ":" + role.Job + ":" + level);
                    var granted = result.Plan.After.Find(Bag(66))!.Core;
                    That(granted is { EpicExchangeOrigin: null, Extension119: 0 } &&
                        result.Plan.CharacterPrecondition!.MatchesIdentity(role) &&
                        !result.Plan.CharacterPrecondition.MatchesIdentity(role with { GrowType = 1 }),
                        "806 父盒不向消耗品子盒传播史诗来源或实例位，原角色资格快照继续约束计划 " + id);
                }
        That(denied.Count == 0,
            "806 任意部位盒应允许四种真实自选子盒，实际拒绝：" + string.Join(",", denied.Select(row => row.Key + ":" + row.Value)));
        Console.WriteLine($"PASS {Count - checks} 806 分解声明嵌套自选盒断言，{openings}次真实合法选择规划");
    }

    public static void RunOrdinaryParents(TownCatalog towns, ItemResources resources)
    {
        int checks = Count;
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var service = new InventoryBoosterService(resources.Items, resources.Boosters, resources.GoldLimits,
            resources.EpicPieces, contracts: resources.Contracts);
        foreach (var entry in new[] { (Source: 490011382, Children: new[] { 490011404, 490011405 }),
            (Source: 490012852, Children: new[] { 490012869, 490012871 }) })
        {
            var parent = resources.Boosters.Get(entry.Source);
            That(parent is { Available: true, Selection: null, Lottery: false, SetsDisjointInstanceType: true,
                SetsEpicExchangeInstanceType: false, Pools.Length: 2 } &&
                parent.Pools.All(pool => pool is { Draws: 1, Rewards: [{ Weight: 1000, Count: 1 }] }) &&
                parent.Pools.Select(pool => pool.Rewards[0].ItemId).SequenceEqual(entry.Children),
                "两款达成礼包保持两个独立固定子盒池 " + entry.Source);
            var source = InventoryRewardGrantService.Create(resources.Items.Get(entry.Source), utcNow: now);
            var sentinel = new InventoryItem(3, Bag(65), InventoryRewardGrantService.Create(resources.Items.Get(1106), utcNow: now));
            var role = new Character(1, 0, "ParentBox", 0, towns.Room, towns.InitialReturn)
            { Level = 95, GrowType = 1, Inventory = new([new(1, Bag(3), source), new(2, Bag(4), source), sentinel], Gold: 12345) };
            string before = JsonSerializer.Serialize(role);
            var opened = service.Open(role, new(3, 1), out var receipt, now);
            That(opened.Accepted && opened.Plan is not null && receipt is not null && JsonSerializer.Serialize(role) == before,
                "普通父盒应真实规划两个自选子盒，不因父盒无Selection拒绝 " + entry.Source);
            var after = opened.Plan!.After;
            That(after.Find(Bag(3)) is null && after.Find(Bag(4)) == role.Inventory.Find(Bag(4)) &&
                after.Find(Bag(65)) == sentinel && after.Gold == 12345 &&
                receipt!.Rewards.Select(row => row.ItemId).SequenceEqual(entry.Children),
                "只消费指定父盒一次，两子盒与哨兵金币及另一个源保持 " + entry.Source);
            foreach (int id in entry.Children)
            {
                var child = after.Items.Single(item => item.Core.ItemId == id);
                That(child.Core == InventoryRewardGrantService.Create(resources.Items.Get(id), utcNow: now) &&
                    child.Core is { Kind: ItemKind.Consumable, DisjointForbidden: false, DestructionSoulForbidden: false,
                        EpicExchangeOrigin: null, Extension119: 0 } &&
                    resources.Boosters.Get(id) is { Available: true, Selection: not null, SetsDisjointInstanceType: true },
                    "四子盒完整核心和自身声明独立保留，既不补装备标志也不代开 " + id);
            }
            That(after.ConsumableStates.SequenceEqual(role.Inventory.ConsumableStates),
                "父盒发奖不改写无关已有消耗品状态；未声明maintenance的普通冷却消费者另行核查 " + entry.Source);
            var range = ItemSlotBoundService.Range(ItemKind.Consumable, role.Inventory.Expansion);
            var full = role with { Inventory = role.Inventory with { Items = [role.Inventory.Items[0],
                .. Enumerable.Range(range.Start, range.End - range.Start).Select(slot =>
                    new InventoryItem(100 + slot, Bag((short)slot), sentinel.Core))] } };
            string fullBefore = JsonSerializer.Serialize(full);
            var noRoom = service.Open(full, new(3, 1), out var noRoomReceipt, now);
            That(!noRoom.Accepted && noRoom.Error == 4 && noRoomReceipt is null && noRoom.Plan is null &&
                JsonSerializer.Serialize(full) == fullBefore,
                "仅一空位不能发两个不同子盒，源与冷却及全部资产不变 " + entry.Source);
        }
        Console.WriteLine($"PASS {Count - checks} ordinary parent selection-child checks");
    }

    static void RunBoundaries(ItemResources resources, BoosterDefinition source, Character role, DateTimeOffset now)
    {
        var archive = (PvfLib.PvfArchive)typeof(ItemCatalog).GetField("pvf", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(resources.Items)!;
        var boosters = new BoosterCatalog(archive, new Dictionary<int, string>());
        var definitions = (ConcurrentDictionary<int, BoosterDefinition>)typeof(BoosterCatalog)
            .GetField("definitions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(boosters)!;
        var service = new InventoryBoosterService(resources.Items, boosters, resources.GoldLimits, resources.EpicPieces,
            contracts: resources.Contracts);
        const int childId = 490011826;
        var child = resources.Boosters.Get(childId);
        BoosterDefinition Parent(int id) => source with { Selection = new(1, [new(0, 0, [new(id, 1)])]) };
        void Reject(int id, string reason, bool ordinary = false)
        {
            string before = JsonSerializer.Serialize(role);
            var result = service.Open(role, ordinary ? new(3, 1) : Select(id), out var receipt, now);
            That(!result.Accepted && result.Plan is null && receipt is null && JsonSerializer.Serialize(role) == before,
                "806 " + reason + "整盒拒绝且无计划、回执或输入资产变更");
        }

        definitions[SourceId] = Parent(1106);
        Reject(1106, "真实普通消耗品不是可用自选子盒，");
        definitions[SourceId] = Parent(childId);
        definitions[childId] = child with { PendingRule = "测试未实现子盒规则" };
        Reject(childId, "有待实现规则的自选子盒，");
        definitions[childId] = BoosterCatalog.Parse("[stackable type]\n`[booster]` 0\n[booster info]\n" +
            "[etc]\n1 1106 1 1\n[/etc]\n[/booster info]");
        That(definitions[childId] is { Available: true, Selection: null }, "806 非自选负例确实为可用随机子盒");
        Reject(childId, "可用随机子盒不属于Selection，");
        definitions[childId] = child;
        foreach (bool disjoint in new[] { false, true })
        {
            definitions[SourceId] = Parent(childId) with { SetsDisjointInstanceType = disjoint, SetsEpicExchangeInstanceType = true };
            Reject(childId, disjoint ? "分解与史诗双声明仍只准装备，" : "仅史诗声明仍只准装备，");
        }

        int contract = resources.Contracts.ByItem.Keys.First(id => resources.Contracts.TryAcquisitionContract(id, out _) &&
            resources.Items.Get(id).Kind == ItemKind.Consumable);
        foreach (int id in new[] { 1, resources.EpicPieces.ByPiece.Keys.First(), resources.Boosters.DirectActivationItems.First(), contract }.Distinct())
        {
            // 即使目录存在选择定义，钱包、碎片、即时激活与账号契约仍保留各自owner。
            definitions[id] = child;
            definitions[SourceId] = Parent(id);
            Reject(id, "钱包/碎片/直接激活/账号契约不能借子盒选择定义转普通实体 " + id + "，");
        }

        var ordinaryParent = source with { Selection = null, Pools = [new(1, [new(childId, 1000, 1)])] };
        definitions[SourceId] = ordinaryParent;
        definitions[childId] = child;
        That(service.Open(role, new(3, 1), out var plainReceipt, now).Accepted && plainReceipt is not null,
            "普通固定父盒可以发真实可用自选子盒");
        foreach (var invalidParent in new[]
        {
            ordinaryParent with { Lottery = true },
            ordinaryParent with { Pools = [new(2, [new(childId, 1000, 1)])] },
            ordinaryParent with { Pools = [new(1, [new(childId, 1000, 1), new(490011783, 1000, 1)])] },
            ordinaryParent with { SetsEpicExchangeInstanceType = true }
        })
        {
            definitions[SourceId] = invalidParent;
            Reject(childId, "随机/多抽/混合奖池/史诗来源父盒不借固定子盒路径放行，", ordinary: true);
        }
        foreach (int id in new[] { 1106, 1, resources.EpicPieces.ByPiece.Keys.First(),
            resources.Boosters.DirectActivationItems.First(), contract }.Distinct())
        {
            definitions[SourceId] = ordinaryParent with { Pools = [new(1, [new(id, 1000, 1)])] };
            Reject(id, "普通父盒的未知子盒或特殊owner仍拒绝 " + id + "，", ordinary: true);
        }
        definitions[SourceId] = ordinaryParent;
        definitions[childId] = child with { PendingRule = "测试未实现子盒规则" };
        Reject(childId, "普通父盒不能发带待实现规则的子盒，", ordinary: true);
    }

    static BoosterOpenRequest Select(int id) => new(3, 1, new(0, 0, [id]));
    static InventorySlot Bag(short slot) => new(InventoryListType.Main, slot);
}
