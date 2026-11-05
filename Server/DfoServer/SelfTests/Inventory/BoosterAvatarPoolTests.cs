using static DfoServer.Testing.TestCheck;
using System.Collections.Concurrent;
using System.Reflection;
using DfoServer.Infrastructure;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Settings;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using PvfLib;

namespace DfoServer.Testing.Inventory;

internal static class BoosterAvatarPoolTests
{
    public static void RunCore()
    {
        var single = Parse("[avatar]\n1 113590008 10000 1 2 0\n[/avatar]");
        That(single.Available && single.Pools.Single().Rewards.Single() is
            { ItemId: 113590008, Weight: 10000, Count: 1, AvatarAbility: null, AvatarPeriodDays: null },
            "单行装扮奖池仍只保留物品、权重和数量");
        var rows = Parse("[avatar]\n2 11 1 1 0 3 12 3 1 45 4\n[/avatar]");
        That(rows.Available && rows.Pools.Single() is { Draws: 2 } pool &&
            pool.Rewards is [{ ItemId: 11, Weight: 1, Count: 1, AvatarPeriodDays: 0, AvatarAbility: 3 },
                { ItemId: 12, Weight: 3, Count: 1, AvatarPeriodDays: 45, AvatarAbility: 4 }],
            "多行装扮奖池按五列保留期限和属性，抽数仍是段首整数");
        var special = Parse("[special avatar]\n1 43102 1000 1 45 0\n[/special avatar]");
        That(special.Available && special.Pools.Single().Rewards.Single() is
            { ItemId: 43102, Weight: 1000, Count: 1, AvatarPeriodDays: 45, AvatarAbility: 0 },
            "单行 special avatar 也读取期限天数和属性选项");
        // 8 行 × 5 列同时被 4 整除，不能再当成行宽歧义整盒拒绝。
        string eight = "2 " + string.Join(' ', Enumerable.Range(0, 8).Select(index => $"{100 + index} {index + 1} 1 0 {index}"));
        var wide = Parse("[avatar]\n" + eight + "\n[/avatar]");
        That(wide.Available && wide.Pools.Single().Rewards.Length == 8 &&
            wide.Pools[0].Rewards[7] is { ItemId: 107, Weight: 8, AvatarAbility: 7 },
            "八行装扮池按五列解析，不因同时整除四列而拒绝");
        foreach (string bad in new[]
        {
            "[avatar]\n1 11 1 1 0 3 12 1 1 0\n[/avatar]",
            "[special avatar]\n1 11 1 1 0\n[/special avatar]",
            "[special avatar]\n1 11 1 1 -1 0\n[/special avatar]",
            "[avatar]\n2 11 0 1 0 0 12 1 1 0 1\n[/avatar]",
            "[avatar]\n2 11 1 1 0 256 12 1 1 0 1\n[/avatar]"
        })
            That(!Parse(bad).Available, "非五列多行、负期限、零权重或越界属性整盒拒绝");
    }

