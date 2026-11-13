using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class InventoryEpicRewardRateTests
{
    public static void RunResources(TownCatalog towns, ItemResources items)
    {
        int before = Count;
        var control = new DungeonDropControlSettings();
        var now = DateTimeOffset.FromUnixTimeSeconds(1792756800);
        const int box = 10160995;
        var source = new InventoryItem(1, new(InventoryListType.Main, 117),
            InventoryRewardGrantService.Create(items.Items.Get(box), 2, now));
        var character = new Character(1, 0, "EpicLottery", 0, towns.Room, towns.InitialReturn)
            { Level = 95, Inventory = new([source], Gold: 100_000_000) };
        var service = new InventoryBoosterService(items.Items, items.Boosters, items.GoldLimits, items.EpicPieces,
            new FirstRandom(), dropControl: control);
        foreach (decimal factor in new[] { 0m, 100m })
        {
            control.Replace([new(DungeonDropScope.All, [], new(Equipment: factor))], true);
            var result = service.OpenLottery(character, new(0, 117), out var receipt, now);
            That(result.Accepted && receipt is not null &&
                DungeonEpicDropAdjustment.IsEpic(items.Items.Get(receipt.ItemId)) == (factor == 100),
                "真实袖珍罐一次开箱读取GM新快照，零倍排除史诗、百倍封顶：" + result.Error + "/" + result.RejectReason + "/" + receipt?.ItemId);
            That(result.Plan!.After.Items.Single(row => row.Core.ItemId == box).Core.Count == 1 &&
                result.Plan.After.Items.Count(row => row.Core.Kind == GameWorld.Items.ItemKind.Equipment) == 1 &&
                character.Inventory.Items is [{ Core.Count: 2 }] && character.Inventory.Items[0] == source,
                "倍率只改选中概率，扣一个源、产生一件装备且规划不改原资产");
        }
        var odds = new EpicRewardOdds(items.Items, items.Boosters);
        var rows = items.Boosters.Get(box).Pools.Single().Rewards;
        long originalEpic = rows.Where(row => DungeonEpicDropAdjustment.IsEpic(items.Items.Get(row.ItemId))).Sum(row => row.Weight);
        var doubled = odds.Apply(rows, 2);
        That(doubled.Sum(row => row.Weight) == rows.Sum(row => row.Weight) &&
            doubled.Where(row => DungeonEpicDropAdjustment.IsEpic(items.Items.Get(row.ItemId))).Sum(row => row.Weight) == 2 * originalEpic,
            "真实混合奖池精确两倍史诗区间，不增加抽数或改变总权重");
        var guaranteed = items.Boosters.Get(10306757).Pools.Single(pool =>
            pool.Rewards.All(row => DungeonEpicDropAdjustment.IsEpic(items.Items.Get(row.ItemId))));
        That(odds.Apply(guaranteed.Rewards, 0).SequenceEqual(guaranteed.Rewards) &&
            odds.Apply(guaranteed.Rewards, 100).SequenceEqual(guaranteed.Rewards),
            "本来保证史诗的奖励池封顶100%，保底及数量不变");
        Console.WriteLine($"PASS {Count - before} 真实随机开箱史诗倍率、保底与资产断言");
    }
    sealed class FirstRandom : Random { public override long NextInt64(long maximum) => 0; }
}
