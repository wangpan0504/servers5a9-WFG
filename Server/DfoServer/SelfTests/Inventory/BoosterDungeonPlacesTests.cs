using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing.Inventory;

internal static class BoosterDungeonPlacesTests
{
    public static void Run(TownCatalog towns, ItemResources resources)
    {
        int checks = Count;
        foreach (int id in new[] { 10150927, 10151070, 10151071, 10155512, 10305492, 10305493, 10305494, 10305517, 10305518 })
            That(resources.Boosters.UsePlaces(id) is { Village: true, SeriaRoom: true, Dungeon: true, PendingRule: null },
                "九份真实礼包资源保留城镇、房间和副本地点并集 " + id);
        var now = DateTimeOffset.UtcNow;
        var service = new InventoryBoosterService(resources.Items, resources.Boosters, resources.GoldLimits, resources.EpicPieces);
        foreach (var (source, rewards) in new[]
        {
            (10150927, new[] { new BoosterReward(10150924, 1000, 4) }),
            (10305518, new[] { new BoosterReward(100390019, 1000, 1), new BoosterReward(100331725, 1000, 1) })
        })
        {
            var definition = resources.Boosters.Get(source);
            That(definition.Available && definition.Pools.SelectMany(pool => pool.Rewards).SequenceEqual(rewards) &&
                definition.Pools.All(pool => pool.Draws == 1) && definition.SetsDisjointInstanceType == (source == 10305518),
                "真实普通礼包完整独立奖池及限制位保持 " + source);
            var role = new Character(1, 0, "DungeonBox", 0, towns.Room, towns.InitialReturn)
            { Inventory = new([new(1, Bag(65), InventoryRewardGrantService.Create(resources.Items.Get(source), 2, now))]) };
            string before = JsonSerializer.Serialize(role);
            var result = service.Open(role, new(65, 1), out var receipt, now);
            That(result.Accepted && result.Plan is not null && receipt is not null && JsonSerializer.Serialize(role) == before &&
                receipt.Rewards.SequenceEqual(rewards.Select(row => new BoosterGrantedItem(row.ItemId, row.Count))),
                "代表礼包完整规划且不提前修改角色 " + source);
            var after = result.Plan!.After;
            That(after.Find(Bag(65)) is { Uid: 1, Core.Count: 1 }, "每次只扣一个源且保留原实例 " + source);
            foreach (var reward in rewards)
            {
                var granted = after.Items.Where(item => item.Core.ItemId == reward.ItemId).ToArray();
                var expected = InventoryRewardGrantService.Create(resources.Items.Get(reward.ItemId), reward.Count, now) with
                    { DisjointForbidden = source == 10305518 };
                That(granted.Length == 1 && granted[0].Core == expected,
                    "代表真实奖励数量及完整核心按模板创建 " + reward.ItemId);
            }
            var range = ItemSlotBoundService.Range(resources.Items.Get(rewards[0].ItemId).Kind, role.Inventory);
            var full = role with { Inventory = role.Inventory with
            {
                Items = role.Inventory.Items.AddRange(Enumerable.Range(range.Start, range.End - range.Start + 1)
                    .Where(slot => slot != 65).Select(slot => new InventoryItem(1000 + slot, Bag((short)slot),
                        InventoryRewardGrantService.Create(resources.Items.Get(rewards[0].ItemId),
                            resources.Items.Get(rewards[0].ItemId).Kind == ItemKind.Equipment ? 1 : int.MaxValue, now))))
            } };
            before = JsonSerializer.Serialize(full);
            var rejected = service.Open(full, new(65, 1), out receipt, now);
            That(!rejected.Accepted && rejected.Plan is null && receipt is null && JsonSerializer.Serialize(full) == before,
                "容量不足拒绝整盒，保留源、完整奖励和资产 " + source);
        }
        Console.WriteLine($"PASS {Count - checks} booster dungeon-place resource checks");
    }

    static InventorySlot Bag(short slot) => new(InventoryListType.Main, slot);
}
