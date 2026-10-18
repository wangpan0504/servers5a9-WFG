using static DfoServer.Testing.TestCheck;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Settings;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;

namespace DfoServer.Testing.Inventory;

internal static class BoosterGrowTypeTests
{
    public static void Run(TownCatalog towns, ItemResources itemResources)
    {
        int checks = Count;
        var catalog = itemResources.Items;
        var now = DateTimeOffset.UtcNow;
        var parsed = Parse("`[swordman]` 50 99 1 1 1106 0.25 2 1112 0.75 1");
        That(parsed.Available && parsed.Pools.Length == 1 && parsed.Pools[0] is { Draws: 1, JobTag: "[swordman]", MinimumLevel: 50,
            MaximumLevel: 99, GrowType: 1 } &&
            parsed.Pools[0].Rewards.SequenceEqual(new BoosterReward[] { new(1106, 25, 2), new(1112, 75, 1) }),
            "转职奖池保留职业、等级、分支和小数权重");
        That(parsed.Pools[0].Matches(0, 1, 50) && parsed.Pools[0].Matches(0, 0x21, 99) &&
            !parsed.Pools[0].Matches(0, 0, 50) && !parsed.Pools[0].Matches(0, 1, 49) && !parsed.Pools[0].Matches(15, 1, 50),
            "转职筛池用 JobTag、GrowType低4位和等级，不把觉醒高位当成另一分支");
        That(Parse("`[at gunner]` 1 99 0 1 1106 100 1").Pools[0].JobTag == "[at gunner]", "空格职业名保持完整标签");
        foreach (string bad in new[] { "`[unknown]` 1 99 0 1 1106 100 1", "`[swordman]` 1 99 0 0 1106 100 1",
            "`[swordman]` 1 99 16 1 1106 100 1", "`[swordman]` 99 1 0 1 1106 100 1", "`[swordman]` 1 99 0 1 1106 100",
            "1 99 0 1 1106 100 1" })
            That(!Parse(bad).Available, "非法转职奖池不能截成无过滤普通盒: " + bad);
        That(Parse("`[swordman]` 1 99 0 1").Pools.IsDefaultOrEmpty, "空转职占位不制造奖励");
        That(BoosterCatalog.Parse("[stackable type]\n`[booster]` 0\n[booster info]\n[hide booster info]\n[/hide booster info]\n[grow type]\n`[swordman]` 1 99 0 1 1106 100 1\n[/grow type]\n[/booster info]").Available,
            "hide booster info 只影响客户端预览，不把转职池标成未实现");

        var soul = itemResources.Boosters.Get(490704570);
        var copy = itemResources.Boosters.Get(490704415);
        That(soul.Available && copy.Available && soul.Pools.Length == 70 && copy.Pools.Length == 70 &&
            soul.Pools.All(pool => pool.JobTag is not null && pool.Draws == 1) &&
            !soul.Pools.Any(pool => pool.JobTag == "[gun blader]"),
            "灵魂武器礼盒按70段转职池解析，枪剑士没有可抽池");
        That(soul.Pools.Single(pool => pool.JobTag == "[swordman]" && pool.GrowType == 1).Rewards.Length == 137 &&
            soul.Pools.Single(pool => pool.JobTag == "[swordman]" && pool.GrowType == 0).Rewards.Length == 116,
            "鬼剑士不同转职分支保留独立奖池长度");
        var plat = itemResources.Boosters.Get(490705076);
        That(plat.Available && plat.Pools.Length == 75 && plat.Pools.Any(pool => pool.JobTag == "[gun blader]"),
            "白金徽章礼盒共用同一转职行宽");

        var random = new BoosterTestsRandom();
        var service = new InventoryBoosterService(catalog, itemResources.Boosters, itemResources.GoldLimits, itemResources.EpicPieces, random);
        var role = new Character(1, 0, "SoulBox", 0, towns.Room, towns.InitialReturn)
        {
            Job = 0, GrowType = 1, Level = 50, Inventory = new([new(1, Bag(65), Core(490704570))])
        };
        var opened = service.Open(role, new(65, 1), out var receipt, now);
        That(opened.Accepted && receipt is { ItemId: 490704570, Slot: 65, Rewards: [{ ItemId: 101000814, Count: 1 }] } &&
            opened.Plan!.After.Find(Bag(65)) is null &&
            catalog.Get(101000814).Kind == ItemKind.Equipment &&
            opened.Plan.After.Items.Single(item => item.Core.ItemId == 101000814).Core ==
                InventoryRewardGrantService.Create(catalog.Get(101000814), utcNow: now),
            "匹配转职池扣除礼盒并发放一件完整灵魂武器");
        That(service.CountMatchingPools(role with { Job = 15, GrowType = 0 }, 490704570) == (70, 0),
            "枪剑士真实转职池仍为零匹配，与源职业资格独立核对");
        That(service.CountMatchingPools(role with { Level = 0 }, 490704570) == (70, 0),
            "等级越界真实转职池仍为零匹配，与源最低等级资格独立核对");
        That(service.Open(role with { Job = 15, GrowType = 0 }, new(65, 1), out _) is
            { Accepted: false, Error: 19, Plan: null, RejectReason: "booster_source_ineligible" },
            "真实源职业资格排除枪剑士，优先拒绝且不扣源");
        That(service.Open(role with { Level = 0 }, new(65, 1), out _) is
            { Accepted: false, Error: 19, Plan: null, RejectReason: "booster_source_ineligible" },
            "低于真实源最低等级时优先拒绝，不抽其他职业或分支");

        var platService = new InventoryBoosterService(catalog, itemResources.Boosters, itemResources.GoldLimits, itemResources.EpicPieces,
            random, debugSettings: new GameDebugSettings { IgnoreCashShopAbsoluteExpiration = true });
        var platRole = role with { GrowType = 1, Inventory = new([new(1, Bag(65), Core(490705076, shopExpiration: true))]) };
        var platOpen = platService.Open(platRole, new(65, 1), out var platReceipt, now);
        That(platOpen.Accepted && platReceipt is { ItemId: 490705076, Rewards: [{ ItemId: 2540007, Count: 1 }] } &&
            platOpen.Plan!.After.Find(Bag(65)) is null,
            "白金徽章礼盒按同一筛池抽取匹配分支");
        Console.WriteLine($"PASS {Count - checks} booster grow-type assertions");

        ItemCore Core(int id, bool shopExpiration = false) =>
            InventoryRewardGrantService.Create(catalog.Get(id), utcNow: now, shopExpiration: shopExpiration);
    }

    static BoosterDefinition Parse(string body) =>
        BoosterCatalog.Parse("[stackable type]\n`[booster]` 0\n[booster info]\n[grow type]\n" + body + "\n[/grow type]\n[/booster info]");
    static InventorySlot Bag(short slot) => new(InventoryListType.Main, slot);

    sealed class BoosterTestsRandom : Random
    {
        public override long NextInt64(long maxValue) => 0;
    }
}
