using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.EpicRoad2019;
using DfoServer.Game.Events;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class EpicRoad2019ShopTests
{
    const int ShopId = 400000098, NpcId = 400001105;
    static readonly (int Id, int Material, int Cost, int Gold, ItemPurchasePeriod? Period, int Limit)[] Stock =
    [
        (490013751,490013716,50,0,ItemPurchasePeriod.Accumulate,5),
        (490013718,490013716,100,0,ItemPurchasePeriod.Accumulate,5),
        (490013719,490013716,100,0,ItemPurchasePeriod.Accumulate,5),
        (490013724,490013716,400,0,ItemPurchasePeriod.Accumulate,5),
        (490013810,490013716,420,0,ItemPurchasePeriod.Monthly,5),
        (490013725,490013716,100,0,ItemPurchasePeriod.Weekly,5),
        (490013726,490013716,60,0,ItemPurchasePeriod.Weekly,5),
        (490013727,490013716,1,0,null,0),
        (490013728,10158964,3,0,null,0),
        (490013729,0,0,80,ItemPurchasePeriod.Daily,5)
    ];

    public static void RunResources(TownCatalog towns, ItemResources resources)
    {
        foreach (int id in new[] { 490013716, 490013717 })
            That(resources.Items.Get(id) is { AccountCommonItem: true, AccountMaterial: null },
                "星河公共材料保留45实体解释，不被账号余额分支遮蔽：" + id);
        That(resources.Items.Get(490011740).AccountMaterial is { CommonInventory: true } &&
            resources.Items.Get(10308807).AccountMaterial is { CommonInventory: false },
            "非活动共通材料与普通账号材料保留上游余额及容器声明");
        RunDefaultClosedResources(towns, resources);
        RunEnabledResources(towns, resources, clock => new(resources.Items, resources.Shops,
            resources.GoldLimits, resources.EpicPieces, clock, epicRoadAvailable: true));
        RunSessionResources(resources);

        int checks = Count;
        using var f = new Fixture(towns, resources);
        const int materialId = 490013716;
        That(!f.Current.Inventory.Items.Any(item => item.Core.ItemId == materialId),
            "716容器RED从既有空库存夹具发放真实资源，不预置45实例");
        var grant = InventoryRewardGrantService.TryGrantBatch(f.Current, [new(materialId, 1)],
            resources.Items, resources.GoldLimits, resources.EpicPieces, f.Clock.GetUtcNow());
        That(grant.Accepted && grant.Plan is not null, "真实716正式发奖规划成功，不能以资源或余额不足冒容器RED");
        var granted = grant.Plan!.After.Items.Where(item => item.Core.ItemId == materialId).ToArray();
        That(granted.Length == 1 && granted[0].Core.Count == 1 &&
            granted[0].Position.List == (InventoryListType)45 && granted[0].Position.Index is >= 0 and < 50 &&
            !grant.Plan.After.Items.Any(item => item.Core.ItemId == materialId && item.Position.List == InventoryListType.Main),
            "真实716新发必须进入45容器的0至49槽，不能仍落Main或转成余额");
        f.Passed = true;
        Console.WriteLine($"PASS {Count - checks} 星河716正式发奖容器断言（仅规划）");
        RunCommonMaterialResources(towns, resources);
    }

    // 只检验真实716的规划、旧Main兼容与ACK材料语义；持久化/CAS沿原十SKU入口回归。
    static void RunCommonMaterialResources(TownCatalog towns, ItemResources resources)
    {
        int checks = Count;
        using var f = new Fixture(towns, resources);
        const int materialId = 490013716;
        var template = resources.Items.Get(materialId);
        var prototype = InventoryRewardGrantService.Create(template, utcNow: f.Clock.GetUtcNow());
        var mainSlot = new InventorySlot(InventoryListType.Main, 121);
        var commonSlot = new InventorySlot(InventoryListType.AccountCommon, 0);
        var legacy = new InventoryItem(9001, mainSlot, prototype with { Value = 5, SortLockFlag = 1 });
        var common = new InventoryItem(9002, commonSlot, prototype with { Value = 100 });
        // 故意反置数组顺序，不能借枚举顺序实现Main先消费。
        var mixed = f.Current with { Inventory = f.Current.Inventory with { Items = [common, legacy] } };
        mixed.Inventory.Validate();
        string original = JsonSerializer.Serialize(mixed.Inventory);
        var spent = InventoryMaterialConsumptionService.Plan(mixed.Inventory, [new(materialId, 1)], resources.EpicPieces, f.Clock.GetUtcNow());
        That(spent.Accepted && spent.Plan!.ChangedSlots.Length == 1 && spent.Plan.ChangedSlots.Contains(mainSlot) &&
            spent.Plan.After.Find(mainSlot) == legacy with { Core = legacy.Core with { Value = 4 } } &&
            spent.Plan.After.Find(commonSlot) == common && JsonSerializer.Serialize(mixed.Inventory) == original,
            "混合Main5与45一百先扣Main一份，保UID完整属性和未消费45，仅Main变为4");
        f.Events.Override(ServerEventService.EpicRoadId, true);
        var guard = new InventoryEventPrecondition(f.Events.Read(ServerEventService.EpicRoadId).Schedule, f.Clock);
        var shop = new InventoryShopService(resources.Items, resources.Shops, resources.GoldLimits, resources.EpicPieces,
            f.Clock, epicRoadAvailable: true);
        var purchase = shop.Buy(mixed, new(490013727, 1, ShopId, NpcId), out var receipt, requiredEvent: guard);
        That(purchase.Accepted && receipt is not null && receipt.Materials.IsEmpty &&
            receipt.Paid.Find(mainSlot) == spent.Plan!.After.Find(mainSlot) && receipt.Paid.Find(commonSlot) == common,
            "真实727扣旧Main时ACK材料数为0，不能把Main余4写成45余量");
        var onlyCommon = f.Current with { Inventory = f.Current.Inventory with { Items = [common] } };
        var commonPurchase = shop.Buy(onlyCommon, new(490013727, 1, ShopId, NpcId), out var commonReceipt, requiredEvent: guard);
        That(commonPurchase.Accepted && commonReceipt is not null && commonReceipt.Materials.IsEmpty &&
            commonReceipt.Paid.Find(commonSlot) == common with { Core = common.Core with { Value = 99 } } &&
            commonPurchase.Plan!.ChangedSlots.Contains(commonSlot),
            "45-only真实727购买成功且精确扣一份，不再查不存在的Main首格");
        That(!InventoryDeleteService.Plan(onlyCommon.Inventory, commonSlot, common.Core.Value, 1).Accepted,
            "共享扣材的可选45删除不开放普通丢弃入口");
        var later = common with { Uid = 9003, Position = new(InventoryListType.AccountCommon, 3), Core = prototype with { Value = 5 } };
        var earlier = common with { Position = new(InventoryListType.AccountCommon, 1), Core = prototype with { Value = 2 } };
        var reversed = onlyCommon.Inventory with { Items = [later, earlier] };
        var ordered = InventoryMaterialConsumptionService.Plan(reversed, [new(materialId, 4)], resources.EpicPieces, f.Clock.GetUtcNow());
        That(ordered.Accepted && ordered.Plan!.After.Find(earlier.Position) is null &&
            ordered.Plan.After.Find(later.Position) == later with { Core = later.Core with { Value = 3 } } && ordered.Plan.ChangedSlots.Length == 2,
            "45内部按槽位消费，删尽低格并保高格原UID与属性");
        var duplicate = InventoryMaterialConsumptionService.Plan(mixed.Inventory,
            [new(materialId, 60), new(materialId, 60)], resources.EpicPieces, f.Clock.GetUtcNow());
        That(!duplicate.Accepted && duplicate.Plan is null && JsonSerializer.Serialize(mixed.Inventory) == original,
            "重复716费用汇总后不足整笔拒绝，不能部分扣除Main或45");
        foreach (bool locked in new[] { true, false })
        {
            var unavailable = common with { Core = locked ? common.Core with { EquipmentLockId = 1 } :
                common.Core with { ExpireTime = checked((int)f.Clock.GetUtcNow().AddSeconds(-1).ToUnixTimeSeconds()) } };
            var blocked = mixed.Inventory with { Items = [unavailable, legacy] };
            string snapshot = JsonSerializer.Serialize(blocked);
            var denied = InventoryMaterialConsumptionService.Plan(blocked, [new(materialId, 6)], resources.EpicPieces, f.Clock.GetUtcNow());
            That(!denied.Accepted && denied.Plan is null && JsonSerializer.Serialize(blocked) == snapshot,
                "45锁定或过期不能补足Main差额，整批原快照不变：" + locked);
        }
        var added = InventoryRewardGrantService.TryGrantBatch(mixed, [new(materialId, 1)],
            resources.Items, resources.GoldLimits, resources.EpicPieces, f.Clock.GetUtcNow());
        That(added.Accepted && added.Plan!.After.Find(mainSlot) == legacy &&
            added.Plan.After.Find(commonSlot) == common with { Core = common.Core with { Value = 101 } },
            "新发716只合45兼容堆，旧Main原UID和数量保持不动");
        var full = mixed with { Inventory = mixed.Inventory with { Items = [legacy,
            ..Enumerable.Range(0, ItemSlotBoundService.AccountCommonCapacity).Select(slot =>
                new InventoryItem(9100 + slot, new(InventoryListType.AccountCommon, checked((short)slot)),
                    prototype with { Value = template.StackLimit }))] } };
        full.Inventory.Validate();
        string fullSnapshot = JsonSerializer.Serialize(full.Inventory);
        var overflow = InventoryRewardGrantService.TryGrantBatch(full, [new(materialId, 1)],
            resources.Items, resources.GoldLimits, resources.EpicPieces, f.Clock.GetUtcNow());
        That(!overflow.Accepted && overflow.Plan is null && JsonSerializer.Serialize(full.Inventory) == fullSnapshot,
            "45五十格满时默认整笔拒绝，不能合旧Main或借空快捷槽");
        f.Passed = true;
        Console.WriteLine($"PASS {Count - checks} 星河45实体发奖、兼容扣材与材料ACK断言（仅规划）");
    }

    // 默认资源关闭时，不能凭商店已在PVF中就购买。
    static void RunDefaultClosedResources(TownCatalog towns, ItemResources resources)
    {
        int checks = Count;
        using var f = new Fixture(towns, resources);
        var shop = new InventoryShopService(resources.Items, resources.Shops, resources.GoldLimits, resources.EpicPieces, f.Clock);
        var request = new BuyItemRequest(490013729, 1, ShopId, NpcId);
        That(resources.Shops.Allows(NpcId, ShopId, request.ItemId) &&
            resources.Items.Get(request.ItemId).ShopPrice.Gold == 80 && f.Current.Inventory.Gold >= 80,
            "默认关闭RED使用真实在售729、80金币且有足额资产，不能用错误商品/不足余额假拒绝");
        f.Events.Override(ServerEventService.EpicRoadId, true);
        var guard = new InventoryEventPrecondition(f.Events.Read(ServerEventService.EpicRoadId).Schedule, f.Clock);
        var bare = shop.Buy(f.Current, request, out var bareReceipt);
        var active = shop.Buy(f.Current, request, out var activeReceipt, requiredEvent: guard);
        That(!bare.Accepted && !active.Accepted && bareReceipt is null && activeReceipt is null,
            $"资源默认关闭时裸service及仅有10005有效guard均拒绝：bare={bare.Accepted}, active={active.Accepted}," +
            $" bareError={bare.Error}, activeError={active.Error}");
        var session = new GameSession(f.Repository, towns, f.Owner, timeProvider: f.Clock, serverEvents: f.Events);
        try
        {
            session.Select(f.Current.Slot); session.MarkReady();
            bool invoked = false;
            var bypass = session.PlanAndCommitShopBuy(ShopId, character =>
            {
                invoked = true;
                return shop.Buy(character, request, out _);
            });
            That(!bypass.Accepted && !invoked,
                "旧普通Session购买入口直接拒绝星河98，不能运行调用者自带规划绕过专用资格");
        }
        finally { session.End(); }
        // 选角允许正常维护；对比在旧入口操作之后另核零购买，不把选角维护误当扣费。
        That(f.Repository.LoadCharacter(f.Owner, f.Current.Slot).Inventory.ShopPurchases.All(row => row.ItemId != request.ItemId) &&
            f.Repository.LoadCharacter(f.Owner, f.Current.Slot).Inventory.Gold == 100_000,
            "默认关闭路径不扣80金币，也不占用729每日一次额度");
        var ordinary = shop.Buy(f.Repository.LoadCharacter(f.Owner, f.Current.Slot), new(3175, 1, 2, 1), out var ordinaryReceipt);
        That(ordinary.Accepted && ordinaryReceipt is not null,
            "新增98保护不影响原NPC1/店2的真实普通商品3175");
        f.Passed = true;
        Console.WriteLine($"PASS {Count - checks} 星河商店默认关闭与普通入口隔离断言");
    }

    // enabledShop由root用显式资源可用装配构造；测试不反射私有成员或修改全局活动状态。
    // 本入口只覆盖service/原仓储合同，Session资格由独立合成商人夹具承担。
    public static void RunEnabledResources(TownCatalog towns, ItemResources resources,
        Func<TimeProvider, InventoryShopService> enabledShop)
    {
        int checks = Count;
        using var f = new Fixture(towns, resources, materials: true);
        var shop = enabledShop(f.Clock);
        That(ReferenceEquals(shop.Clock, f.Clock) &&
            resources.Shops.Shops[ShopId].NpcId == NpcId &&
            resources.Shops.Shops[ShopId].Items.SetEquals(Stock.Select(row => row.Id)),
            "完整十SKU仅使用原NPC/原店、统一测试钟及真实在售集合");
        f.Events.Override(ServerEventService.EpicRoadId, true);
        var request = new BuyItemRequest(490013751, 1, ShopId, NpcId);
        That(!shop.Buy(f.Current, request, out _).Accepted &&
            !shop.Buy(f.Current, request, out _, requiredEvent:
                new(new(312, OverrideActive: true), f.Clock)).Accepted,
            "即便资源可用，裸service或312资格仍不能购买星河98");
        f.Sql("INSERT INTO item_purchase_counts VALUES('epic-shop',0,490013751,4,0,2)");
        f.Current = f.Repository.LoadCharacter(f.Owner, f.Current.Slot);
        foreach (var row in Stock)
        {
            var price = resources.Items.Get(row.Id).ShopPrice;
            That(price.Gold == row.Gold && (row.Material == 0 ? price.Materials.IsEmpty :
                price.Materials is [{ ItemId: var material, Count: var amount }] && material == row.Material && amount == row.Cost) &&
                (row.Period is null ? price.PeriodicLimit is null :
                    price.PeriodicLimit is { AccountWide: false } limit && limit.Period == row.Period && limit.Count == row.Limit),
                "原商品费用、角色五次限购与原周期：" + row.Id);
            int quantity = row.Limit > 0 ? row.Limit : 2;
            request = new(row.Id, quantity, ShopId, NpcId);
            var before = f.Current;
            var sibling = f.Repository.LoadCharacter(f.Owner, f.Sibling.Slot);
            var stale = Plan(sibling, request with { Count = 1 }, out _);
            var result = Plan(before, request, out var receipt);
            That(result.Accepted && receipt is not null && receipt.ItemId == row.Id && receipt.Count == quantity &&
                CountItem(result.Plan!.After, row.Id) - CountItem(before.Inventory, row.Id) == quantity &&
                before.Inventory.Gold - result.Plan.After.Gold == row.Gold * quantity &&
                (row.Material == 0 || Material(before.Inventory, row.Material, resources.Items) - Material(result.Plan.After, row.Material, resources.Items) == row.Cost * quantity),
                "完整数量、真实金币/材料与商品回执同一计划：" + row.Id);
            f.Current = InventoryService.Commit(f.Repository, f.Owner, before, result);
            if (row.Limit > 0)
            {
                That(stale.Accepted, "同账号另一角色在额度耗尽前可合法规划：" + row.Id);
                That(!Plan(f.Current, request with { Count = 1 }, out _).Accepted,
                    "本角色满五次后第六次拒绝：" + row.Id);
                if (row.Material == 490013716 && resources.Items.Get(row.Material).AccountCommonItem)
                    Expect<InvalidOperationException>(() => InventoryService.Commit(f.Repository, f.Owner, sibling, stale),
                        "另一角色旧共享材料快照不能覆盖已扣材料，与角色限购独立");
                sibling = f.Repository.LoadCharacter(f.Owner, sibling.Slot);
                var siblingResult = Plan(sibling, request, out _);
                That(siblingResult.Accepted, "同账号另一角色仍有独立五次额度：" + row.Id);
                sibling = InventoryService.Commit(f.Repository, f.Owner, sibling, siblingResult);
                That(!Plan(sibling, request with { Count = 1 }, out _).Accepted,
                    "另一角色也仅在自身满五次后拒绝：" + row.Id);
                f.Current = f.Repository.LoadCharacter(f.Owner, f.Current.Slot);
                var displayed = shop.PurchaseCounts(f.Current, NpcId, f.Clock.Now)!
                    .Where(count => count.ItemId == row.Id).ToArray();
                That(displayed is [{ AccountWide: false, Count: 5 }],
                    "商店回显仅本角色五次计数，不混入历史账号额度：" + row.Id);
            }
        }
        That(f.Current.Inventory.ShopPurchases.Any(row => row.ItemId == 490013751 && row.AccountWide && row.Count == 2),
            "旧账号累计兑换记录保留，不迁移、不删除、不占角色新额度");
        string persisted = f.Snapshot();
        var restored = new CharacterRepository(f.Path).LoadCharacter(f.Owner, f.Current.Slot);
        // 读库按商品排序，规划按原店顺序追加；先核完整计数，再消除顺序差异比较其余角色状态。
        That(ShopPurchaseCount.Same(f.Current.Inventory.ShopPurchases, restored.Inventory.ShopPurchases) &&
            QuestCompletionRepositoryTests.Same(f.Current with
            { Inventory = f.Current.Inventory with { ShopPurchases = restored.Inventory.ShopPurchases } }, restored),
            "十SKU资产、公共材料与角色及历史账号限购从新仓储恢复");
        request = new(490013727, 2, ShopId, NpcId);
        foreach (string change in new[] { "closed", "off-on", "rescheduled", "expired" })
        {
            f.Events.Schedule(ServerEventService.EpicRoadId, f.Clock.Now.AddSeconds(-1), f.Clock.Now.AddSeconds(60));
            var planned = Plan(f.Current, request, out _);
            That(planned.Accepted && planned.Plan?.RequiredEvent?.Schedule.EventId == ServerEventService.EpicRoadId,
                "真实727规划冻结10005资格：" + change);
            string before = f.Snapshot();
            if (change == "closed") f.Events.Override(ServerEventService.EpicRoadId, false);
            else if (change == "off-on") { f.Events.Override(ServerEventService.EpicRoadId, false); f.Events.Override(ServerEventService.EpicRoadId, true); }
            else if (change == "rescheduled") f.Events.Schedule(ServerEventService.EpicRoadId, f.Clock.Now.AddSeconds(-1), f.Clock.Now.AddSeconds(120));
            else f.Clock.Now = f.Clock.Now.AddSeconds(60);
            Expect<InventoryEventRejectedException>(() => InventoryService.Commit(f.Repository, f.Owner, f.Current, planned),
                "原资产事务拒绝规划后的活动变化：" + change);
            That(f.Snapshot() == before, "活动变化不扣716、不发727、不改revision及任何账号额度：" + change);
        }
        f.Events.Override(ServerEventService.EpicRoadId, true);
        That(f.Snapshot() == persisted, "所有晚期活动拒绝保持十SKU已提交资产");
        // 最后一份累计限购由另一个未购商品周期不能构造；推进真实729新服务日再测写入失败。
        f.Clock.Now = f.Clock.Now.AddDays(1);
        request = new(490013729, 5, ShopId, NpcId);
        var last = Plan(f.Current, request, out _);
        That(last.Accepted, "729新服务日恢复角色五次额度，测试晚期SQL失败");
        string rollback = f.Snapshot();
        f.Sql("CREATE TRIGGER epic_shop_abort_insert AFTER INSERT ON item_purchase_counts BEGIN SELECT RAISE(ABORT,'epic shop rollback'); END");
        f.Sql("CREATE TRIGGER epic_shop_abort_update AFTER UPDATE ON item_purchase_counts BEGIN SELECT RAISE(ABORT,'epic shop rollback'); END");
        Expect<SqliteException>(() => InventoryService.Commit(f.Repository, f.Owner, f.Current, last), "限购写入晚期故障回滚整笔购买");
        f.Sql("DROP TRIGGER epic_shop_abort_insert"); f.Sql("DROP TRIGGER epic_shop_abort_update");
        That(f.Snapshot() == rollback, "SQL回滚400金币、商品、限购与完整角色及账号状态");
        f.Current = InventoryService.Commit(f.Repository, f.Owner, f.Current, last);
        That(!Plan(f.Current, request with { Count = 1 }, out _).Accepted, "故障后原计划可重试仅提交一次，729满五次不可再买");
        f.Passed = true;
        Console.WriteLine($"PASS {Count - checks} 星河十SKU、角色五次限购、活动CAS与晚期事务回滚断言");

        InventoryResult Plan(Character character, BuyItemRequest buy, out ShopPurchaseReceipt? receipt) =>
            shop.Buy(character, buy, out receipt, requiredEvent:
                new(f.Events.Read(ServerEventService.EpicRoadId).Schedule, f.Clock));
    }

    static void RunSessionResources(ItemResources resources)
    {
        int checks = Count;
        var point = new Location(1, 0, 100, 100);
        var map = new TownMap("synthetic/epic-shop.map",
            [new(new(0, 0, 1000, 1000), [], [])], [], false, [new(NpcId, 100, 100)]);
        var area = new TownArea(1, 0, 1, [], [], [], point, map, [], []);
        var towns = new TownCatalog(new() { [(1, 0)] = area }, point, point, new(0, [], []), [],
            new Dictionary<uint, uint[]>());
        using var f = new Fixture(towns, resources);
        var shop = new InventoryShopService(resources.Items, resources.Shops, resources.GoldLimits,
            resources.EpicPieces, f.Clock, epicRoadAvailable: true);
        var disabled = new InventoryShopService(resources.Items, resources.Shops, resources.GoldLimits,
            resources.EpicPieces, f.Clock);
        var request = new BuyItemRequest(490013729, 1, ShopId, NpcId);
        var schedule = new ServerEventSchedule(ServerEventService.EpicRoadId, 1,
            f.Clock.Now.AddSeconds(-1).ToUnixTimeSeconds(), f.Clock.Now.AddSeconds(60).ToUnixTimeSeconds());
        foreach (var rejected in new[]
        {
            EpicRoad2019ShopRules.Prepare(request, resources.Shops, map, schedule, f.Clock, false),
            EpicRoad2019ShopRules.Prepare(request, resources.Shops, null, schedule, f.Clock, true),
            EpicRoad2019ShopRules.Prepare(request with { NpcId = 1 }, resources.Shops, map, schedule, f.Clock, true),
            EpicRoad2019ShopRules.Prepare(request with { ItemId = 3175 }, resources.Shops, map, schedule, f.Clock, true),
            EpicRoad2019ShopRules.Prepare(request, resources.Shops, map, null, f.Clock, true),
            EpicRoad2019ShopRules.Prepare(request, resources.Shops, map, schedule with { EventId = 312 }, f.Clock, true),
            EpicRoad2019ShopRules.Prepare(request, resources.Shops, map, schedule with { OverrideActive = false }, f.Clock, true),
            EpicRoad2019ShopRules.Prepare(request, resources.Shops, map,
                schedule with { StartsAt = f.Clock.Now.AddSeconds(1).ToUnixTimeSeconds() }, f.Clock, true),
            EpicRoad2019ShopRules.Prepare(request, resources.Shops, map,
                schedule with { EndsAt = f.Clock.Now.ToUnixTimeSeconds() }, f.Clock, true)
        }) That(rejected is null, "星河商店拒绝无资源、无真实商人、错目录、错活动及关闭/未来/到期");
        That(EpicRoad2019ShopRules.Prepare(request, resources.Shops, map, schedule, f.Clock, true) is not null,
            "合成地图仅用于服务端商人资格，匹配真实商品和有效活动时可捕获10005guard");
        var session = new GameSession(f.Repository, towns, f.Owner, timeProvider: f.Clock, serverEvents: f.Events);
        try
        {
            session.Select(f.Current.Slot); session.MarkReady();
            f.Events.Override(ServerEventService.EpicRoadId, true);
            string before = f.Snapshot();
            var unavailable = session.PlanAndCommitEpicRoad2019ShopBuy(request, disabled, resources.Shops, f.Events, out var missingReceipt);
            var wrong = session.PlanAndCommitEpicRoad2019ShopBuy(request with { NpcId = 1 }, shop, resources.Shops, f.Events, out var wrongReceipt);
            f.Events.Override(ServerEventService.EpicRoadId, false);
            var closed = session.PlanAndCommitEpicRoad2019ShopBuy(request, shop, resources.Shops, f.Events, out var closedReceipt);
            That(!unavailable.Accepted && !wrong.Accepted && !closed.Accepted && missingReceipt is null &&
                wrongReceipt is null && closedReceipt is null && f.Snapshot() == before,
                "专用Session拒绝缺资源、错误商人和关闭活动，不扣金币或占账号额度");
            f.Events.Override(ServerEventService.EpicRoadId, true);
            var prior = session.Selected!;
            var accepted = session.PlanAndCommitEpicRoad2019ShopBuy(request, shop, resources.Shops, f.Events, out var receipt);
            That(accepted.Accepted && receipt is { ItemId: 490013729, Count: 1 } &&
                accepted.Plan?.RequiredEvent?.Schedule.EventId == ServerEventService.EpicRoadId &&
                accepted.Receipt is not null && ReferenceEquals(accepted.Receipt.Character, session.Selected) &&
                session.Selected!.Inventory.Gold == prior.Inventory.Gold - 80 &&
                CountItem(session.Selected.Inventory, 490013729) == CountItem(prior.Inventory, 490013729) + 1,
                "专用Session在合成商人地图提交真实729购买及10005guard，安装同一角色回执；不代表原入口恢复");
            That(QuestCompletionRepositoryTests.Same(session.Selected!, new CharacterRepository(f.Path).LoadCharacter(f.Owner, prior.Slot)),
                "专用Session真实购买从独立Repository恢复");
        }
        finally { session.End(); }
        f.Passed = true;
        Console.WriteLine($"PASS {Count - checks} 星河合成商人Session成功与失败断言（非原活动入口）");
    }

    static int CountItem(InventorySnapshot inventory, int id) => inventory.Items.Where(row => row.Core.ItemId == id).Sum(row => row.Core.Count);
    static int Material(InventorySnapshot inventory, int id, ItemCatalog catalog) => catalog.Get(id).AccountMaterial is not null
        ? inventory.AccountWallet.Material(id).Balance : CountItem(inventory, id);
    static void Expect<T>(Action action, string name) where T : Exception
    {
        try { action(); } catch (T) { That(true, name); return; }
        throw new InvalidOperationException("FAIL 未遇到预期异常：" + name);
    }
    sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 3, 12, 0, 0, TimeSpan.FromHours(8));
        public override DateTimeOffset GetUtcNow() => Now;
    }
    sealed class Fixture : IDisposable
    {
        readonly string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "s5a9-epic-shop-" + Guid.NewGuid().ToString("N"));
        public string Path { get; }
        public CharacterRepository Repository { get; }
        public AccountOwners.Lease Owner { get; }
        public ServerEventService Events { get; }
        public Clock Clock { get; } = new();
        public Character Current { get; set; }
        public Character Sibling { get; private set; }
        public bool Passed { get; set; }
        public Fixture(TownCatalog towns, ItemResources resources, bool materials = false)
        {
            Path = System.IO.Path.Combine(directory, "game.db");
            Repository = new(Path);
            Owner = new AccountOwners().Acquire("epic-shop", () => { });
            Current = Repository.Create(Owner, new("EpicShopA", towns.Room, towns.InitialReturn)
                { Level = 95, Inventory = InventorySnapshot.Empty with { Gold = 100_000 } });
            Sibling = Repository.Create(Owner, new("EpicShopB", towns.Room, towns.InitialReturn)
                { Level = 95, Inventory = InventorySnapshot.Empty with { Gold = 100_000 } });
            if (materials)
            {
                var inventory = new InventoryService(resources.Items, resources.GoldLimits, resources.EpicPieces);
                var grant = inventory.Grant(Current, [new(490013716, 15000), new(10158964, 30)]);
                That(grant.Accepted, "商店夹具通过正式发奖规划准备真实账号材料和物品材料");
                Current = InventoryService.Commit(Repository, Owner, Current, grant);
                That(Material(Current.Inventory, 490013716, resources.Items) == 15000 && Material(Current.Inventory, 10158964, resources.Items) == 30,
                    "夹具真实材料足额，不能因余额不足误造活动拒绝RED");
                if (resources.Items.Get(490013716) is { AccountMaterial: null, AccountCommonItem: false })
                {
                    var siblingGrant = inventory.Grant(Sibling, [new(490013716, 15000)]);
                    That(siblingGrant.Accepted, "实体兑换材料给同账号另一角色独立足额准备，限购CAS不能因缺料误拒绝");
                    Sibling = InventoryService.Commit(Repository, Owner, Sibling, siblingGrant);
                }
            }
            Events = new(new ServerEventRepository(Path), Clock);
        }
        public string Snapshot() => JsonSerializer.Serialize(new
        {
            Characters = Repository.List(Owner),
            Wallet = Repository.LoadAccountWallet(Owner)
        });
        public void Sql(string sql)
        {
            using var db = new SqliteConnection("Data Source=" + Path + ";Pooling=False"); db.Open();
            using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
        }
        public void Dispose()
        {
            Owner.Dispose();
            if (Passed) TestFiles.DeleteDirectory(directory, true);
            else Console.WriteLine("FAIL fixture: " + directory);
        }
    }
}
