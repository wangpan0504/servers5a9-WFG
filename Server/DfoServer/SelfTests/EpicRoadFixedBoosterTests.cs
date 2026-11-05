using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

// 三个固定材料盒的真实资源/原仓储合同；不代表原生入口或客户端实机。
internal static class EpicRoadFixedBoosterTests
{
    public static void RunResources(TownCatalog towns, ItemResources resources)
    {
        int checks = Count;
        var now = new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.FromHours(8));
        var inventory = new InventoryService(resources.Items, resources.GoldLimits, resources.EpicPieces);
        var booster = new InventoryBoosterService(resources.Items, resources.Boosters, resources.GoldLimits,
            resources.EpicPieces, contracts: resources.Contracts);
        foreach (var (boxId, rewardId, amount) in new[]
        {
            (490013725, 10308559, 8), (490013726, 10306750, 16), (490013729, 490013728, 3)
        })
        {
            var definition = resources.Boosters.Get(boxId);
            var target = resources.Items.Get(rewardId);
            That(definition.Available && definition.Selection is null && definition.PackageRewards.IsDefaultOrEmpty &&
                definition.Pools is [{ Draws: 1, Rewards: [{ ItemId: var id, Weight: 1000, Count: var count }] }] &&
                id == rewardId && count == amount && target.Kind == ItemKind.Material,
                "原固定盒保留唯一真实材料奖励、权重及完整数量：" + boxId);
            string directory = Path.Combine(Path.GetTempPath(), "s5a9-epic-fixed-box-" + Guid.NewGuid().ToString("N"));
            bool passed = false;
            try
            {
                string path = Path.Combine(directory, "game.db");
                var repository = new CharacterRepository(path);
                using var owner = new AccountOwners().Acquire("epic-fixed", () => { });
                var current = repository.Create(owner, new("EpicFixed", towns.Room, towns.InitialReturn) { Level = 95 });
                current = InventoryService.Commit(repository, owner, current,
                    inventory.Grant(current, [new(boxId, 2)]));
                var before = current;
                var source = current.Inventory.Items.First(row => row.Core.ItemId == boxId);
                var result = booster.Open(current, new(source.Position.Index, 1), out var receipt, now);
                That(result.Accepted && result.Plan is not null && receipt is not null &&
                    receipt.Rewards.SequenceEqual(new[] { new BoosterGrantedItem(rewardId, amount) }) &&
                    Quantity(result.Plan.After, boxId) == Quantity(before.Inventory, boxId) - 1 &&
                    Quantity(result.Plan.After, rewardId) == Quantity(before.Inventory, rewardId) + amount &&
                    result.Plan.After.Gold == before.Inventory.Gold && result.Plan.OverflowItems.IsEmpty,
                    "真实固定盒只消费一源，完整到账，不扣额外金币、不邮寄缩水奖励：" + boxId);
                current = InventoryService.Commit(repository, owner, before, result);
                That(QuestCompletionRepositoryTests.Same(current,
                    new CharacterRepository(path).LoadCharacter(owner, current.Slot)) &&
                    Quantity(current.Inventory, boxId) == 1 && Quantity(current.Inventory, rewardId) == amount &&
                    (target.AccountMaterial is not null || current.Inventory.Items
                        .Where(row => row.Core.ItemId == rewardId).All(row => row.Uid > 0)),
                    "原资产事务扣源、真实材料及UID经独立Repository完整恢复：" + boxId);

                if (target.AccountMaterial is { } actualLimit)
                {
                    // 仅真实模板明确为账号钱包时测试其实际持有上限，不把account attach强当钱包。
                    int extra = checked(actualLimit.HoldingLimit - (int)Quantity(current.Inventory, rewardId));
                    if (extra > 0)
                        current = InventoryService.Commit(repository, owner, current,
                            inventory.Grant(current, [new(rewardId, extra)]));
                    That(current.Inventory.AccountWallet.Material(rewardId).Balance == actualLimit.HoldingLimit,
                        "真实账号材料已通过正式Grant达到持有上限");
                }
                else
                {
                    // 三盒实际目标为材料时，按其真实分类范围填满，不能填消耗品页假造容量拒绝。
                    var range = ItemSlotBoundService.Range(target.Kind, current.Inventory);
                    var full = Enumerable.Range(range.Start, range.End - range.Start + 1).Select(slot =>
                        new InventoryItem(0, new(InventoryListType.Main, checked((short)slot)),
                            InventoryRewardGrantService.Create(target, target.StackLimit, now)));
                    var filled = current.Inventory with { Items = current.Inventory.Items
                        .Where(row => !(row.Position.List == InventoryListType.Main && row.Core.Kind == target.Kind))
                        .Concat(full).ToImmutableArray() };
                    current = repository.CommitInventory(owner, current,
                        new(current.Inventory, filled, "epic_fixed_box_capacity_fixture"));
                }
                source = current.Inventory.Items.Single(row => row.Core.ItemId == boxId);
                string state = Snapshot();
                result = booster.Open(current, new(source.Position.Index, 1), out receipt, now);
                That(!result.Accepted && result.Error == 4 && result.Plan is null && receipt is null &&
                    Snapshot() == state && Quantity(current.Inventory, boxId) == 1,
                    "真实材料页满或真实持有上限时整盒拒绝，不扣源、不截断奖励：" + boxId);
                passed = true;

                // 数量按真实模板决定容器，绝不硬编码某ID为账号钱包；用long避免满多堆统计溢出。
                long Quantity(InventorySnapshot state, int itemId) => resources.Items.Get(itemId).AccountMaterial is not null
                    ? state.AccountWallet.Material(itemId).Balance
                    : state.Items.Where(row => row.Core.ItemId == itemId).Sum(row => (long)row.Core.Count);
                string Snapshot() => JsonSerializer.Serialize(new
                {
                    Characters = repository.List(owner), Wallet = repository.LoadAccountWallet(owner)
                });
            }
            finally
            {
                if (passed) TestFiles.DeleteDirectory(directory, true);
                else Console.WriteLine("FAIL fixture: " + directory);
            }
        }
        Console.WriteLine($"PASS {Count - checks} 星河三固定盒完整开盒、容量原子拒绝与持久恢复断言（非实机）");
    }
}
