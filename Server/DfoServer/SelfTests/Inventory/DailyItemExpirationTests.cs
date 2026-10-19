using DfoServer.GameWorld.Items;
using DfoServer.Game.Inventory.Services;
using PvfLib;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Session;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Rental;
using DfoServer.Game.Currency;
using DfoServer.Game.Settings;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing.Inventory;

internal static class DailyItemExpirationTests
{
    public static void RunCore()
    {
        var boundary = new DateTimeOffset(2027, 1, 1, 6, 0, 0, TimeSpan.FromHours(8));
        var daily = ItemExpiration.Parse(new PvfDocument("[daily delete item]\n1\n"));
        That(daily.Resolve(boundary.AddSeconds(-1)) == boundary.ToUnixTimeSeconds(),
            "每日删除新实例在下个UTC+8六点到期，不能永久化");
        That(daily.Resolve(boundary) == boundary.AddDays(1).ToUnixTimeSeconds(),
            "恰在六点及跨年创建严格取下一服务日");
        foreach (bool ignoreAbsolute in new[] { false, true })
        foreach (bool ignoreRelative in new[] { false, true })
            That(daily.Resolve(boundary.AddSeconds(-1), ignoreAbsolute, ignoreRelative) == boundary.ToUnixTimeSeconds(),
                "商城日期调试不取消每日删除期限");
        var early = ItemExpiration.Parse(new PvfDocument($"[daily delete item]\n1\n[expiration date]\n`{boundary.AddSeconds(-1).ToUnixTimeSeconds()}`\n"));
        That(early.Resolve(boundary.AddSeconds(-2)) == boundary.AddSeconds(-1).ToUnixTimeSeconds(),
            "已有更早普通期限不被每日边界延后");
        That(ItemExpiration.Parse(new PvfDocument("[daily delete item]\n0\n")).Resolve(boundary) == 0,
            "明确关闭每日删除不改变永久规则");
        foreach (int value in new[] { 2, -1, 14 })
        {
            var unknown = ItemExpiration.Parse(new PvfDocument($"[daily delete item]\n{value}\n"));
            That(unknown.DailyDelete == value && !unknown.SupportsCreation,
                "完整未知整数声明保留原资源元数据，不误作永久或每日1");
            Reject<InvalidDataException>(() => unknown.Resolve(boundary), "未知每日声明未证语义时拒绝新建");
        }
        foreach (string text in new[] { "", "1 0", "`1`", "1\n[daily delete item]\n1",
            "1\n[daily delete item", "1\n[/daily delete item]" })
            Reject<InvalidDataException>(() => ItemExpiration.Parse(new PvfDocument("[daily delete item]\n" + text)),
                "每日删除非法、重复或残缺声明拒绝");
        foreach (string text in new[] { "[outer]\n[daily delete item]\n1\n[/outer]", "[outer]\n[inner]\n[daily delete item]\n1\n[/outer]\n[/inner]",
            "[outer]\n[outer]\n[/outer]\n[daily delete item]\n1\n[/outer]" })
            Reject<InvalidDataException>(() => ItemExpiration.Parse(new PvfDocument(text)), "嵌套及交叉闭合声明不变永久");
        That(daily.Resolve(DateTimeOffset.FromUnixTimeSeconds(int.MaxValue)) == ItemExpiration.MaximumUnixTime,
            "每日截止超出安全范围时钳回最大值，不抛异常、降为永久或溢出");
        That((daily with { Days = 7 }).Resolve(boundary.AddSeconds(-1)) == boundary.ToUnixTimeSeconds(),
            "普通相对期长于每日截止时取更早的每日边界");
        var template = new ItemTemplate(100, ItemKind.Consumable, 0, 1, 0, 1000, 0, 0, 1, "[trade]", ushort.MaxValue, 0, "每日奖励")
            { Expiration = daily };
        That(InventoryRewardGrantService.Create(template, 10, boundary.AddSeconds(-1)).ExpireTime == boundary.ToUnixTimeSeconds(),
            "统一Create将每日截止写入完整核心");
        var unknownPool = new DungeonItemDropPool(new DungeonWeightedDropItem[]
        {
            new(template, 1), new(template with { Id = 101, Expiration = new(DailyDelete: 14) }, 1)
        });
        Reject<NotSupportedException>(() => new DungeonDropGenerationPlan("完整未知期限候选池", [new(1, unknownPool, 1, 1)]),
            "任一未知期限候选使完整计划显式未支持，不删候选或留待死亡异常");
        That(unknownPool.Entries.Length == 2 && unknownPool.TotalWeight == 2,
            "未支持期限不会修改原候选池及概率权重");
    }
    public static void RunResources(TownCatalog towns, ItemResources resources)
    {
        var boundary = new DateTimeOffset(2027, 1, 1, 6, 0, 0, TimeSpan.FromHours(8));
        var now = boundary.AddSeconds(-1);
        UnknownCreation(towns, resources, now);
        var template = resources.Items.Get(490704522);
        var core = InventoryRewardGrantService.Create(template, 10, now);
        That(core.ExpireTime == boundary.ToUnixTimeSeconds(), "真实522统一Create获得有限期完整核心");
        var source = new InventoryItem(1, new(InventoryListType.Main, 65), core);
        var character = new Character(1, 0, "DailyExpiry", 0, towns.Room, towns.InitialReturn) { Inventory = new([source]) };
        var clock = new DailyBoosterTestClock(now);
        var consumable = new InventoryConsumableUseService(resources.RecoveryConsumables, clock);
        var use = consumable.Plan(character, new(source.Position, 1, template.Id, 0), now.ToUnixTimeSeconds());
        That(use.Accepted && use.Plan!.CharacterPrecondition!.Matches(character), "真实522在期限前可按普通药剂使用");
        var shop = new InventoryShopService(resources.Items, resources.Shops, resources.GoldLimits, resources.EpicPieces, clock);
        var sell = shop.Sell(character, new(source.Position, 1, 0, 0, 0));
        That(sell.Accepted && sell.Plan!.CharacterPrecondition!.Matches(character), "真实522在期限前可出售并绑定源期限");
        clock.Advance(TimeSpan.FromSeconds(1));
        That(!use.Plan!.CharacterPrecondition!.Matches(character) && !sell.Plan!.CharacterPrecondition!.Matches(character) &&
            !consumable.Plan(character, new(source.Position, 2, template.Id, 0), clock.GetUtcNow().ToUnixTimeSeconds()).Accepted &&
            !shop.Sell(character, new(source.Position, 1, 0, 0, 0)).Accepted,
            "同一可前进Clock使使用与出售在恰到期时拒绝");
        var ordinary = source with { Core = core with { ExpireTime = 0 } };
        That(InventoryStackRuleService.CanShareStack(core, core with { Value = 1 }) &&
            !InventoryStackRuleService.CanShareStack(core, InventoryRewardGrantService.Create(template, 1, boundary)),
            "同服务日同截止可合堆，跨日新截止不合堆续期");
        That(!InventoryStackRuleService.CanShareStack(ordinary.Core, core), "历史零期限实例不与每日新奖励合并续期");
        That(InventoryCargoService.AllowsInsert(core, InventoryListType.PersonalCargo, template) &&
            !InventoryCargoService.AllowsInsert(core, InventoryListType.AccountCargo, template),
            "522维持个人仓库准入和账号仓库既有trade资格拒绝");
        Persistence(towns, resources, now, source, ordinary);
    }