    public static void RunResources(TownCatalog towns, CharacterCatalog characters, ItemResources resources)
    {
        _ = characters;
        var now = DateTimeOffset.UnixEpoch.AddSeconds(1_700_000_000);
        var aura = resources.Boosters.Get(490004995);
        That(aura.Available && aura.Pools.Single().Rewards.Single() is
            { ItemId: 113590008, Count: 1, AvatarAbility: null, AvatarPeriodDays: null },
            "真实单行礼盒490004995仍不把尾列当成期限或属性");
        OpenAura(towns, resources, now);
        foreach (var (itemId, period) in new[] { (64, 45), (63, 45) })
        {
            var definition = resources.Boosters.Get(itemId);
            That(definition.Available && definition.PendingRule is null &&
                definition.Pools.Where(pool => pool.Rewards.Any(row => row.AvatarPeriodDays is not null))
                    .All(pool => pool.Rewards.All(row => row.AvatarPeriodDays == period && row.AvatarAbility == 0)),
                "海之勇者礼盒的 special avatar 为 45 天、属性 0 " + itemId);
        }
        var bear = resources.Boosters.Get(52);
        var rare = bear.Pools.SelectMany(pool => pool.Rewards).Where(row => row.ItemId == 39085).ToArray();
        That(bear.Available && bear.Pools.Any(pool => pool.Draws == 2 && pool.Rewards.Length > 1) &&
            rare.Any(row => row is { AvatarAbility: 0, Weight: 100, AvatarPeriodDays: 0 }) &&
            rare.Any(row => row is { AvatarAbility: 1, Weight: 50, AvatarPeriodDays: 0 }),
            "野生力量熊套装保留多行奖池，稀有属性权重低于普通行");
        foreach (int itemId in new[] { 53, 54, 55, 56, 65, 66, 67, 69, 70, 71, 72, 73 })
            That(resources.Boosters.Get(itemId).Available &&
                resources.Boosters.Get(itemId).Pools.Any(pool => pool.Rewards.Any(row => row.AvatarAbility is not null)),
                "在售装扮礼盒奖池已可解析 " + itemId);
        (int Commodity, int Item)[] sea =
        [
            (3604, 70), (3605, 64), (3606, 71), (3607, 65), (3608, 72),
            (3609, 66), (3610, 73), (3611, 67), (3612, 69), (3613, 63)
        ];
        foreach (var (commodity, item) in sea)
            That(resources.CashShop.Product(commodity) is { CeraPrice: 8800, Count: 1 } product && product.ItemId == item,
                "海之勇者商品号、物品和 8800 点券 " + commodity);
        That(resources.CashShop.Product(5129) is { ItemId: 10008024, CeraPrice: 18800 },
            "5129 的后写行是百鬼夜行礼包，不是野生力量物品 52");
        BuyAndOpenSea(towns, resources, now);
        OpenWild(towns, resources, now);
        DrawDistribution(towns, resources, now);
        MixedAvatarRows(towns, resources, now);
    }

    static void MixedAvatarRows(TownCatalog towns, ItemResources resources, DateTimeOffset now)
    {
        const int avatarId = 43102;
        const string pools = "[avatar]\n1 43102 1000 1 0\n[/avatar]\n" +
            "[special avatar]\n1 43102 1000 1 45 4\n[/special avatar]";
        var boosters = Isolated(resources, 64, pools);
        var service = Service(resources, boosters, new ScriptedRandom([]));
        int periodDeadline = new ItemExpiration(Days: 45).Resolve(now);
        var template = resources.Items.Get(avatarId);
        int templateDeadline = template.Expiration.Resolve(now, true);
        foreach (int count in new[] { 1, 2 })
        {
            var role = Role(towns, resources.Items.Get(64), count, now);
            var opened = service.Open(role, new(65, count), out var receipt, now);
            var awarded = opened.Plan?.After.Items.Where(item => item.Core.ItemId == avatarId).ToArray() ?? [];
            That(opened.Accepted && receipt?.Rewards.Single().Count == 2 * count && awarded.Length == 2 * count &&
                awarded.Count(item => item.Core.Durability == 0 && item.Avatar?.ExpireTime == templateDeadline) == count &&
                awarded.Count(item => item.Core.Durability == 4 && item.Avatar?.ExpireTime == periodDeadline) == count &&
                opened.Plan!.After.Find(new(InventoryListType.Main, 65)) is null,
                "普通与显式装扮池的同ID奖励全部到账并分别保留属性、期限，开盒数量 " + count +
                    "，实际实例 " + awarded.Length);
        }
        var tight = Role(towns, resources.Items.Get(64), 1, now);
        var range = ItemSlotBoundService.Range(ItemKind.Avatar, tight.Inventory);
        var prototype = InventoryRewardGrantService.Create(template, utcNow: now, shopExpiration: true);
        var occupied = Enumerable.Range(range.Start, range.End - range.Start)
            .Select(index => InventoryRewardGrantService.CreateItem(template,
                new(InventoryListType.Avatar, checked((short)index)), prototype with { Value = index + 2 }) with { Uid = index + 2 });
        tight = tight with { Inventory = tight.Inventory with { Items = tight.Inventory.Items.AddRange(occupied) } };
        tight.Inventory.Validate();
        var rejected = service.Open(tight, new(65, 1), out var failedReceipt, now);
        That(!rejected.Accepted && rejected.Error == 4 && rejected.Plan is null && failedReceipt is null &&
            tight.Inventory.Find(new(InventoryListType.Main, 65))?.Core.Count == 1,
            "同ID装扮奖励仍需两个槽，只有一个空槽时整笔拒绝且保留源礼盒");
    }

