using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Events;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Strongway2019;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class Strongway2019ShopTests
{
    public static void RunResources(TownCatalog towns, ItemResources resources)
    {
        int before = Count;
        var clock = new Clock(DateTimeOffset.Parse("2026-11-04T05:59:59+08:00"));
        var schedule = new ServerEventSchedule(312, 1, clock.Now.AddDays(-1).ToUnixTimeSeconds(), clock.Now.AddDays(30).ToUnixTimeSeconds());
        var request = new BuyItemRequest(490013018, 1, 400000084, 400001051);
        var map = MerchantMap(request.NpcId);
        var required = Strongway2019ShopRules.Prepare(request, resources.Shops, map, schedule, clock);
        That(required is not null && required.Schedule == schedule && required.Matches(schedule),
            "首期真实商店在当前商人地图和312服务端有效时段内取得精确活动事务资格");
        That(Strongway2019ShopRules.Handles(400000084) && Strongway2019ShopRules.Handles(400000086) &&
            !Strongway2019ShopRules.Handles(274), "首期商店规则仅识别084和086，不改变普通商店");
        foreach (var rejected in new[]
        {
            Strongway2019ShopRules.Prepare(request, resources.Shops, null, schedule, clock),
            Strongway2019ShopRules.Prepare(request, resources.Shops, MerchantMap(1), schedule, clock),
            Strongway2019ShopRules.Prepare(request with { NpcId = 400001052 }, resources.Shops, MerchantMap(400001052), schedule, clock),
            Strongway2019ShopRules.Prepare(request with { ItemId = 10309616 }, resources.Shops, map, schedule, clock),
            Strongway2019ShopRules.Prepare(request, resources.Shops, map, null, clock),
            Strongway2019ShopRules.Prepare(request, resources.Shops, map, schedule with { EventId = 64 }, clock),
            Strongway2019ShopRules.Prepare(request, resources.Shops, map, schedule with { OverrideActive = false }, clock),
            Strongway2019ShopRules.Prepare(request, resources.Shops, map, schedule with { StartsAt = clock.Now.AddSeconds(1).ToUnixTimeSeconds() }, clock),
            Strongway2019ShopRules.Prepare(request, resources.Shops, map, schedule with { EndsAt = clock.Now.ToUnixTimeSeconds() }, clock)
        }) That(rejected is null, "非城镇、错误商人/商品、缺活动、错误活动、关闭及半开时段边界均不给购买资格");
        That(!required!.Matches(schedule with { Revision = schedule.Revision + 1 }),
            "相同时段但活动revision已变也不能重放旧商店规划");
        var rows = new (int Id, int Material, int Cost, ItemPurchasePeriod? Period, int Maximum)[]
        {
            (490013018,490012698,5,ItemPurchasePeriod.Weekly,1),
            (490013029,490012698,1,ItemPurchasePeriod.Weekly,2),
            (490013014,490012698,2,ItemPurchasePeriod.Weekly,2),
            (490013015,490012698,2,ItemPurchasePeriod.Weekly,2),
            (490013019,490012698,150,ItemPurchasePeriod.Accumulate,1),
            (490013020,490012698,150,ItemPurchasePeriod.Accumulate,1),
            (490013021,490012698,300,ItemPurchasePeriod.Accumulate,1),
            (490013022,490012699,2,ItemPurchasePeriod.Accumulate,3),
            (490013067,490012699,4,ItemPurchasePeriod.Accumulate,1),
            (490013024,490012699,5,ItemPurchasePeriod.Accumulate,1),
            (490013315,490012699,5,ItemPurchasePeriod.Accumulate,1),
            (490013316,490012699,5,ItemPurchasePeriod.Accumulate,1),
            (490013317,490012699,25,ItemPurchasePeriod.Accumulate,1),
            (490013027,490012699,4,null,0),
            (10309616,0,0,null,0), (10309617,0,0,null,0)
        };
        foreach (var row in rows)
        {
            var price = resources.Items.Get(row.Id).ShopPrice;
            That(price.Gold == 0 && price.Limit is null && price.UnresolvedPurchaseLimits.IsEmpty &&
                (row.Material == 0 ? price.Materials.IsEmpty : price.Materials is [{ ItemId: var material, Count: var cost }] && material == row.Material && cost == row.Cost) &&
                (row.Period is null ? price.PeriodicLimit is null : price.PeriodicLimit is { AccountWide: true } limit && limit.Period == row.Period && limit.Count == row.Maximum),
                "真实资源商品保留全部材料、数量和账号限购，不按宣传文字猜补：" + row.Id);
        }
        foreach (int shopId in new[] { 400000084, 400000086 })
        {
            var stock = resources.Shops.Shops[shopId];
            That(stock.Items.SetEquals(rows.Where(row => shopId == 400000086 || row.Material != 0).Select(row => row.Id)),
                "两店完整商品集合分别为14项和16项，不忽略任一资源SKU：" + shopId);
            using var fixture = new Fixture(towns, resources, clock, shopId);
            foreach (var row in rows.Where(row => stock.Items.Contains(row.Id)))
            {
                int quantity = row.Maximum > 0 ? row.Maximum : Math.Min(2, resources.Items.Get(row.Id).StackLimit);
                var buy = new BuyItemRequest(row.Id, quantity, shopId, stock.NpcId);
                var current = fixture.Current;
                var stale = fixture.Plan(fixture.Sibling, buy with { Count = 1 }, out _);
                var planned = fixture.Plan(current, buy, out var receipt);
                That(planned.Accepted && receipt is not null && receipt.ItemId == row.Id && receipt.Count == quantity &&
                    CountItem(planned.Plan!.After, row.Id) - CountItem(current.Inventory, row.Id) == quantity &&
                    (row.Material == 0 || CountItem(current.Inventory, row.Material) - CountItem(planned.Plan.After, row.Material) == row.Cost * quantity),
                    "每个在售SKU均通过真实shop整批扣足材料并规划完整物品：" + shopId + "/" + row.Id +
                    $" accepted={planned.Accepted} error={planned.Error} quantity={quantity} receipt={receipt?.Count} delta={(planned.Plan is { } diagnostic ? CountItem(diagnostic.After,row.Id)-CountItem(current.Inventory,row.Id) : -1)}");
                fixture.Current = InventoryService.Commit(fixture.Repository, fixture.Owner, current, planned);
                if (row.Maximum > 0)
                {
                    Reject<InvalidOperationException>(() => InventoryService.Commit(fixture.Repository, fixture.Owner, fixture.Sibling, stale),
                        "另一个角色的旧规划不能覆盖同账号已耗尽的商品额度");
                    fixture.Sibling = fixture.Repository.LoadCharacter(fixture.Owner, fixture.Sibling.Slot);
                    That(!fixture.Plan(fixture.Sibling, buy with { Count = 1 }, out _).Accepted,
                        "换角色不能绕过已落库的账号周期上限：" + row.Id);
                }
                if (row.Material == 0)
                    That(fixture.Current.Inventory.Items.Any(item => item.Core.ItemId == row.Id && item.ExpireTime > clock.Now.ToUnixTimeSeconds()),
                        "免费专用药保留真实每日删除期限：" + row.Id);
            }
            fixture.Current = new CharacterRepository(fixture.Path).LoadCharacter(fixture.Owner, fixture.Current.Slot);
            That(fixture.Current.Inventory.ShopPurchases.Any(row => row.ItemId == 490013022 && row.Count == 3 && row.Period == ItemPurchasePeriod.Accumulate),
                "商店累计限购在仓储重建后保持，不随重连重置");
            var restoredRows = new InventoryShopService(resources.Items, resources.Shops, resources.GoldLimits, resources.EpicPieces, clock)
                .PurchaseCounts(fixture.Current, stock.NpcId, clock.Now)!;
            var nativeCounts = DfoServer.Testing.Inventory.ShopPurchaseLimitTests.NativePurchaseCounts(
                DfoServer.Network.Builders.Inventory.ShopPackets.PurchaseCounts(restoredRows));
            That(nativeCounts.Purchases.GetValueOrDefault(490013019) == 1 && nativeCounts.Purchases.GetValueOrDefault(490013067) == 1 &&
                nativeCounts.Sales.Count == 0,
                "真实019/067重建仓储后查询恢复已购1/1，不能只在ACK21后临时正确或误写销售缓存");
        }
        PeriodAndTransaction(towns, resources);
        ProductionSession(towns, resources);
        RunFrameCompatibility(towns, resources);
        RunFrameTransferResources(towns, resources);
        RunDynamicEntryResources(towns, resources);
        Console.WriteLine($"PASS {Count - before} 首期两商店完整SKU、材料、账号限购、地点与活动事务断言");
    }

    static void PeriodAndTransaction(TownCatalog towns, ItemResources resources)
    {
        var clock = new Clock(DateTimeOffset.Parse("2026-11-04T05:59:59+08:00"));
        using var fixture = new Fixture(towns, resources, clock, 400000084);
        var request = new BuyItemRequest(490013018, 1, 400000084, 400001051);
        var planned = fixture.Plan(fixture.Current, request, out _);
        fixture.Current = InventoryService.Commit(fixture.Repository, fixture.Owner, fixture.Current, planned);
        clock.Now = clock.Now.AddSeconds(1);
        That(!fixture.Plan(fixture.Current, request, out _).Accepted,
            "奖励周三重置不使资源weekly商店限购提前重置");
        clock.Now = DateTimeOffset.Parse("2026-11-05T05:59:59+08:00");
        That(!fixture.Plan(fixture.Current, request, out _).Accepted, "商店周四06点前仍使用原周次数");
        clock.Now = clock.Now.AddSeconds(1);
        planned = fixture.Plan(fixture.Current, request, out _);
        That(planned.Accepted, "商店周四06点新周恢复真实周限购");
        string before = JsonSerializer.Serialize(fixture.Repository.List(fixture.Owner));
        fixture.Events.Override(312, false);
        Reject<InventoryEventRejectedException>(() => InventoryService.Commit(fixture.Repository, fixture.Owner, fixture.Current, planned),
            "规划后活动被关闭，扣料发货事务重新核对完整312时段并拒绝");
        That(JsonSerializer.Serialize(fixture.Repository.List(fixture.Owner)) == before,
            "活动版本变化不扣材料、不发物品、不占商品次数");
        fixture.Events.Override(312, true);
        planned = fixture.Plan(fixture.Current, request, out _);
        fixture.Sql("CREATE TRIGGER strongway_shop_abort AFTER UPDATE ON item_purchase_counts BEGIN SELECT RAISE(ABORT,'shop rollback'); END");
        Reject<SqliteException>(() => InventoryService.Commit(fixture.Repository, fixture.Owner, fixture.Current, planned),
            "商品额度写入晚期失败回滚完整购买");
        fixture.Sql("DROP TRIGGER strongway_shop_abort");
        That(JsonSerializer.Serialize(fixture.Repository.List(fixture.Owner)) == before,
            "失败购买回滚材料、完整附件、角色revision及限购账本");
    }

    static void ProductionSession(TownCatalog towns, ItemResources resources)
    {
        var clock = new Clock(DateTimeOffset.Parse("2026-11-05T06:00:00+08:00"));
        using var fixture = new Fixture(towns, resources, clock, 400000086);
        var area = towns.Areas.First(row => row.Value.Default.Npcs.Any(npc => npc.Id == 400001052));
        var npc = area.Value.Default.Npcs.First(npc => npc.Id == 400001052);
        fixture.Current = fixture.Repository.Save(fixture.Owner, fixture.Current,
            new(area.Key.Item1, area.Key.Item2, checked((ushort)npc.X), checked((ushort)npc.Y)), fixture.Current.Return);
        var session = new GameSession(fixture.Repository, towns, fixture.Owner, timeProvider: clock);
        session.Select(fixture.Current.Slot, resumeTown: true);
        session.MarkReady();
        var shop = new InventoryShopService(resources.Items, resources.Shops, resources.GoldLimits, resources.EpicPieces, clock);
        var request = new BuyItemRequest(490013018, 1, 400000086, 400001052);
        That(!shop.Buy(session.Selected!, request, out _).Accepted &&
            !session.PlanAndCommitShopBuy(request.ShopId, _ => throw new InvalidOperationException("不应调用旧商店规划")).Accepted,
            "首期商店不能通过缺少活动资格的裸service或旧通用Session入口绕过正式链");
        var result = session.PlanAndCommitStrongway2019ShopBuy(request, shop, resources.Shops, fixture.Events, out var receipt);
        That(result.Accepted && receipt is not null && result.Plan?.RequiredEvent?.Schedule.EventId == 312 &&
            result.Receipt is not null && ReferenceEquals(result.Receipt.Character, session.Selected),
            "实际地图中的回城商人经过正式Session购买提交并安装同一角色回执");
        string before = JsonSerializer.Serialize(fixture.Repository.List(fixture.Owner));
        var wrongMerchant = session.PlanAndCommitStrongway2019ShopBuy(
            request with { ShopId = 400000084, NpcId = 400001051 }, shop, resources.Shops, fixture.Events, out _);
        fixture.Events.Override(312, false);
        var closed = session.PlanAndCommitStrongway2019ShopBuy(
            request with { ItemId = 490013029 }, shop, resources.Shops, fixture.Events, out _);
        That(!wrongMerchant.Accepted && !closed.Accepted && JsonSerializer.Serialize(fixture.Repository.List(fixture.Owner)) == before,
            "正式Session拒绝不在当前地图的另一商人及关闭活动，不改变已提交库存");
    }

    public static void RunDynamicEntryResources(TownCatalog towns, ItemResources resources)
    {
        int beforeCount = Count;
        var clock = new Clock(DateTimeOffset.Parse("2026-11-05T06:00:00+08:00"));
        using var fixture = new Fixture(towns, resources, clock, 400000084);
        That(towns.Room is { Town: 38, Area: 1 } &&
            !towns.Areas[(38, 1)].Default.Npcs.Any(npc => npc.Id == 400001051),
            "真实38/1没有静态051，入口商人必须沿312动态呈现资格，不能靠合成地图通过");
        var price = resources.Items.Get(490013018).ShopPrice;
        That(price.Gold == 0 && price.Materials is [{ ItemId: 490012698, Count: 5 }],
            "动态入口测试使用真实商品490013018及五个强者的证明，不猜价或换免费商品");
        var session = new GameSession(fixture.Repository, towns, fixture.Owner, timeProvider: clock);
        session.Select(fixture.Current.Slot, resumeTown: true);
        session.MarkReady();
        var shop = new InventoryShopService(resources.Items, resources.Shops, resources.GoldLimits, resources.EpicPieces, clock);
        var request = new BuyItemRequest(490013018, 1, 400000084, 400001051);
        string before = JsonSerializer.Serialize(fixture.Repository.List(fixture.Owner));
        var wrong = session.PlanAndCommitStrongway2019ShopBuy(
            request with { ShopId = 400000086, NpcId = 400001052 }, shop, resources.Shops, fixture.Events, out _);
        fixture.Events.Override(312, false);
        var closed = session.PlanAndCommitStrongway2019ShopBuy(request, shop, resources.Shops, fixture.Events, out _);
        That(!wrong.Accepted && !closed.Accepted &&
            JsonSerializer.Serialize(fixture.Repository.List(fixture.Owner)) == before,
            "38/1不能冒返回店052/086，动态商人活动关闭也拒绝且资产不变");
        fixture.Events.Override(312, true);
        var initial = session.Selected!;
        int material = CountItem(initial.Inventory, 490012698), product = CountItem(initial.Inventory, 490013018);
        var result = session.PlanAndCommitStrongway2019ShopBuy(request, shop, resources.Shops, fixture.Events, out var receipt);
        var after = session.Selected!;
        That(result.Accepted,
            $"动态入口正式Session接受购买：accepted={result.Accepted}, error={result.Error}, reason={result.RejectReason ?? "<null>"}," +
            $" phase={session.Phase}, position={initial.Position}, material={material}, product={product}");
        That(receipt is { ItemId: 490013018, Count: 1 },
            $"动态入口购买保留商品回执：receipt={receipt}");
        That(result.Plan is not null && result.Plan.RequiredEvent?.Schedule.EventId == 312,
            $"动态入口购买保留312事务资格：plan={result.Plan is not null}, event={result.Plan?.RequiredEvent?.Schedule.EventId}");
        That(result.Receipt is not null && ReferenceEquals(result.Receipt.Character, session.Selected),
            $"动态入口安装同一角色提交回执：receipt={result.Receipt is not null}, same={ReferenceEquals(result.Receipt?.Character, session.Selected)}");
        That(CountItem(after.Inventory, 490012698) == material - 5,
            $"动态入口真实扣五材料：before={material}, after={CountItem(after.Inventory, 490012698)}");
        That(CountItem(after.Inventory, 490013018) == product + 1,
            $"动态入口真实发一商品：before={product}, after={CountItem(after.Inventory, 490013018)}");
        var restored = new CharacterRepository(fixture.Path).LoadCharacter(fixture.Owner, initial.Slot);
        That(CountItem(restored.Inventory, 490012698) == material - 5 &&
            CountItem(restored.Inventory, 490013018) == product + 1,
            "动态入口商店结果由新Repository恢复，不依赖本地显示或连接缓存");
        Console.WriteLine($"PASS {Count - beforeCount} 首期动态入口真实Session商店断言");
    }


    public static void RunFrameCompatibility(TownCatalog towns, ItemResources resources)
    {
        int beforeCount = Count;
        var clock = new Clock(DateTimeOffset.Parse("2026-11-05T06:00:00+08:00"));
        using var fixture = new Fixture(towns, resources, clock, 400000086);
        var area = towns.Areas.First(row => row.Value.Default.Npcs.Any(npc => npc.Id == 400001052));
        var npc = area.Value.Default.Npcs.First(npc => npc.Id == 400001052);
        fixture.Current = fixture.Repository.Save(fixture.Owner, fixture.Current,
            new(area.Key.Item1, area.Key.Item2, checked((ushort)npc.X), checked((ushort)npc.Y)), fixture.Current.Return);
        var inventory = new InventoryService(resources.Items, resources.GoldLimits, resources.EpicPieces);
        var shop = new InventoryShopService(resources.Items, resources.Shops, resources.GoldLimits, resources.EpicPieces, clock);
        var booster = new InventoryBoosterService(resources.Items, resources.Boosters, resources.GoldLimits,
            resources.EpicPieces, contracts: resources.Contracts, avatars: resources.AvatarPackages, clock: clock);
        var skins = new InventorySkinStorageService(resources.Items, resources.Skins, resources.GoldLimits,
            resources.EpicPieces, resources.Contracts, clock);
        var session = Reconnect();
        var request = new BuyItemRequest(490013316, 1, 400000086, 400001052);
        var initial = session.Selected!;
        That(session.PlanAndCommitStrongway2019ShopBuy(request, shop, resources.Shops, fixture.Events, out _).Accepted &&
            CountItem(session.Selected!.Inventory, 490012699) == CountItem(initial.Inventory, 490012699) - 5,
            "本活动真实052/086兑换316扣五枚699，不改资源售价");
        var box = session.Selected!.Inventory.Items.Single(item => item.Core.ItemId == 490013316);
        session = Reconnect();
        That(session.Selected!.Inventory.Items.Single(item => item.Uid == box.Uid).Core == box.Core,
            "新316在新Repository和Session选角后保留完整来源核心");
        string beforeOpen = JsonSerializer.Serialize(fixture.Repository.List(fixture.Owner));
        var beforeOpenSession = session.Selected!;
        fixture.Sql("CREATE TRIGGER strongway_frame_open_abort AFTER INSERT ON inventory_audit_log " +
            "WHEN NEW.action_name='booster_open' BEGIN SELECT RAISE(ABORT,'frame open rollback'); END");
        Reject<SqliteException>(() => Open(box), "新来源开盒后段审计失败必须传播");
        fixture.Sql("DROP TRIGGER strongway_frame_open_abort");
        That(ReferenceEquals(session.Selected, beforeOpenSession) && JsonSerializer.Serialize(fixture.Repository.List(fixture.Owner)) == beforeOpen,
            "开盒SQL失败保留源316、来源、资产、revision及在线快照");
        That(Open(box).Accepted, "新兑换316沿原真实Booster奖励生成318");
        var coupon = session.Selected!.Inventory.Items.Single(item => item.Core.ItemId == 490013318);
        session = Reconnect();
        That(session.Selected!.Inventory.Items.Single(item => item.Uid == coupon.Uid).Core == coupon.Core,
            "新318在新Repository和Session选角后保留完整来源核心");
        string beforeUse = JsonSerializer.Serialize(fixture.Repository.List(fixture.Owner));
        var beforeUseSession = session.Selected!;
        // 首个行为RED：原资源2020动作已过期；只改新来源后必须真正可登记，不以字段存在冒充生效。
        That(skins.Add(session.Selected!, coupon.Position, town: true).Accepted,
            "仅本活动新兑换316的真实318在当前时钟可以登记永久角色边框");
        fixture.Sql("CREATE TRIGGER strongway_frame_skin_abort AFTER UPDATE ON characters " +
            "BEGIN SELECT RAISE(ABORT,'frame skin rollback'); END");
        Reject<SqliteException>(() => session.UseSkinStorage(skins, coupon.Position), "新边框登记后段角色写入失败必须传播");
        fixture.Sql("DROP TRIGGER strongway_frame_skin_abort");
        That(ReferenceEquals(session.Selected, beforeUseSession) && JsonSerializer.Serialize(fixture.Repository.List(fixture.Owner)) == beforeUse,
            "登记SQL失败完整回滚券、角色边框、revision及在线快照");
        That(session.UseSkinStorage(skins, coupon.Position).Accepted && session.Selected!.Inventory.Find(coupon.Position) is null &&
            session.Selected!.Inventory.Skins.PartyFrames is [{ SkinId: 420000004, ExpiresAt: 0 }] &&
            session.Selected!.Inventory.AccountSkins.PartyFrames.IsEmpty,
            "新318一次消费精确UID，仅登记角色420000004且永久");
        session = Reconnect();
        That(session.Selected!.Inventory.Skins.PartyFrames is [{ SkinId: 420000004, ExpiresAt: 0 }] &&
            fixture.Repository.LoadCharacter(fixture.Owner, fixture.Sibling.Slot).Inventory.Skins.PartyFrames.IsEmpty,
            "永久角色边框重连保留，同账号兄弟角色不获得所有权");
        That(!session.PlanAndCommitStrongway2019ShopBuy(request, shop, resources.Shops, fixture.Events, out _).Accepted,
            "新增来源不绕过原316账号累计限购");

        That(resources.Items.Get(490013316).AttachType.Contains("[account]", StringComparison.Ordinal) &&
            resources.Items.Get(490013318).AttachType.Contains("[trade]", StringComparison.Ordinal),
            "真实316账号绑定、318不可交易，不按trade字样误放拍卖");
        foreach (var item in new[] { box, coupon })
        {
            var template = resources.Items.Get(item.Core.ItemId);
            That(DfoServer.Game.Auction.AuctionService.TransferBlocked(item.Core, template) is not null &&
                DfoServer.Game.Mailbox.MailboxService.TransferError(item, template, false, clock.Now.ToUnixTimeSeconds()) !=
                    DfoServer.Game.Mailbox.MailboxError.None,
                "带新来源的真实316/318仍拒绝跨账号邮件及拍卖");
        }
        That(DfoServer.Game.Mailbox.MailboxService.TransferError(box, resources.Items.Get(490013316), true,
                clock.Now.ToUnixTimeSeconds()) == DfoServer.Game.Mailbox.MailboxError.None &&
            DfoServer.Game.Mailbox.MailboxService.TransferError(coupon, resources.Items.Get(490013318), true,
                clock.Now.ToUnixTimeSeconds()) != DfoServer.Game.Mailbox.MailboxError.None,
            "原账号内邮件只允许316，318仍按原规则拒绝");
        That(JsonSerializer.Serialize(box.Core).Contains("\"Strongway2019FrameOrigin\":{\"Version\":1}", StringComparison.Ordinal) &&
            JsonSerializer.Serialize(coupon.Core).Contains("\"Strongway2019FrameOrigin\":{\"Version\":1}", StringComparison.Ordinal),
            "服务端窄来源从实际新316传递到实际新318并持久化");

        var baseline = session.Selected!;
        var withCoupon = baseline with { Inventory = baseline.Inventory with { Items = baseline.Inventory.Items.Add(coupon) } };
        That(!skins.Add(withCoupon, coupon.Position, town: true).Accepted,
            "已有永久边框再用同类新券按原重复规则拒绝");
        var expired = coupon with { Core = coupon.Core with { ExpireTime = checked((int)clock.Now.ToUnixTimeSeconds()) } };
        var emptySkins = baseline.Inventory.Skins with { PartyFrames = [] };
        var expiredCharacter = baseline with { Inventory = baseline.Inventory with { Skins = emptySkins, Items = baseline.Inventory.Items.Add(expired) } };
        That(!skins.Add(expiredCharacter, expired.Position, town: true).Accepted,
            "新券动作永久仍不能绕过券实例自身等号到期");
        var future = coupon with { Core = coupon.Core with { ExpireTime = checked((int)clock.Now.ToUnixTimeSeconds() + 1) } };
        var futureCharacter = expiredCharacter with { Inventory = expiredCharacter.Inventory with { Items = baseline.Inventory.Items.Add(future) } };
        var futurePlan = skins.Add(futureCharacter, future.Position, town: true);
        That(futurePlan.Accepted && futurePlan.Plan!.CharacterPrecondition?.QualificationExpiresAt == future.ExpireTime,
            "新券仍将自身到期时间附于原提交前提");
        That(!skins.Add(futureCharacter, future.Position, town: false).Accepted,
            "本次永久规则保留真实village场景限制");

        That(session.PlanAndCommitInventory(character => inventory.Grant(character, [new(490013316, 1), new(490013318, 1)])).Accepted,
            "普通Grant同ID保持无活动来源");
        var oldBox = session.Selected!.Inventory.Items.Single(item => item.Core.ItemId == 490013316);
        var oldCoupon = session.Selected!.Inventory.Items.Single(item => item.Core.ItemId == 490013318);
        That(!JsonSerializer.Serialize(oldBox.Core).Contains("Strongway2019FrameOrigin", StringComparison.Ordinal) &&
            !JsonSerializer.Serialize(oldCoupon.Core).Contains("Strongway2019FrameOrigin", StringComparison.Ordinal) &&
            !InventoryStackRuleService.CanShareStack(box.Core, oldBox.Core),
            "普通Grant不补来源且完整Core比较隔离新旧316");
        var oldCharacter = session.Selected! with { Inventory = session.Selected!.Inventory with { Skins = emptySkins } };
        That(!skins.Add(oldCharacter, oldCoupon.Position, town: true).Accepted,
            "普通Grant318在当前活动开启时仍按2020动作期限拒绝");
        That(session.PlanAndCommitInventory(character => InventoryDeleteService.Plan(character.Inventory,
                oldCoupon.Position, oldCoupon.Core.ItemId, 1)).Accepted &&
            session.Selected!.Inventory.Find(oldCoupon.Position) is null,
            "经正常删除规划分离旧券拒绝与旧盒产物场景，不让已满旧318堆阻断开盒");
        clock.Now = clock.Now.AddMilliseconds(Math.Max(1, resources.Boosters.Get(490013316).CoolTime));
        That(Open(oldBox).Accepted, "旧来源316仍可按原资源正常开盒");
        var oldRewards = session.Selected!.Inventory.Items.Where(item => item.Core.ItemId == 490013318).ToArray();
        That(oldRewards.Length == 1 && oldRewards.All(item =>
                !JsonSerializer.Serialize(item.Core).Contains("Strongway2019FrameOrigin", StringComparison.Ordinal)),
            "旧316当前开盒产生的318仍无来源，不按开盒时间自动永久");
        foreach (var item in oldRewards)
            That(!skins.Add(session.Selected! with { Inventory = session.Selected!.Inventory with { Skins = emptySkins } },
                item.Position, town: true).Accepted, "旧318保持固定2020动作日期");
        using var entryFixture = new Fixture(towns, resources, clock, 400000084);
        var entryRequest = request with { ShopId = 400000084, NpcId = 400001051 };
        var entryPlan = entryFixture.Plan(entryFixture.Current, entryRequest, out _);
        That(entryPlan.Accepted && entryPlan.Plan!.After.Items.Any(item => item.Core.ItemId == 490013316 &&
                JsonSerializer.Serialize(item.Core).Contains("Strongway2019FrameOrigin", StringComparison.Ordinal)),
            "真实084也在售316，同一312活动新兑换沿相同来源规则");
        Console.WriteLine($"PASS {Count - beforeCount} 本活动新兑换316到318永久角色边框、旧实例与回滚断言");

        GameSession Reconnect()
        {
            var connected = new GameSession(new CharacterRepository(fixture.Path), towns, fixture.Owner, timeProvider: clock);
            connected.Select(fixture.Current.Slot, resumeTown: true); connected.MarkReady();
            return connected;
        }
        InventoryResult Open(InventoryItem source) => session.PlanAndCommitBooster(character => booster.Open(character,
            new(source.Position.Index, 1), out _), booster.UsePlaces(490013316), 490013316);
    }


    public static void RunFrameTransferResources(TownCatalog towns, ItemResources resources)
    {
        int beforeCount = Count;
        var clock = new Clock(DateTimeOffset.Parse("2026-11-05T06:00:00+08:00"));
        using var fixture = new Fixture(towns, resources, clock, 400000086);
        var inventory = new InventoryService(resources.Items, resources.GoldLimits, resources.EpicPieces);
        fixture.Current = InventoryService.Commit(fixture.Repository, fixture.Owner, fixture.Current,
            inventory.Grant(fixture.Current, [new(0, 1_000_000), new(490013316, 1)]));
        var oldBox = fixture.Current.Inventory.Items.Single(item => item.Core.ItemId == 490013316);
        fixture.Current = InventoryService.Commit(fixture.Repository, fixture.Owner, fixture.Current,
            fixture.Plan(fixture.Current, new(490013316, 1, 400000086, 400001052), out _));
        var box = fixture.Current.Inventory.Items.Single(item => item.Core.ItemId == 490013316 && item.Uid != oldBox.Uid);
        var ordinary = InventoryRewardGrantService.Create(resources.Items.Get(490013316), utcNow: clock.Now);
        var frameBooster = new InventoryBoosterService(resources.Items, resources.Boosters, resources.GoldLimits,
            resources.EpicPieces, contracts: resources.Contracts, avatars: resources.AvatarPackages, clock: clock);
        var preview = frameBooster.Open(fixture.Current, new(box.Position.Index, 1), out _);
        That(preview.Accepted, "真实新316单次开盒可规划318");
        var existingFrame = preview.Plan!.After.Items.Single(item => item.Core.ItemId == 490013318) with
            { Position = new(InventoryListType.Main, 119) };
        var withExisting = fixture.Current with { Inventory = fixture.Current.Inventory with
            { Items = fixture.Current.Inventory.Items.Add(existingFrame) } };
        var besideExisting = frameBooster.Open(withExisting, new(box.Position.Index, 1), out _);
        That(besideExisting.Accepted && besideExisting.Plan!.After.Items.Count(item => item.Core.ItemId == 490013318) == 2 &&
            besideExisting.Plan.After.Find(existingFrame.Position) == existingFrame &&
            besideExisting.Plan.After.Items.Where(item => item.Core.ItemId == 490013318).All(item => item.Core.Count == 1 &&
                JsonSerializer.Serialize(item.Core).Contains("Strongway2019FrameOrigin", StringComparison.Ordinal)),
            "已有同来源满堆318时再开316仍独占空槽，原318精确实例不变");
        if (resources.Items.Get(490013316).StackLimit > 1)
        {
            var batchBox = box with { Core = box.Core with { Value = 2 } };
            var batchCharacter = fixture.Current with { Inventory = fixture.Current.Inventory with { Items = [batchBox] } };
            var batchBooster = new InventoryBoosterService(resources.Items, resources.Boosters, resources.GoldLimits,
                resources.EpicPieces, contracts: resources.Contracts, avatars: resources.AvatarPackages, clock: clock);
            var batch = batchBooster.Open(batchCharacter, new(batchBox.Position.Index, 2), out var batchReceipt);
            That(batch.Accepted && batchReceipt is not null && batch.Plan!.After.Items.Length == 2 &&
                batch.Plan.After.Items.All(item => item.Core.ItemId == 490013318 && item.Core.Count == 1 &&
                    JsonSerializer.Serialize(item.Core).Contains("Strongway2019FrameOrigin", StringComparison.Ordinal)),
                "模板允许堆叠的同来源316批量开两份仍逐张传递318来源，不丢批量奖励");
            var range = ItemSlotBoundService.Range(ItemKind.Consumable, fixture.Current.Inventory.Expansion);
            var fullItems = Enumerable.Range(range.Start, range.End - range.Start + 1)
                .Select(index => new InventorySlot(InventoryListType.Main, checked((short)index)))
                .Select(position => position == batchBox.Position ? batchBox :
                    new InventoryItem(0, position, existingFrame.Core)).ToImmutableArray();
            var fullCharacter = batchCharacter with { Inventory = batchCharacter.Inventory with { Items = fullItems } };
            That(!batchBooster.Open(fullCharacter, new(batchBox.Position.Index, 2), out _).Accepted &&
                fullCharacter.Inventory.Find(batchBox.Position) == batchBox,
                "满消耗页开两份只空一格时整拒，源堆和来源保持，不溢出邮件");
        }
        That(!InventoryStackRuleService.CanShareStack(box.Core, oldBox.Core) && box.Core.Count == 1 && oldBox.Core.Count == 1,
            "旧316与新兑换316同时存在仍保留两个来源独立实体");
        That(CoreBytes(box.Core).SequenceEqual(CoreBytes(ordinary)),
            "新增服务端来源不改变真实物品核心123B及任何未知wire位");
        box.Core.Validate();
        string coreJson = JsonSerializer.Serialize(box.Core);
        Reject<InvalidDataException>(() => JsonSerializer.Deserialize<ItemCore>(coreJson.Replace("\"Version\":1", "\"Version\":2", StringComparison.Ordinal))!.Validate(),
            "新来源未知版本不能通过核心校验");
        Reject<InvalidDataException>(() => (box.Core with { ItemId = 490013317 }).Validate(),
            "新来源不能挪给相邻商品");
        Reject<InvalidDataException>(() => (box.Core with { Kind = ItemKind.Equipment }).Validate(),
            "新来源不能挪给装备或复用Epic来源");
        Move(box, new(InventoryListType.Main, 120));
        box = fixture.Current.Inventory.Items.Single(item => item.Uid == box.Uid);
        var cargo = new InventoryCargoService(resources.Items, resources.Cargo, resources.GoldLimits, clock);
        fixture.Current = InventoryService.Commit(fixture.Repository, fixture.Owner, fixture.Current,
            cargo.AccountUpgrade(fixture.Current, create: true));
        That(fixture.Current.Inventory.AccountCargo.Capacity > 0, "沿真实账号仓开通规则准备合法容量");
        Move(box, new(InventoryListType.AccountCargo, 0));
        fixture.Current = new CharacterRepository(fixture.Path).LoadCharacter(fixture.Owner, fixture.Current.Slot);
        var inCargo = fixture.Current.Inventory.Items.Single(item => item.Uid == box.Uid);
        That(inCargo.Position == new InventorySlot(InventoryListType.AccountCargo, 0) && inCargo.Core == box.Core,
            "新316账号仓存入后由新Repository完整恢复同UID及来源");
        Move(inCargo, box.Position);
        box = fixture.Current.Inventory.Items.Single(item => item.Uid == box.Uid);
        var mailService = new DfoServer.Game.Mailbox.MailboxService(fixture.Repository, resources.Items, resources.GoldLimits, resources.EpicPieces);
        var mailRequest = new DfoServer.Game.Mailbox.MailboxSendRequest(fixture.Sibling.Name, 0,
            [new(0, box.Position.Index, box.Core.ItemId, 1)], "frame provenance");
        That(mailService.PlanSend(fixture.Current, fixture.Owner.Account, mailRequest, clock.Now, out var sendPlan) ==
            DfoServer.Game.Mailbox.MailboxError.None, "真实账号绑定316允许同账号兄弟角色寄件");
        var sent = mailService.Send(fixture.Owner, fixture.Current, sendPlan!);
        That(sent.Accepted, "316同账号邮件与精确源扣除同事务提交");
        fixture.Current = sent.Character!;
        var restoredMail = new DfoServer.Game.Mailbox.MailboxService(new CharacterRepository(fixture.Path), resources.Items,
            resources.GoldLimits, resources.EpicPieces);
        var mail = restoredMail.Inspect(fixture.Owner, fixture.Sibling.Id, clock.Now).Entries.Single(row => row.Id == sent.MailId);
        That(mail.Attachments is [{ Item: var attachment }] && attachment.Uid == box.Uid && attachment.Core == box.Core,
            "真实邮件JSON重读保留新316完整来源，不依赖客户端123B或拍卖快照");
        fixture.Sibling = fixture.Repository.LoadCharacter(fixture.Owner, fixture.Sibling.Slot);
        That(restoredMail.PlanClaim(fixture.Owner, fixture.Sibling, [mail.Attachments[0].ClaimId], clock.Now, out var claimPlan) ==
            DfoServer.Game.Mailbox.MailboxError.None, "316同账号真实附件可以领取");
        var claimed = restoredMail.Claim(fixture.Owner, fixture.Sibling, claimPlan!);
        That(claimed.Accepted, "同账号316领取正常提交");
        fixture.Sibling = new CharacterRepository(fixture.Path).LoadCharacter(fixture.Owner, fixture.Sibling.Slot);
        var received = fixture.Sibling.Inventory.Items.Single(item => item.Uid == box.Uid);
        That(received.Core == box.Core, "邮件领取后再次重读仍保留同UID及窄来源");
        var booster = new InventoryBoosterService(resources.Items, resources.Boosters, resources.GoldLimits,
            resources.EpicPieces, contracts: resources.Contracts, avatars: resources.AvatarPackages, clock: clock);
        fixture.Sibling = InventoryService.Commit(fixture.Repository, fixture.Owner, fixture.Sibling,
            booster.Open(fixture.Sibling, new(received.Position.Index, 1), out _));
        var coupon = fixture.Sibling.Inventory.Items.Single(item => item.Core.ItemId == 490013318);
        That(!InventoryCargoService.AllowsInsert(coupon.Core, InventoryListType.AccountCargo, resources.Items.Get(490013318)),
            "实际318的trade绑定继续拒绝账号仓，不能为保存来源放宽交易");
        var skins = new InventorySkinStorageService(resources.Items, resources.Skins, resources.GoldLimits, resources.EpicPieces,
            resources.Contracts, clock);
        fixture.Sibling = InventoryService.Commit(fixture.Repository, fixture.Owner, fixture.Sibling,
            skins.Add(fixture.Sibling, coupon.Position, town: true));
        That(fixture.Sibling.Inventory.Skins.PartyFrames is [{ SkinId: 420000004, ExpiresAt: 0 }] &&
            new CharacterRepository(fixture.Path).LoadCharacter(fixture.Owner, fixture.Current.Slot).Inventory.Skins.PartyFrames.IsEmpty,
            "合法移动和同账号邮件后的新来源318可入永久角色边框，归收件角色所有");
        Console.WriteLine($"PASS {Count - beforeCount} 新316核心校验、wire不变、账号仓与邮件来源持久传递断言");

        void Move(InventoryItem source, InventorySlot destination)
        {
            var planned = InventoryMoveService.Plan(fixture.Current, new(source.Position, source.Core.ItemId, 1,
                destination, 0, 0, -1, 0, 0), resources.Items, clock.Now.ToUnixTimeSeconds(), resources.Contracts);
            That(planned.Accepted, "新316按原合法容器移动规划");
            fixture.Current = InventoryService.Commit(fixture.Repository, fixture.Owner, fixture.Current, planned);
        }
        static byte[] CoreBytes(ItemCore core)
        {
            using var buffer = new MemoryStream();
            using var writer = new BinaryWriter(buffer);
            DfoServer.Network.Builders.Inventory.ItemListProtocolWriter.Write(writer, 65, core);
            return buffer.ToArray();
        }
    }

    static TownMap MerchantMap(int npc) => new("fixture/merchant.map", [], [], false, [new(checked((uint)npc), 0, 0)]);
    static int CountItem(InventorySnapshot inventory, int id) => inventory.Items.Where(item => item.Core.ItemId == id).Sum(item => item.Core.Count);

    sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    sealed class Fixture : IDisposable
    {
        readonly string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "s5a9-strongway-shop-" + Guid.NewGuid().ToString("N"));
        readonly ItemResources resources;
        readonly Clock clock;
        readonly InventoryShopService shop;
        public string Path { get; }
        public CharacterRepository Repository { get; }
        public AccountOwners.Lease Owner { get; }
        public ServerEventService Events { get; }
        public Character Current { get; set; }
        public Character Sibling { get; set; }

        public Fixture(TownCatalog towns, ItemResources resources, Clock clock, int shopId)
        {
            this.resources = resources; this.clock = clock;
            Directory.CreateDirectory(directory);
            Path = System.IO.Path.Combine(directory, "game.db");
            Repository = new(Path);
            Owner = new AccountOwners().Acquire("strongway-shop-" + shopId, () => { });
            Events = new(new ServerEventRepository(Path), clock);
            Events.Schedule(312, clock.Now.AddDays(-1), clock.Now.AddDays(30));
            Current = Repository.Create(Owner, new("SWShopA", towns.Room, towns.InitialReturn) { Level = 95 });
            Sibling = Repository.Create(Owner, new("SWShopB", towns.Room, towns.InitialReturn) { Level = 95 });
            var inventory = new InventoryService(resources.Items, resources.GoldLimits, resources.EpicPieces);
            Current = InventoryService.Commit(Repository, Owner, Current, inventory.Grant(Current, [new(490012698, 2000), new(490012699, 2000)]));
            Sibling = InventoryService.Commit(Repository, Owner, Sibling, inventory.Grant(Sibling, [new(490012698, 2000), new(490012699, 2000)]));
            Current = Repository.LoadCharacter(Owner, Current.Slot);
            shop = new(resources.Items, resources.Shops, resources.GoldLimits, resources.EpicPieces, clock);
        }

        public InventoryResult Plan(Character character, BuyItemRequest request, out ShopPurchaseReceipt? receipt)
        {
            receipt = null;
            var required = Strongway2019ShopRules.Prepare(request, resources.Shops, MerchantMap(request.NpcId), Events.Read(312).Schedule, clock);
            if (required is null) return InventoryResult.Reject(17);
            return shop.Buy(character, request, out receipt, clock.Now, required);
        }

        public void Sql(string sql)
        {
            using var db = new SqliteConnection("Data Source=" + Path + ";Pooling=False"); db.Open();
            using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
        }

        public void Dispose() { Owner.Dispose(); TestFiles.DeleteDirectory(directory, true); }
    }
}