    static void UnknownCreation(TownCatalog towns, ItemResources resources, DateTimeOffset now)
    {
        // 全量兼容检查发现的唯一未知整数：称号模板可供存量读取，不推断14的期限含义。
        var unknown = resources.Items.Get(400330115);
        That(unknown.Expiration.DailyDelete == 14 && !unknown.Expiration.SupportsCreation,
            "真实400330115模板完整装载并保留daily原值14");
        foreach (bool ignoreAbsolute in new[] { false, true })
        foreach (bool ignoreRelative in new[] { false, true })
            Reject<InvalidDataException>(() => InventoryRewardGrantService.Create(unknown, utcNow: now,
                shopExpiration: ignoreAbsolute, ignoreUsablePeriod: ignoreRelative),
                "未知14新建明确拒绝，商城忽略选项不能放行为永久或每日1");
        var character = new Character(1, 0, "ExpiryCompat", 0, towns.Room, towns.InitialReturn);
        string before = JsonSerializer.Serialize(character);
        foreach (var rewards in new InventoryReward[][]
        {
            [new(490704522, 1), new(unknown.Id, 1)],
            [new(unknown.Id, 1), new(490704522, 1)]
        })
        {
            var grant = InventoryRewardGrantService.TryGrantBatch(character, rewards, resources.Items,
                resources.GoldLimits, resources.EpicPieces, now, allowOverflowMail: true);
            That(!grant.Accepted && grant.Error == 19 && grant.Plan is null && JsonSerializer.Serialize(character) == before,
                "未知期限奖励在首尾均整批拒绝19，不返回部分发奖或溢出邮件计划");
        }
        var rental = new RentalCatalog(100, 100, 1000, [1],
            new Dictionary<(string Job, int Tier), IReadOnlyDictionary<int, int>>
            { [("[swordman]", 0)] = new Dictionary<int, int> { [unknown.Id] = 1 } });
        var paid = character with { Inventory = character.Inventory with
            { AccountWallet = AccountWallet.Empty.Set(CurrencyKind.RentalPoint, 10) } };
        var rent = new RentalService(rental, resources.Items).Rent(paid, unknown.Id, 0, 0, now);
        That(!rent.Accepted && rent.Plan is null && paid.Inventory.AccountWallet.RentalPoints == 10,
            "合法租赁分组中的未知期限称号安全拒绝，不抛异常或扣点");
        var legacy = new InventoryItem(2, new(InventoryListType.Main, 9), new(unknown.Id, unknown.Kind,
            InventoryRewardGrantService.InitialQuality) { Durability = unknown.Durability });
        var transferred = InventoryRewardGrantService.TryGrantItems(character, [legacy], resources.Items,
            resources.GoldLimits, resources.EpicPieces, now);
        That(transferred.Accepted && transferred.Plan!.After.Items.Single().Core == legacy.Core,
            "已有未知声明实例按完整核心转移，不追溯补戳或禁入");
        UnknownAvatars(character, resources, now);
    }

