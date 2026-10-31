using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing.Inventory;

internal static class BoosterCharacterPoolTests
{
    public static void RunCore()
    {
        int checks = Count;
        var definition = Parse("`[at priest]` 1 99 1 1106 0.25 2 1112 0.75 1");
        That(definition.Available && definition.Pools is [{ JobTag: "[at priest]", MinimumLevel: 1,
            MaximumLevel: 99, GrowType: -1, Draws: 1 }] && definition.Pools[0].Rewards.SequenceEqual(
                new BoosterReward[] { new(1106, 25, 2), new(1112, 75, 1) }),
            "charactor保留完整职业标签、等级范围、抽数与小数权重，不错读成转职分支");
        var pool = definition.Pools[0];
        foreach (byte grow in new byte[] { 0, 1, 4, 0x11, 0x21 })
            That(pool.Matches(14, grow, 1) && pool.Matches(14, grow, 99), "职业奖池不限制转职和觉醒，等级两端包含");
        That(!pool.Matches(4, 1, 55) && !pool.Matches(14, 1, 0) && !pool.Matches(14, 1, 100),
            "女圣职者不误配男圣职者，等级区间外不抽取");
        var growPool = new BoosterPool(1, [new(1106, 1, 1)]) { JobTag = "[at priest]", GrowType = 0 };
        That(growPool.Matches(14, 0, 1) && !growPool.Matches(14, 1, 1),
            "grow type分支0仍仅限未转职，不能误作职业奖池的通配标记");
        foreach (string bad in new[] { "`[unknown]` 1 99 1 1106 100 1", "`[at priest]` 99 1 1 1106 100 1",
            "`[at priest]` 1 99 0 1106 100 1", "`[at priest]` 1 99 1 1106 100",
            "`[at priest]` 1 99 1 1106 -1 1", "`[at priest]` 1 99 1 1106 100 0" })
            That(!Parse(bad).Available, "非法职业奖池不截断、不忽略条件或制造奖励");
        That(!Parse("`[at priest]` 1 99 1").Available &&
            !Parse("`[at priest]` 1 99 1 1106 0 1").Available, "空占位及零总权重不制造一件奖励");
        Console.WriteLine($"PASS {Count - checks} 职业礼包解析与筛池边界断言");
    }

    public static void RunResources(TownCatalog towns, ItemResources resources)
    {
        int checks = Count;
        var definition = resources.Boosters.Get(10002631);
        That(definition.Available && definition.Pools.Length == 80 && definition.Pools.All(pool => pool.GrowType == -1),
            "豪华婚礼化妆箱完整保留16职业各5个独立奖池");
        var now = DateTimeOffset.UtcNow;
        var service = new InventoryBoosterService(resources.Items, resources.Boosters, resources.GoldLimits,
            resources.EpicPieces, contracts: resources.Contracts, avatars: resources.AvatarPackages);
        var source = new InventoryItem(1, new(InventoryListType.Main, 68),
            InventoryRewardGrantService.Create(resources.Items.Get(10002631), utcNow: now));
        for (byte job = 0; job < 16; job++)
        {
            var expectedPools = definition.Pools.Where(pool => pool.Matches(job, 0, 55)).ToArray();
            That(expectedPools.Length == 5 && expectedPools.All(pool => pool.JobTag == ItemCatalog.JobTag(job) &&
                pool.Rewards is [{ Weight: 100000 }]), "仅选择本职业五个确定奖池 " + job);
            foreach (byte grow in new byte[] { 0, 1, 0x21 })
            {
                var role = new Character(1, 0, "WeddingBox", 0, towns.Room, towns.InitialReturn)
                    { Job = job, Level = 55, GrowType = grow, Inventory = new([source]) };
                var result = service.Open(role, new(68, 1), out var receipt, now);
                var expected = expectedPools.Select(pool => new BoosterGrantedItem(pool.Rewards[0].ItemId, pool.Rewards[0].Count))
                    .OrderBy(row => row.ItemId).ToArray();
                That(result.Accepted && receipt!.Rewards.OrderBy(row => row.ItemId).SequenceEqual(expected) &&
                    result.Plan!.UsedItems is [{ ItemId: 10002631, Count: 1 }] && role.Inventory.Find(source.Position) == source,
                    "未转职、转职和觉醒角色均只领取本职业完整五项，不修改规划前快照 " + job + "/" + grow);
            }
        }
        var priest = new Character(1, 0, "PriestBox", 0, towns.Room, towns.InitialReturn)
            { Job = 14, Level = 55, GrowType = 1, Inventory = new([source]) };
        var opened = service.Open(priest, new(68, 1), out var rewards, now);
        That(opened.Accepted && rewards!.Rewards.OrderBy(row => row.ItemId).SequenceEqual(new BoosterGrantedItem[]
            { new(10002611, 1), new(10002612, 1), new(10002901, 100), new(415600000, 1), new(490700962, 1) }),
            "现场女圣职者取得婚纱包、神圣烛台、100心意通和两种婚戒升级券");
        That(opened.Plan!.After.Items.Single(row => row.Core.ItemId == 415600000) is { Avatar: not null },
            "武器装扮创建独立详情，不只发放普通核心");
        var avatar = resources.Items.Get(415600000);
        var range = ItemSlotBoundService.Range(ItemKind.Avatar, priest.Inventory);
        var full = priest.Inventory with { Items = priest.Inventory.Items.AddRange(Enumerable.Range(range.Start, range.End - range.Start + 1).Select(slot =>
        {
            var item = InventoryRewardGrantService.CreateItem(avatar, new(InventoryListType.Avatar, (short)slot),
                InventoryRewardGrantService.Create(avatar, utcNow: now));
            return item with { Uid = 100 + slot, Core = item.Core with { Value = 100 + slot } };
        })) };
        That(!service.Open(priest with { Inventory = full }, new(68, 1), out _, now).Accepted &&
            full.Find(source.Position) == source, "装扮页满格时整笔拒绝，普通道具也不部分入包，源盒保留");
        Console.WriteLine($"PASS {Count - checks} 职业礼包真实奖池及整批发放断言");
    }

    static BoosterDefinition Parse(string data) => BoosterCatalog.Parse(
        "[stackable type]\n`[booster]` 0\n[booster info]\n[charactor]\n" + data + "\n[/charactor]\n[/booster info]");
}
