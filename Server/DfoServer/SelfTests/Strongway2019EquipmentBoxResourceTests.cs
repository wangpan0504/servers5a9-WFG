using System.Text.Json;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class Strongway2019EquipmentBoxResourceTests
{
    public static void RunResources(TownCatalog towns, ItemResources resources)
    {
        int before = Count;
        int[][] expected =
        [
            [401000067, 401010062, 401020066, 401030067, 401040060],
            [401000067, 401010062, 401020066, 401030067, 401040060],
            [402000062, 402010062, 402020062, 402030060, 402040062],
            [402000062, 402010062, 402020062, 402030060, 402040062],
            [404000059, 404010061, 404020059, 404030060, 404040061],
            [404000059, 404010061, 404020059, 404030060, 404040061],
            [406000055, 406010055, 406020064, 406030064, 406040055],
            [406000055, 406010055, 406020064, 406030064, 406040055],
            [408010058, 408030058, 408040058, 408000059, 408020057],
            [408010058, 408030058, 408040058, 408000059, 408020057],
            [409000060, 409010060, 409030035, 409040078],
            [401000067, 401010062, 401020066, 401030067],
            [414000067, 414010064, 414020048, 414030038],
            [416010029, 416020029, 416000030, 416030029],
            [401000067, 401010062, 401020066, 401030067, 401040060],
            [406000055, 406010055, 406020064, 406030064, 406040055]
        ];
        var selectable = resources.Boosters.Get(490013021);
        var selectionDefinition = selectable.Selection!;
        That(selectable is { Available: true, Lottery: false, Selection.SelectionCount: 1 } &&
            selectable.Pools.IsEmpty && selectionDefinition.Categories.Length == 16 &&
            selectionDefinition.Categories.Sum(category => category.Rewards.Length) == 76 &&
            selectionDefinition.Categories.SelectMany(category => category.Rewards).Select(row => row.ItemId).Distinct().Count() == 37,
            "021保留真实16分类、76条选择路径和37唯一装备，不能按唯一装备合并分类");
        for (byte category = 0; category < 16; category++)
        {
            var actual = selectionDefinition.Categories[category];
            That(actual.Category == category && actual.Subcategory == 0 && actual.PendingRule is null &&
                actual.Rewards.Select(row => row.ItemId).SequenceEqual(expected[category]) &&
                actual.Rewards.All(row => row.Count == 1 && row.Upgrade == 0 && row.Refinement == 0 &&
                    row.UpgradeChances.IsEmpty && resources.Items.Get(row.ItemId).Kind == ItemKind.Equipment),
                "021完整原序分类与装备数量/强化锻造无额外改写：" + category);
        }
        (int Box, int Reward, ItemKind Kind)[] fixedBoxes =
        [
            (490013024, 400330185, ItemKind.Equipment),
            (490013315, 401590023, ItemKind.Avatar),
            (490013317, 401590024, ItemKind.Avatar)
        ];
        foreach (var row in fixedBoxes)
        {
            var definition = resources.Boosters.Get(row.Box);
            That(definition is { Available: true, Lottery: false, Selection: null } && definition.PackageRewards.IsDefaultOrEmpty &&
                definition.Pools is [{ Draws: 1 }] && definition.Pools[0].Rewards.SequenceEqual(
                    new BoosterReward[] { new(row.Reward, 1000, 1) }) && resources.Items.Get(row.Reward).Kind == row.Kind,
                "真实固定礼盒保留唯一原奖、数量和真实实体类型：" + row.Box);
        }
        foreach (int id in new[] { 490013021, 490013024, 490013315, 490013317 })
        {
            var template = resources.Items.Get(id);
            int expectedStackLimit = id == 490013317 ? int.MaxValue : 1;
            That(template is { Kind: ItemKind.Consumable, MinimumLevel: 1, Jobs: ushort.MaxValue } &&
                template.StackLimit == expectedStackLimit,
                $"真实奖励盒保留all/Lv1和各自堆叠声明：{id}, actualStackLimit={template.StackLimit}, expectedStackLimit={expectedStackLimit}");
            foreach (byte job in Enumerable.Range(0, 16).Select(value => (byte)value))
                That(template.AllowsUse(95, job), "奖励盒资源允许全部职业合法级别：" + id + ":" + job);
        }

        // 单一临时数据库，16个既有Session分别连续开一分类，避免每条选择路径冷建schema。
        using var f = new Strongway2019MatchingTests.Fixture(towns);
        var actors = Enumerable.Range(0, 4).SelectMany(_ => f.Team()).ToArray();
        var booster = new InventoryBoosterService(resources.Items, resources.Boosters, resources.GoldLimits,
            resources.EpicPieces, contracts: resources.Contracts, avatars: resources.AvatarPackages, clock: f.Clock);
        int choices = 0;
        for (byte category = 0; category < 16; category++)
        {
            var actor = actors[category];
            foreach (int rewardId in expected[category])
            {
                OpenAndRestore(actor, 490013021, rewardId, new(category, 0, [rewardId]));
                choices++;
            }
        }
        That(choices == 76, "021实际正常Grant/Open/Session提交与恢复覆盖全部76条分类选择路径");
        foreach (var row in fixedBoxes) OpenAndRestore(actors[0], row.Box, row.Reward, null);
        Console.WriteLine($"PASS {Count - before} 首期021完整76选择及024/315/317精确奖励、完整实例和恢复断言");

        void OpenAndRestore(GameSession actor, int boxId, int rewardId, BoosterSelectionRequest? selection)
        {
            f.Clock.Advance(TimeSpan.FromMilliseconds(100));
            var grant = actor.PlanAndCommitInventory(character => InventoryRewardGrantService.TryGrantBatch(character,
                [new(boxId, 1)], resources.Items, resources.GoldLimits, resources.EpicPieces, utcNow: f.Clock.GetUtcNow()));
            That(grant.Accepted, $"正常单罐Grant：box={boxId}, reward={rewardId}, error={grant.Error}");
            var original = actor.Selected!;
            var source = original.Inventory.Items.Single(item => item.Core.ItemId == boxId);
            That(source.Core.Count == 1 && source.Uid > 0 && source.Position.List == InventoryListType.Main,
                "源盒由正常获取分配真实UID并进入消耗品页：" + boxId);
            var oldUids = original.Inventory.Items.Select(item => item.Uid).ToHashSet();
            var frozen = booster.Open(original, new(source.Position.Index, 1, selection), out var receipt, f.Clock.GetUtcNow());
            That(frozen.Accepted && frozen.Plan is not null && receipt is not null &&
                receipt.Rewards.SequenceEqual(new BoosterGrantedItem[] { new(rewardId, 1) }) &&
                frozen.Plan.OverflowItems.IsEmpty,
                $"正式Open仅规划精确一件真实奖：box={boxId}, reward={rewardId}, category={selection?.Category}, error={frozen.Error}");
            var plan = frozen.Plan!;
            var plannedReward = plan.After.Items.Single(item => item.Core.ItemId == rewardId && !oldUids.Contains(item.Uid));
            That(actor.PlanAndCommitBooster(_ => frozen, booster.UsePlaces(boxId), boxId).Accepted,
                "正式Session提交真实源盒开盒计划：" + boxId + ":" + rewardId);
            var committed = actor.Selected!;
            var reward = committed.Inventory.Items.Single(item => item.Core.ItemId == rewardId && !oldUids.Contains(item.Uid));
            var normalized = plannedReward with { Uid = reward.Uid };
            if (reward.Core.Kind == ItemKind.Avatar)
                normalized = normalized with { Core = normalized.Core with { Value = checked((int)reward.Uid) } };
            That(committed.Revision == original.Revision + 1 && committed.Inventory.Find(source.Position) is null &&
                committed.Inventory.Items.Length == original.Inventory.Items.Length && reward.Uid > 0 &&
                !oldUids.Contains(reward.Uid) && reward == normalized &&
                reward.Position.List == ItemSlotBoundService.List(reward.Core.Kind) &&
                ItemSlotBoundService.Allows(committed.Inventory, reward.Position, reward.Core.Kind) &&
                reward.Core.ItemId == rewardId && reward.Core.Count == 1 &&
                (reward.Core.Kind == ItemKind.Avatar ? reward.Avatar is not null : reward.Avatar is null) && reward.Creature is null,
                "只扣指定源、创建完整Core/详情与独立新UID，进入真实类型容器：" + boxId + ":" + rewardId);
            That(original.Inventory.Items.Where(item => item.Uid != source.Uid).All(item =>
                committed.Inventory.Find(item.Position) == item) && committed.Inventory.Gold == original.Inventory.Gold,
                "既有奖励、其他实例和金币不因后续开盒变化：" + boxId + ":" + rewardId);
            var owner = actor.CaptureSocialActor()!.Owner;
            That(JsonSerializer.Serialize(new CharacterRepository(f.DatabasePath).LoadCharacter(owner, original.Slot)) ==
                JsonSerializer.Serialize(committed), "新Repository完整恢复真实奖励/Core/详情/UID及其余库存：" + boxId + ":" + rewardId);
        }
    }
}
