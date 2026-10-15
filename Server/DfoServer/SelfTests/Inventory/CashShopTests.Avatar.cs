using System.Buffers.Binary;
using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Network.Parsers.Inventory;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing.Inventory;

internal static partial class CashShopTests
{
    static void AvatarCore()
    {
        var first = CashShopRequests.Buy(Convert.FromHexString("000001000004031F2104000000000000000000"));
        That(first is { CurrencyMode: 0, GiftMode: 0, PcRoom: 0, PaymentMode: 0 } &&
            first.Items.Single() is { CommodityId: 270623, AttributeValue: 4, TypeIndex: 3, PaymentMode: 0 },
            "实际散件购买19B请求保留期限3及属性4，不再丢弃期限");
        var mixed = new byte[34]; mixed[2] = 2;
        BinaryPrimitives.WriteUInt32LittleEndian(mixed.AsSpan(7), 270623);
        mixed[19] = 1; mixed[20] = 140; mixed[21] = 3;
        BinaryPrimitives.WriteUInt32LittleEndian(mixed.AsSpan(22), 700001);
        var parsed = CashShopRequests.Buy(mixed);
        That(parsed.PaymentMode == 0 && parsed.Items[1] is { PaymentMode: 1, AttributeValue: 140, TypeIndex: 3 },
            "支付方式属于每个商品，混合车不复用首行支付标记，属性不截成低五位");
        foreach (var body in new byte[][] { mixed[..^1], [.. mixed, 0], new byte[19] })
            Reject<InvalidDataException>(() => CashShopRequests.Buy(body), "商城商品数和正文严格闭合");
        var shop = CashShopCatalog.Parse("[avatar]\n700001 106550541 3 0 0 -1\n[/avatar]\n" +
            "[avatar dye]\n105531 490704677 1 0 0 100 `1个` 1 0\n[/avatar dye]");
        That(shop.Product(700001) is { Count: 1, AvatarType: 3, CeraPrice: -1 }, "商品第三列是类型索引，负一价格由EQU类型行决定");
        That(shop.Product(105531) is { ItemId: 490704677, Count: 1, CeraPrice: 100, Section: "avatar dye", AvatarType: 0 },
            "染色剂使用独立九列商品分类，不套用装扮的六列期限与动态价格");
        Reject<InvalidDataException>(() => CashShopCatalog.Parse("[avatar]\n700001 106550541 3 0 0 -2\n[/avatar]"), "其他负价不变成免费");
        var avatar = AvatarPackageCatalog.ParseAvatar("[avatar type select]\n7 1 2 200 0 0 0\n" +
            "30 0 0 440 0 0 0\n0 0 0 1320 0 0 0\n[/avatar type select]\n" +
            "[avatar select ability]\n140 `[HP MAX]` `+` 100\n[/avatar select ability]");
        That(avatar.PendingRule is null && avatar.Types.Length == 4 && avatar.Types[1] is { Days: 7, Gold: 1, WinPoints: 2, Cera: 200 } &&
            avatar.Types[3].Days == 0 && avatar.Abilities.SetEquals([140]), "共享装扮目录保持隐式0、独立价格及完整属性编号");
    }

