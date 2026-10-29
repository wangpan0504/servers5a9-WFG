using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Events;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.Network.Builders.Inventory;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class MileageShopTests
{
    public static void RunCore()
    {
        int checks = Count;
        const string normal = "[item]\n1001 101 1 0 0 100 `1个` 0 0\n[/item]\n" +
            "[specific product mileage]\n1001 7 1002 0\n[/specific product mileage]\n" +
            "[mileage rate by category]\n`item` 3 `nametag` 3\n[/mileage rate by category]\n[buy only cera]\n101\n[/buy only cera]";
        const string text = "[item]\n2001 101 2 0 5 0 `2个` 0 0\n2002 101 1 0 0 0 `1个` 0 0\n2003 101 1 0 10 0 `1个` 0 0\n[/item]\n" +
            "[purchasing limit]\n2001 2 0 0 0 0\n2003 1 1 0 0 0\n[/purchasing limit]\n[limit mileage by cash]\n20\n[/limit mileage by cash]";
        var shop = CashShopCatalog.Parse(normal, mileageText: text);
        That(CashShopCatalog.Parse("[item]\n3001 101 1 800000 0 0 `1个` 0 0\n[/item]").Product(3001)
            is { GoldPrice: 800000, CeraPrice: 0 }, "普通商城金币价非零、点券价为零的商品不当作免费");
        var mileage = shop.Mileage;
        That(mileage.Available && mileage.Products[2001] is { Count: 2, MileagePrice: 5, CeraPrice: 0 }, "积分价不落入普通点券价格列");
        var product = shop.Product(1001)!;
        That(mileage.Rebate(product, 100) == 7 && mileage.Rebate(product, 50) == 7 && mileage.Rebate(product, 0) == 0,
            "指定商品返积分覆盖分类率，不按支付分摊拆小固定值，免费兑换不返分");
        That(mileage.Rebate(product with { CommodityId = 1002 }, 100) == 0 &&
            mileage.Rebate(product with { CommodityId = 1003 }, 100) == 3 &&
            mileage.Rebate(mileage.Products[2001], 100) == 0, "明确零返分不回退分类，积分购买不循环赚积分");
        That(mileage.Rebate(product with { CommodityId = 1003 }, 50) == 2 &&
            mileage.Rebate(product with { CommodityId = 1003, Section = "visual" }, 50) == 2,
            "分类返积分沿原生float32加0.5取整，名称装饰映射nametag分类");
        var catalog = new ItemCatalog(null!, new Dictionary<int, string>(), new Dictionary<int, string>(), 0);
        catalog.PrefillTemplates(new Dictionary<int, ItemTemplate>
        {
            [101] = new(101, ItemKind.Consumable, 0, 1, 0, 1000, 0, 0, 1, "[free]", ushort.MaxValue, 0, "积分样品")
        });
        var clock = new ConsumableUseSessionTests.FakeClock();
        var eventStore = new EventStore();
        var events = new ServerEventService(eventStore, clock);
        var service = new InventoryCashShopService(catalog, shop,
            new GoldLimitCatalog("[gold limit from level]\n0 400000000\n[/gold limit from level]"),
            new EpicPieceCatalog("[piece list]\n9999 9998\n[/piece list]"), null!, events: events, clock: clock);
        var role = new Character(1, 0, "MileageCore", 0, new(0, 0, 0, 0), null)
        {
            Level = 1, Inventory = InventorySnapshot.Empty with { AccountWallet = AccountWallet.Empty with { Mileage = 20 } }
        };
        CashShopPurchaseRequest Request(int id = 2001, byte mode = 0) => new(0, [new(id, 255, 255)]) { CurrencyMode = mode };
        That(!service.Buy(role, Request(), out _).Accepted, "活动266未配置关闭，不因积分足够而放行");
        events.Override(266, true);
        That(service.Buy(role, Request(mode: 1), out _) is { Accepted: true, Plan.After.AccountWallet.Mileage: 15 } &&
            service.Buy(role, Request(1001, 1), out _) is { Accepted: false, RejectReason: "mileage_source_product_mismatch" },
            "原生积分来源1可正常扣积分，但不能将普通商品请求改作积分付款");
        var first = service.Buy(role, Request(mode: 2), out var receipt);
        That(first.Accepted && receipt!.Products.Single().IsMileage && first.Plan!.RequiredEvent is not null &&
            first.Plan.After.AccountWallet.Mileage == 15 && first.Plan.After.AccountWallet.MileageShop.Purchases[2001] == 1 &&
            first.Plan.After.Items.Single().Core.Count == 2, "积分、一次商品份数和整包产物同一计划，购物车形状不改变币种");
        role = role with { Inventory = first.Plan!.After };
        var second = service.Buy(role, Request(), out _);
        That(second.Accepted && second.Plan!.After.Items.Single().Core.Count == 4 && second.Plan.After.AccountWallet.Mileage == 10,
            "第二次购买按商品份数计限购，不把包内数量当购买次数");
        role = role with { Inventory = second.Plan!.After };
        That(!service.Buy(role, Request(), out _).Accepted && !service.Buy(role, Request(2003), out _).Accepted,
            "达到限购及尚未确认的非零周期参数均不扣积分");
        That(!service.Buy(role, new(0, [new(2002, 0), new(1001, 0)]), out _).Accepted &&
            !service.Buy(role, Request(2002, 4), out _).Accepted, "不跨目录混单，不把未知首字节当积分支付开关");
        var free = service.Buy(role, Request(2002), out _);
        That(free.Accepted && free.Plan!.After.AccountWallet.Mileage == role.Inventory.AccountWallet.Mileage,
            "资源明确的零积分商品保留正常发奖，不额外扣点券");
        var stale = first.Plan.RequiredEvent!;
        events.Override(266, false);
        That(!stale.Matches(events.Read(266).Schedule) && !service.Buy(role, Request(2002), out _).Accepted,
            "计划冻结活动版本，关闭不放行或重置已购次数");
        events.Schedule(266, clock.GetUtcNow().AddSeconds(1), clock.GetUtcNow().AddSeconds(2));
        events.Override(266, null);
        clock.Advance(TimeSpan.FromSeconds(1));
        That(service.Buy(role, Request(2002), out _).Accepted, "活动开始端点允许购买");
        clock.Advance(TimeSpan.FromSeconds(1));
        That(!service.Buy(role, Request(2002), out _).Accepted, "活动结束端点拒绝购买");
        var wallet = role.Inventory.AccountWallet with { MileageShop = new(17, ImmutableSortedDictionary<int, int>.Empty.Add(2001, 2)) };
        That(!wallet.Same(wallet with { MileageShop = MileageShopState.Empty }), "购买额度及消费积分计数参与钱包快照比较");
        // 仅支持的周期0才下发，2003保留待确认状态。
        Bytes(MileagePackets.Limits(wallet, mileage), [0, 1, 209, 7, 0, 0, 2, 0, 0, 0], "限购706使用商品号及已购份数");
        That(MileagePackets.State(wallet)[5] == 17, "427恢复独立消费获得计数，不再固定填零");
        Console.WriteLine($"PASS {Count - checks} 积分商城资源、资格、限购及原生通知断言");
    }

    sealed class EventStore : IServerEventStore
    {
        ServerEventSchedule state = new(266);
        public ServerEventSchedule Read(ushort id) => id == 266 ? state : new(id);
        public ServerEventSchedule Save(ServerEventSchedule expected, ServerEventSchedule desired)
        {
            if (state != expected) throw new InvalidOperationException();
            return state = desired with { Revision = expected.Revision + 1 };
        }
    }
}
