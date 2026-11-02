using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.PcRoom;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.PcRoom;
using DfoServer.Infrastructure;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class PcRoomRewardTests
{
    public static void RunCore()
    {
        int checks = Count;
        const int loginId = 65001, rentalId = 65002, firstId = 65003, secondId = 65004;
        var items = new ItemCatalog(null!, new Dictionary<int, string>(), new Dictionary<int, string>(), 0);
        items.PrefillTemplates(new Dictionary<int, ItemTemplate>
        {
            [loginId] = new(loginId, ItemKind.Consumable, 0, 1, 0, 99, 0, 1, 1, "[account]", ushort.MaxValue, 0, "登录盒"),
            [rentalId] = new(rentalId, ItemKind.Consumable, 0, 1, 0, 99, 0, 1, 1, "[trade]", ushort.MaxValue, 0, "租赁盒"),
            [firstId] = new(firstId, ItemKind.Equipment, 12, 1, 30, 1, 0, 4, 85, "[sealing]", ushort.MaxValue, 0, "租赁武器一"),
            [secondId] = new(secondId, ItemKind.Equipment, 12, 1, 30, 1, 0, 4, 85, "[sealing]", ushort.MaxValue, 0, "租赁武器二")
        });
        var catalog = new PcRoomCatalog(new(loginId, 1, rentalId, 1249),
            [new(0, 1, [new(firstId, 0, 0)]), new(0, 85, [new(firstId, 7, 3), new(secondId, 7, 3)])]);
        var gold = new GoldLimitCatalog("[gold limit from level]\n0 2000000000\n[/gold limit from level]");
        var pieces = new EpicPieceCatalog("[piece list]\n66001 66002\n[/piece list]");
        var service = new PcRoomRewardService(catalog, items, gold, pieces);
        var now = DateTimeOffset.FromUnixTimeSeconds(1793152800);
        var ordinary = new InventoryItem(44, new(InventoryListType.Main, 65), new(rentalId, ItemKind.Consumable, 3));
        var character = new Character(1, 0, "PcRoom", 0, new(1, 0, 0, 0), null)
            { Job = 0, Level = 85, Inventory = new([ordinary]) };
        That(catalog.Weapons(0, 84).Length == 1 && catalog.Weapons(0, 85).Length == 2 && catalog.Weapons(1, 85).IsEmpty,
            "网吧武器按真实职业及等级档选择，不回退到其他职业");
        var gift = service.PlanClaim(character, true, now);
        That(gift.Accepted && gift.Plan!.After.Find(ordinary.Position) == ordinary && gift.Plan.After.Items.Length == 2,
            "租赁礼盒与同ID普通堆分开，原UID及数量不变");
        var lease = gift.Plan!.After.Items.Single(item => item.Uid == 0);
        That(lease.Core.IsRental && lease.ExpireTime == now.ToUnixTimeSeconds() + 86400,
            "网吧租赁礼盒固定领取后24小时，不采用下一次06点");
        var weapons = service.PlanWeapons(character, new byte[] { 0, 1 }, now);
        That(weapons.Accepted && weapons.Plan!.After.Items.Count(item => item.Core.Kind == ItemKind.Equipment) == 2 &&
            weapons.Plan.After.Items.Where(item => item.Core.Kind == ItemKind.Equipment).All(item => item.Uid == 0 &&
                item.Core.IsRental && item.Core.Upgrade == 7 && item.Core.GenuineUpgrade == 3 && item.ExpireTime == lease.ExpireTime),
            "批量武器规划保留强化锻造与统一期限，物理UID留给事务分配");
        var existing = new InventoryItem(55, new(InventoryListType.Equipment, 12),
            new(firstId, ItemKind.Equipment, 1) { IsRental = true, ExpireTime = lease.ExpireTime });
        var duplicate = character with { Inventory = new([ordinary, existing]) };
        That(!service.PlanWeapons(duplicate, new byte[] { 1, 0 }, now).Accepted && duplicate.Inventory.Items.Length == 2,
            "混合申请含已有未到期武器时整笔拒绝，不先发其他武器");
        That(!service.PlanWeapons(character, new byte[] { 0, 0 }, now).Accepted &&
            !service.PlanWeapons(character, new byte[] { 255 }, now).Accepted &&
            !service.PlanWeapons(character, Array.Empty<byte>(), now).Accepted,
            "重复候选、越界和空武器请求不产生资产计划");
        var inherited = InventoryRewardGrantService.TryGrantBatch(character, [new(rentalId, 2)], items, gold, pieces,
            now.AddHours(2), rentalDeadline: lease.ExpireTime);
        That(inherited.Accepted && inherited.Plan!.After.Find(ordinary.Position) == ordinary &&
            inherited.Plan.After.Items.Single(item => item.Uid == 0).Core is { IsRental: true } core && core.ExpireTime == lease.ExpireTime,
            "后续子盒发放继承源绝对截止且不污染永久堆");
        That(!InventoryRewardGrantService.TryGrantBatch(character, [new(rentalId, 1)], items, gold, pieces,
            DateTimeOffset.FromUnixTimeSeconds(lease.ExpireTime), rentalDeadline: lease.ExpireTime).Accepted,
            "原租期到期等号拒绝新发放，不通过开盒续期");
        var ordinaryGift = InventoryRewardGrantService.TryGrantBatch(character, [new(rentalId, 2)], items, gold, pieces, now);
        That(ordinaryGift.Accepted && ordinaryGift.Plan!.After.Items is [{ Core.Count: 5, Core.IsRental: false }],
            "未传租期的旧发奖入口仍按普通堆叠规则工作");
        ParseContracts();
        Console.WriteLine($"PASS {Count - checks} pc-room reward core checks");
    }

    static void ParseContracts()
    {
        string[] labels = ["[grade 1]", "[/grade 1]", "[grade 2]", "[/grade 2]", "[login reward info]", "[/login reward info]",
            "[various buff index]", "[/various buff index]", "[legendary rental item]"];
        PvfToken[] tokens = [new(3,0),new(0,8),new(3,1),new(3,2),new(0,8),new(3,3),
            new(3,4),new(0,1),new(0,100),new(0,1),new(3,5),new(3,6),new(0,1),new(0,1249),new(3,7),new(3,8),new(0,200)];
        var parsed = PcRoomPremiumFile.Parse(tokens, value => labels[value]);
        That(parsed.LoginRewards is [{ Grade: 1, ItemId: 100, Count: 1 }] && parsed.RentalItemId == 200 &&
            parsed.Buffs is [{ Grade: 1, BuffId: 1249 }] && parsed.AccelerationRates.IsEmpty,
            "网吧格式保留原奖品与Buff行，缺少燃烧倍率不造默认倍率");
        Reject<InvalidDataException>(() => PcRoomPremiumFile.Parse(tokens.Where((_, at) => at != 9).ToArray(), value => labels[value]),
            "网吧奖励行缺列时拒绝，不将下一标签当奖励");
    }

    public static void RunResources(PvfArchive archive, ItemResources resources)
    {
        int checks = Count;
        var catalog = new PcRoomCatalog(archive);
        That(catalog.Premium is { LoginItemId: 490007905, LoginCount: 1, RentalItemId: 490010101, BuffId: 1249,
            AccelerationExperiencePercent: 100 },
            "本版网吧奖励和燃烧比例来自实际声明");
        var service = new PcRoomRewardService(catalog, resources.Items, resources.GoldLimits, resources.EpicPieces);
        var now = DateTimeOffset.FromUnixTimeSeconds(1793152800);
        var character = new Character(1, 0, "PcRoomLease", 0, new(1, 0, 0, 0), null)
            { Job = 0, Level = 85, Inventory = new([]) };
        That(service.PlanClaim(character, true, now).Accepted && service.PlanWeapons(character, new byte[] { 0 }, now).Accepted,
            "本版租赁总盒和职业档武器均能以实际模板规划完整资产");
        int[] firstTier = [27163, 27360, 28055];
        var lowChoices = catalog.Weapons(9, 4);
        That(lowChoices.Select(choice => choice.ItemId).SequenceEqual(firstTier) &&
            lowChoices.All(choice => choice.Upgrade == 7 && choice.Refinement == 3) &&
            catalog.Weapons(9, 1).SequenceEqual(lowChoices) && catalog.Weapons(9, 19).SequenceEqual(lowChoices),
            "女鬼剑租赁首档从1级覆盖到19级，候选顺序及强化7锻造3按实际资源保留");
        That(catalog.Weapons(9, 20).Select(choice => choice.ItemId).SequenceEqual(new[] { 27164, 27361, 28056, 27763, 28329 }),
            "女鬼剑20级切到下一租赁档，不沿用首档三候选");
        var lowCharacter = character with { Job = 9, Level = 4 };
        var lowRental = service.PlanWeapons(lowCharacter, new byte[] { 0, 1 }, now);
        That(lowRental.Accepted && lowRental.Plan!.After.Items.Select(item => item.Core.ItemId).SequenceEqual(firstTier.Take(2)) &&
            lowRental.Plan.After.Items.All(item => item.Core is { IsRental: true, Attr: 7, GenuineUpgrade: 3 } &&
                item.ExpireTime == now.ToUnixTimeSeconds() + 86400),
            "4级女鬼剑按首档租赁资格规划两件10级武器，不把穿戴等级误当领取等级");
        That(firstTier.Take(2).Select(resources.Items.Get).All(template => template.MinimumLevel == 10 &&
            !InventoryMoveService.WearLevelSatisfied(lowCharacter, template, now.ToUnixTimeSeconds(), resources.Contracts)),
            "租赁领取不放宽原穿戴等级，无豁免的4级角色仍不能穿10级武器");
        Console.WriteLine($"PASS {Count - checks} pc-room reward resource checks");
    }
}