    static void OpenAura(TownCatalog towns, ItemResources resources, DateTimeOffset now)
    {
        var role = Role(towns, resources.Items.Get(490004995), 1, now);
        var service = Service(resources, resources.Boosters, new ScriptedRandom([0]));
        var opened = service.Open(role, new(65, 1), out var receipt, now);
        var avatar = opened.Plan?.After.Items.SingleOrDefault(item => item.Core.ItemId == 113590008);
        That(opened.Accepted && receipt!.Rewards.Any(row => row.ItemId == 113590008 && row.Count == 1) &&
            avatar is { Core.Durability: 0, Avatar: { ExpireTime: 0 } },
            "开启490004995仍发放无属性、模板期限的转职光环");
    }

    static void BuyAndOpenSea(TownCatalog towns, ItemResources resources, DateTimeOffset now)
    {
        var role = new Character(1, 0, "SeaHero", 0, towns.Room, towns.InitialReturn)
        {
            Job = 0,
            Level = 40,
            Inventory = InventorySnapshot.Empty with { AccountWallet = AccountWallet.Empty.Set(CurrencyKind.Cera, 100_000) }
        };
        var shop = new InventoryCashShopService(resources.Items, resources.CashShop, resources.GoldLimits, resources.EpicPieces,
            resources.Cargo, contracts: resources.Contracts);
        var bought = shop.Buy(role, new(0, [new(3605, 0)]), out var purchase, now);
        var box = bought.Plan?.After.Items.SingleOrDefault(item => item.Core.ItemId == 64);
        That(bought.Accepted && purchase is not null && box is not null &&
            bought.Plan!.After.AccountWallet.Get(CurrencyKind.Cera) == 100_000 - 8800,
            "8800 点券可购买海之勇者礼包 1，商品 3605 发放物品 64");
        if (box is null)
            return;
        var opened = Service(resources, resources.Boosters, new ScriptedRandom(Enumerable.Repeat(0L, 32).ToArray())).Open(
            role with { Inventory = bought.Plan!.After }, new(box.Position.Index, 1), out _, now);
        That(!opened.Accepted && opened.Error == 19 && resources.Boosters.DirectActivationItems.Contains(29) &&
            bought.Plan!.After.Find(box.Position) == box,
            "海之勇者整盒仍拒绝：奖池含直接激活的 29 号无限疲劳服务，不能当成普通道具发掉");
    }

    static void OpenWild(TownCatalog towns, ItemResources resources, DateTimeOffset now)
    {
        var role = Role(towns, resources.Items.Get(52), 1, now);
        var opened = Service(resources, resources.Boosters, new ScriptedRandom(Enumerable.Repeat(0L, 32).ToArray())).Open(
            role, new(65, 1), out _, now);
        var avatars = opened.Plan?.After.Items.Where(item => item.Position.List == InventoryListType.Avatar).ToArray() ?? [];
        That(opened.Accepted && avatars.Length >= 2 && avatars.All(item => item.Avatar is not null) &&
            avatars.Any(item => item.Core.ItemId == 39529 && item.Core.Durability == 0 && item.Avatar!.ExpireTime == 0),
            "开启野生力量熊套装按首行发放期限 0 的装扮 " + opened.Error + " " + opened.RejectReason);
    }