    static void UnknownAvatars(Character character, ItemResources resources, DateTimeOffset now)
    {
        // 复用既有装扮用例，仅临时改变期限元数据；缓存始终恢复，不污染其它组。
        const int packageId = 490705426, selectedId = 42611;
        var source = new InventoryItem(1000, new(InventoryListType.Main, 73),
            InventoryRewardGrantService.Create(resources.Items.Get(packageId), utcNow: now, shopExpiration: true));
        var role = character with { Job = 15, Level = 99, Inventory = new([source]) };
        var service = new InventoryAvatarPackageService(resources.Items, resources.AvatarPackages, resources.GoldLimits,
            resources.EpicPieces, new GameDebugSettings { IgnoreCashShopAbsoluteExpiration = true });
        byte ability = resources.AvatarPackages.GetAvatar(selectedId).Abilities.Max();
        var request = new AvatarPackageOpenRequest(73, 1, 0, 0, [selectedId], [new(selectedId, ability)]);
        That(service.Open(role, request, out _, now).Accepted, "未知期限装扮回归的合法选择对照成功");
        var avatar = resources.Items.Get(selectedId);
        try
        {
            resources.Items.PrefillTemplates([new(selectedId, avatar with { Expiration = new(DailyDelete: 14) })]);
            var ordinary = service.Open(role, request, out var receipt, now);
            var cera = service.OpenCeraPackage(role, new(73, [new(selectedId, ability)]), out var ceraReceipt, now);
            That(!ordinary.Accepted && ordinary.Error == 19 && ordinary.Plan is null && receipt is null &&
                !cera.Accepted && cera.Error == 19 && cera.Plan is null && ceraReceipt is null && role.Inventory.Items.Single() == source,
                "160及518装扮入口对未知期限沿原19拒绝，不扣源或部分发装扮");
        }
        finally { resources.Items.PrefillTemplates([new(selectedId, avatar)]); }
        const int shopAvatarId = 106550541;
        var shopAvatar = resources.Items.Get(shopAvatarId);
        var shopper = character with { Job = 3, Level = 60, Inventory = InventorySnapshot.Empty with
            { AccountWallet = AccountWallet.Empty.Set(CurrencyKind.Cera, 10_000) } };
        var shop = new InventoryCashShopService(resources.Items, resources.CashShop, resources.GoldLimits,
            resources.EpicPieces, resources.Cargo, contracts: resources.Contracts, avatars: resources.AvatarPackages);
        var buy = new CashShopPurchaseRequest(0, [new(270623, 4, 3)]);
        That(shop.Buy(shopper, buy, out _, now).Accepted, "未知期限商城回归的合法装扮购买对照成功");
        try
        {
            resources.Items.PrefillTemplates([new(shopAvatarId, shopAvatar with { Expiration = new(DailyDelete: 14) })]);
            var rejected = shop.Buy(shopper, buy, out var receipt, now);
            That(!rejected.Accepted && rejected.Error == 17 && rejected.Plan is null && receipt is null &&
                shopper.Inventory.Items.IsEmpty && shopper.Inventory.AccountWallet.Get(CurrencyKind.Cera) == 10_000,
                "商城散件装扮未知期限沿原17拒绝，不扣点券或发默认永久件");
        }
        finally { resources.Items.PrefillTemplates([new(shopAvatarId, shopAvatar)]); }
    }