    static void VerifyAvatars(TownCatalog towns, ItemResources resources)
    {
        var items = resources.Items;
        var now = DateTimeOffset.UtcNow;
        var character = new Character(1, 0, "AvatarShop", 0, towns.Room, towns.InitialReturn)
        {
            Job = 3, Level = 60,
            Inventory = InventorySnapshot.Empty with { AccountWallet = AccountWallet.Empty.Set(CurrencyKind.Cera, 10_000) }
        };
        var service = new InventoryCashShopService(items, resources.CashShop, resources.GoldLimits, resources.EpicPieces,
            contracts: resources.Contracts, avatars: resources.AvatarPackages) { IgnoreAbsoluteExpiration = false };
        var dyedCart = CashShopRequests.Buy(Convert.FromHexString("00000200000403EF340400000000000000000000FFFF3B9C01000000000000000000"));
        var dyed = service.Buy(character, dyedCart, out var dyedReceipt, now);
        That(dyed.Accepted && dyedReceipt!.Products.Select(row => row.CommodityId).SequenceEqual([275695, 105531]) &&
            dyed.Plan!.After.AccountWallet.Get(CurrencyKind.Cera) == 7260 && dyed.Plan.After.Items.Length == 2 &&
            dyed.Plan.After.Items.Single(row => row.Avatar is not null) is { Core.ItemId: 406560053, Core.Durability: 4, ExpireTime: 0 } &&
            dyed.Plan.After.Items.Single(row => row.Core.IsStackable) is { Core.ItemId: 490704677, Core.Count: 1, Core.Durability: 0 },
            "现场混合车按EQU收2640、染色剂收100，FF选项不误作装扮属性或期限");
        var shortCera = character with { Inventory = character.Inventory with { AccountWallet = AccountWallet.Empty.Set(CurrencyKind.Cera, 2739) } };
        That(service.Buy(shortCera, dyedCart, out _) is { Accepted: false, Error: 10, Plan: null } && shortCera.Inventory.Items.IsEmpty,
            "混合车余额差一点也整笔拒绝，不部分购买时装或染色剂");
        foreach (var (commodity, days, price) in new[] { (270621, 7, 200), (270622, 30, 440), (270623, 0, 1320) })
        {
            var product = resources.CashShop.Product(commodity)!;
            var result = service.Buy(character, Request(product, 4), out var receipt, now);
            var reward = result.Plan?.After.Items.Single();
            That(result.Accepted && receipt!.Products.Single().CeraPrice == price &&
                reward is { Uid: 0, Core.ItemId: 106550541, Core.Durability: 4, Avatar: not null } &&
                reward.ExpireTime == (days == 0 ? 0 : now.AddDays(days).ToUnixTimeSeconds()) &&
                result.Plan!.After.AccountWallet.Get(CurrencyKind.Cera) == 10_000 - price && character.Inventory.Items.IsEmpty,
                "普通时装按本次期限收费，属性、完整详情及期限进入同一纯计划 " + days);
        }
        var unlimited = resources.CashShop.Product(270623)!;
        foreach (var bad in new CashShopPurchaseRequest[]
        {
            new(0, [new(unlimited.CommodityId, 5, 3)]), new(0, [new(unlimited.CommodityId, 4, 0)]),
            new(0, [new(unlimited.CommodityId, 4, 1)]), new(2, [new(unlimited.CommodityId, 4, 3, 2)]),
            new(0, [new(unlimited.CommodityId, 4, 3, FirstOptionCount: 1)]),
            new(0, [new(unlimited.CommodityId, 4, 3, RelatedItemId: 1)]),
            Request(unlimited, 4) with { GiftMode = 1 }, Request(unlimited, 4) with { CurrencyMode = 1 }
        })
            That(service.Buy(character, bad, out _) is { Accepted: false, Plan: null }, "非法属性、期限、扩展和未支持支付不扣款发默认时装");
        That(!service.Buy(character with { Inventory = InventorySnapshot.Empty }, Request(unlimited, 4), out _, now).Accepted,
            "动态EQU价格仍必须足额支付");
        foreach (var (id, type) in new[] { (2681588, 1), (2681589, 2), (2681590, 3), (2681591, 4), (2681592, 5), (2681593, 6),
            (2681594, 9), (10004710, 6), (10004666, 6), (10004773, 6), (10004898, 9) })
            That(items.Get(id).AvatarGiftType == type, "全部指定STK按当前资源类型读取 " + id);
        foreach (var (couponId, commodity) in new[] { (2681588, 270621), (2681589, 270622), (2681590, 270623),
            (2681593, 622001), (10004710, 622001), (10004666, 622001), (10004773, 622001) })
        {
            var product = resources.CashShop.Product(commodity)!;
            var coupon = Coupon(couponId, 3, 2);
            var before = character with { Inventory = character.Inventory with { Items = [coupon] } };
            var result = service.Buy(before, Request(product, 0, 1), out _, now);
            That(result.Accepted && result.Plan!.After.Find(coupon.Position) is { Uid: 77, Core.Count: 1 } &&
                result.Plan.After.Items.Single(row => row.Avatar is not null).Core.ItemId == product.ItemId &&
                result.Plan.After.AccountWallet == before.Inventory.AccountWallet,
                "兑换兼容同gift type的活动券，快捷栏源也只扣一件、不扣点券 " + couponId);
        }
        var synthetic = CashShopCatalog.Parse("[avatar]\n700091 42611 3 0 0 -1\n700092 401500147 3 0 0 -1\n[/avatar]");
        var alternate = new InventoryCashShopService(items, synthetic, resources.GoldLimits, resources.EpicPieces,
            avatars: resources.AvatarPackages) { IgnoreAbsoluteExpiration = false };
        foreach (int id in new[] { 2681594, 10004898 })
        {
            var owner = character with { Inventory = character.Inventory with { Items = [Coupon(id, 65, 1)] } };
            var result = alternate.Buy(owner, Request(synthetic.Product(700091)!, 0, 1), out _, now);
            That(result.Accepted && result.Plan!.After.Items is [{ Core.ItemId: 42611, Core.Durability: 0, Avatar: not null }] &&
                result.Plan.After.AccountWallet == owner.Inventory.AccountWallet, "稀有券按type9兑换同档永久装扮 " + id);
        }
        var sharedAbility = alternate.Buy(character, Request(synthetic.Product(700092)!, 140), out _, now);
        That(sharedAbility.Accepted && sharedAbility.Plan!.After.Items.Single().Core.Durability == 140,
            "直接购买同样使用共享ability case的真实编号，不截断140或补不存在的0");
        var selected = new AvatarPackageCatalog(_ => "[avatar type select]\n7 3 4 5 0 0 0\n[/avatar type select]",
            new Dictionary<int, string> { [106550541] = "price.equ" }, new Dictionary<int, string>());
        var costs = CashShopCatalog.Parse("[avatar]\n700093 106550541 1 0 0 -1\n[/avatar]");
        var multipleCurrencies = new InventoryCashShopService(items, costs, resources.GoldLimits, resources.EpicPieces, avatars: selected)
            { IgnoreAbsoluteExpiration = false };
        var funded = character with { Inventory = character.Inventory with { Gold = 10, WinPoints = 10 } };
        var payment = multipleCurrencies.Buy(funded, Request(costs.Product(700093)!, 0), out _, now);
        That(payment.Accepted && payment.Plan!.After is { Gold: 7, WinPoints: 6 } &&
            payment.Plan.After.AccountWallet.Get(CurrencyKind.Cera) == 9995, "七整数行的金币、胜点和点券各扣真实费用");
        var upper = resources.CashShop.Product(622001)!;
        var wrong = character with { Inventory = character.Inventory with { Items = [Coupon(2681590, 65, 1)] } };
        That(!service.Buy(wrong, Request(upper, 0, 1), out _, now).Accepted, "低档券不能兑换高档时装，不回退收费");
        var valid = Coupon(2681590, 66, 1);
        var expired = Coupon(2681590, 65, 1) with { Uid = 76, Core = valid.Core with { ExpireTime = (int)now.ToUnixTimeSeconds() } };
        var locked = expired with { Core = valid.Core with { EquipmentLockId = 1 } };
        foreach (var denied in new[] { expired, locked })
        {
            var owner = character with { Inventory = character.Inventory with { Items = [denied, valid] } };
            var result = service.Buy(owner, Request(unlimited, 4, 1), out _, now);
            That(result.Accepted && result.Plan!.After.Items.Contains(denied) && result.Plan.After.Find(valid.Position) is null,
                "跳过到期/安全锁券，仅消费下一个有效实例");
            That(!service.Buy(owner with { Inventory = owner.Inventory with { Items = [denied] } }, Request(unlimited, 4, 1), out _, now).Accepted,
                "只有非法源时拒绝，不消费、不借点券支付");
        }
        var one = character with { Inventory = character.Inventory with { Items = [valid] } };
        var duplicate = new CashShopPurchaseRequest(1, [new(unlimited.CommodityId, 4, 3, 1), new(unlimited.CommodityId, 0, 3, 1)]);
        That(!service.Buy(one, duplicate, out _, now).Accepted && one.Inventory.Items.Single() == valid,
            "同车重复兑换足额校验，不能一张券发两件或部分扣款");
        var mixed = new CashShopPurchaseRequest(0, [new(unlimited.CommodityId, 4, 3), new(unlimited.CommodityId, 0, 3, 1)]);
        var hybrid = service.Buy(one, mixed, out _, now);
        That(hybrid.Accepted && hybrid.Plan!.After.Items.Count(item => item.Avatar is not null) == 2 &&
            hybrid.Plan.After.AccountWallet.Get(CurrencyKind.Cera) == 8680 && hybrid.Plan.After.Find(valid.Position) is null,
            "同车按行分别扣点券和兑换券，完整发两件独立原型");
        var finite = valid with { Core = valid.Core with { ExpireTime = (int)now.AddSeconds(1).ToUnixTimeSeconds() } };
        var planned = service.Buy(one with { Inventory = one.Inventory with { Items = [finite] } }, Request(unlimited, 4, 1), out _, now);
        That(planned.Accepted && planned.Plan!.CharacterPrecondition!.Matches(one, now.ToUnixTimeSeconds()) &&
            !planned.Plan.CharacterPrecondition.Matches(one, finite.ExpireTime), "源券到期秒在原角色提交前置条件再次复核");
        var template = items.Get(unlimited.ItemId);
        var fill = Enumerable.Range(0, ItemSlotBoundService.AvatarCapacity).Select(slot => InventoryRewardGrantService.CreateItem(template,
            new(InventoryListType.Avatar, (short)slot), InventoryRewardGrantService.Create(template, utcNow: now)) with
            { Uid = 1000 + slot, Core = InventoryRewardGrantService.Create(template, utcNow: now) with { Value = 1000 + slot } }).ToImmutableArray();
        var overflow = service.Buy(character with { Inventory = character.Inventory with { Items = fill } }, Request(unlimited, 4), out _, now);
        That(overflow.Accepted && overflow.Plan!.OverflowItems is [{ Avatar: not null, Core.Durability: 4, ExpireTime: 0 }] &&
            overflow.Plan.After.Items.SequenceEqual(fill), "商城满页沿既有邮件交付完整实例，不扣款后丢失奖励");

        InventoryItem Coupon(int id, short slot, int count) => new(77, new(InventoryListType.Main, slot),
            InventoryRewardGrantService.Create(items.Get(id), count, now) with { ExpireTime = 0, SortLockFlag = 1 });
    }

    static CashShopPurchaseRequest Request(CashShopProduct product, byte ability, byte mode = 0) =>
        new(mode, [new(product.CommodityId, ability, checked((byte)product.AvatarType), mode)]);
}