    static void DrawDistribution(TownCatalog towns, ItemResources resources, DateTimeOffset now)
    {
        const string replacement = "[special avatar]\n2 43102 1 1 45 0 43102 3 1 0 4\n[/special avatar]";
        var repeated = OpenSynthetic(towns, resources, replacement, [0, 0], 1, now);
        var copies = repeated.Where(item => item.Core.ItemId == 43102).ToArray();
        int days = new ItemExpiration(Days: 45).Resolve(now);
        That(copies.Length == 2 && copies.All(item => item.Core.Durability == 0 && item.Avatar?.ExpireTime == days),
            "同一段抽 2 次有放回，两次都可以命中权重更小的第一行");
        const string weighted = "[special avatar]\n1 43102 1 1 45 0 43102 3 1 0 4\n[/special avatar]";
        var drawn = OpenSynthetic(towns, resources, weighted, [0, 1, 2, 3], 4, now);
        var common = drawn.Where(item => item.Core.Durability == 4 && item.Avatar?.ExpireTime == 0).ToArray();
        var rare = drawn.Where(item => item.Core.Durability == 0 && item.Avatar?.ExpireTime == days).ToArray();
        That(drawn.Length == 4 && rare.Length == 1 && common.Length == 3,
            "权重 1:3 的属性行按独立抽取落入对应次数，稀有行只命中一次");
    }

    static InventoryItem[] OpenSynthetic(TownCatalog towns, ItemResources resources, string pools, long[] rolls, int count, DateTimeOffset now)
    {
        const int boxId = 64;
        var boosters = Isolated(resources, boxId, pools);
        var role = Role(towns, resources.Items.Get(boxId), count, now);
        var service = Service(resources, boosters, new ScriptedRandom(rolls));
        for (int i = 0; i < count; i++)
        {
            var opened = service.Open(role, new(65, 1), out _, now);
            That(opened.Accepted, "合成装扮奖池可开启 " + opened.RejectReason);
            if (!opened.Accepted || opened.Plan is null)
                return [];
            role = role with { Inventory = opened.Plan.After };
        }
        return role.Inventory.Items.Where(item => item.Position.List == InventoryListType.Avatar).ToArray();
    }

    static InventoryBoosterService Service(ItemResources resources, BoosterCatalog boosters, Random random) =>
        new(resources.Items, boosters, resources.GoldLimits, resources.EpicPieces, random,
            debugSettings: new GameDebugSettings { IgnoreCashShopAbsoluteExpiration = true },
            avatars: resources.AvatarPackages);

    static BoosterCatalog Isolated(ItemResources resources, int itemId, string pools)
    {
        var archive = (PvfArchive)typeof(ItemCatalog).GetField("pvf", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(resources.Items)!;
        var catalog = new BoosterCatalog(archive, new Dictionary<int, string>());
        var definitions = (ConcurrentDictionary<int, BoosterDefinition>)typeof(BoosterCatalog)
            .GetField("definitions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(catalog)!;
        definitions[itemId] = Parse(pools);
        return catalog;
    }

    static Character Role(TownCatalog towns, ItemTemplate box, int count, DateTimeOffset now)
    {
        var core = InventoryRewardGrantService.Create(box, count, utcNow: now, shopExpiration: true);
        return new(1, 0, "AvatarPool", 0, towns.Room, towns.InitialReturn)
        {
            Job = 0,
            Level = 40,
            Inventory = new([new(1, new(InventoryListType.Main, 65), core)])
        };
    }

    static BoosterDefinition Parse(string pools) => BoosterCatalog.Parse(
        "[stackable type]\n`[cera booster]` 0\n[booster info]\n" + pools + "\n[/booster info]");

    sealed class ScriptedRandom(long[] values) : Random
    {
        int index;
        public override long NextInt64(long maxValue)
        {
            long value = values[index++];
            if ((ulong)value >= (ulong)maxValue)
                throw new InvalidOperationException("脚本随机数越出本次权重");
            return value;
        }
    }
}