    static void Persistence(TownCatalog towns, ItemResources resources, DateTimeOffset now, InventoryItem source, InventoryItem ordinary)
    {
        string root = Path.Combine(Path.GetTempPath(), "daily-item-expiry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "game.db");
            var repository = new CharacterRepository(path);
            using var owner = new AccountOwners().Acquire("daily-expiry", () => { });
            var created = repository.Create(owner, new("DailyLife", towns.Room, towns.InitialReturn) { Inventory = new([
                source with { Uid = 0 }, ordinary with { Uid = 0, Position = new(InventoryListType.Main, 66) }]) });
            var toCargo = new InventoryMoveRequest(source.Position, source.Core.ItemId, 3, new(InventoryListType.PersonalCargo, 0), 0, 0, 0, 0, 0);
            var move = InventoryMoveService.Plan(created, toCargo, resources.Items, now.ToUnixTimeSeconds());
            That(move.Accepted, "每日奖励允许拆堆存个人仓库");
            // 移动路径沿用既有系统时钟；此处提交早于固定未来期限，之后重开核对原核心。
            created = repository.CommitInventory(owner, created, move.Plan!);
            var restored = new CharacterRepository(path).List(owner).Single();
            That(restored.Inventory.Find(new(InventoryListType.PersonalCargo, 0))!.Core == (source.Core with { Value = 3 }) &&
                restored.Inventory.Find(source.Position)!.Core == (source.Core with { Value = 7 }) &&
                restored.Inventory.Find(new(InventoryListType.Main, 66))!.Core.ExpireTime == 0,
                "拆堆、换槽、个人仓库保存重开保留原每日截止，历史零期限不补戳");
            foreach (bool selling in new[] { false, true })
            {
                var clock = new CommitExpiryClock(now);
                var request = new ConsumableUseRequest(source.Position, 1, source.Core.ItemId, 0);
                var plan = selling
                    ? new InventoryShopService(resources.Items, resources.Shops, resources.GoldLimits, resources.EpicPieces, clock)
                        .Sell(restored, new(source.Position, 1, 0, 0, 0)).Plan!
                    : new InventoryConsumableUseService(resources.RecoveryConsumables, clock).Plan(restored, request, now.ToUnixTimeSeconds()).Plan!;
                string before = JsonSerializer.Serialize(repository.List(owner));
                clock.Arm();
                Reject<InventoryQualificationExpiredException>(() => repository.CommitInventory(owner, restored, plan),
                    "正常源期限跨界在真实事务内以专用类型拒绝使用/售卖");
                That(clock.Reads >= 2 && JsonSerializer.Serialize(repository.List(owner)) == before,
                    "提交前仍有效、写事务内恰到期：不扣源、不加金币、不改revision");
            }
            var expiry = InventoryExpiryService.Plan(restored.Inventory, now.AddSeconds(1).ToUnixTimeSeconds());
            That(expiry.Accepted, "选角共用期限清理规划可处理每日奖励");
            var cleaned = repository.CommitInventory(owner, restored, expiry.Plan!);
            That(cleaned.Inventory.Items.Length == 1 && cleaned.Inventory.Items.Single().Core.ExpireTime == 0 &&
                cleaned.Inventory.Find(new(InventoryListType.Main, 66))!.Core.ItemId == source.Core.ItemId,
                "选角清理已到期背包及个人仓库新实例，历史零期限同模板保留");
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    // 冻结规划结果后，第一次提交预检仍有效，第二次读取才跨期限，验证真实事务内的复验。
    sealed class CommitExpiryClock(DateTimeOffset now) : TimeProvider
    {
        bool armed;
        public int Reads { get; private set; }
        public void Arm() { armed = true; Reads = 0; }
        public override DateTimeOffset GetUtcNow() => armed && ++Reads > 1 ? now.AddSeconds(1) : now;
    }

}
