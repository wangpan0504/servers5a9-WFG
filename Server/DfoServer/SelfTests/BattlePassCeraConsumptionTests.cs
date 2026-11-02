using DfoServer.Game.BattlePass;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class BattlePassCeraConsumptionTests
{
    public static void RunCore()
    {
        int original = Count;
        CashShopProduct Product(int id, int price, int item = 14) => new(id, item, 1, price, "item");
        CashShopPaymentLine[] cart = [new(Product(1, 5), false), new(Product(2, 7), true), new(Product(3, 9), false)];
        var paid = CashShopPaymentFact.Allocate(cart, 11, 6, 4);
        That(paid[0] is { HappyTokenCera: 4, TokenCera: 1, Cera: 0 }, "混车首行先分摊真实欢乐及普通代币");
        That(paid[1] is { Cera: 7, TokenCera: 0, HappyTokenCera: 0 }, "后行仅点券仍先预留真实点券");
        That(paid[2] is { Cera: 4, TokenCera: 5, HappyTokenCera: 0 }, "余行按请求顺序分摊真实点券余额");
        That(paid.Sum(row => row.Cera) == 11 && paid.Sum(row => row.TokenCera) == 6 && paid.Sum(row => row.HappyTokenCera) == 4,
            "三币分摊与真实支付循环逐币一致");
        That(CashShopPaymentFact.Allocate([new(Product(4, 0), false)], 0, 0, 0).Single().Cera == 0,
            "优惠券或存量工具兑换零价不虚增点券");
        foreach (var row in new[] { (0L, 5L, 0L), (0L, 0L, 5L) })
            That(CashShopPaymentFact.Allocate([new(Product(5, 5), false)], row.Item1, row.Item2, row.Item3).Single().Cera == 0,
                "两代币支付不计真实点券");
        Bad(() => CashShopPaymentFact.Allocate(cart, 6, 11, 4), "仅点券预留不足拒绝");
        Bad(() => CashShopPaymentFact.Allocate(cart, 12, 6, 4), "多报实付拒绝");
        Bad(() => CashShopPaymentFact.Allocate(cart, 10, 6, 4), "少报实付拒绝");
        That(BattlePassCeraConsumption.Convert(600, 50, 0) == (300L, 0), "原600真实消费得到300XP");
        That(BattlePassCeraConsumption.Convert(1, 50, 0) == (0L, 50) &&
            BattlePassCeraConsumption.Convert(1, 50, 50) == (1L, 0), "奇数点券跨订单保留分子余数");
        That(BattlePassCeraConsumption.Convert(0, 50, 99) == (0L, 99), "零消费保持原余数");
        That(BattlePassCeraConsumption.Convert(long.MaxValue, 50, 0) == (long.MaxValue / 2, 50), "中间乘法不溢出合法long消费");
        Bad(() => BattlePassCeraConsumption.Convert(-1, 50, 0), "负消费拒绝");
        Bad(() => BattlePassCeraConsumption.Convert(1, 50, 100), "非法余数拒绝");
        var before = CurrencyWallet.Set(new InventorySnapshot([]), CurrencyKind.Cera, 5);
        var debit = CurrencyWallet.Set(before, CurrencyKind.Cera, 0);
        var bonus = CurrencyWallet.Set(debit, CurrencyKind.Cera, 8);
        var at = DateTimeOffset.FromUnixTimeSeconds(1000);
        var fact = CashShopPaymentFact.Capture(before, before, debit, bonus, [new(Product(6, 5), false)], 5, 0, 0, at, TimeProvider.System);
        var plan = new InventoryPlan(before, bonus, "cash_shop_buy") { CashShopPayment = fact };
        That(fact.Cera == 5 && fact.EligibleCera == 5 && fact.Matches(plan, bonus), "赠点券使净余额上升仍仅实扣5计消费");
        That(ReferenceEquals(plan.WithAfter(bonus).CashShopPayment, fact) && fact.Matches(plan.WithAfter(bonus), bonus),
            "库存复制保留同冻结订单与完整实付事实");
        That(!fact.Matches(plan, CurrencyWallet.Set(bonus, CurrencyKind.Cera, 9)), "最终提交钱包必须符合冻结结果");
        var fake = fact with { Paid = before };
        That(!fake.Matches(new(before, bonus, "cash_shop_buy") { CashShopPayment = fake }, bonus), "未实际扣费快照不能伪造消费");
        var goldFact = CashShopPaymentFact.Capture(before, before, debit, debit, [new(Product(7, 5, 0), false)], 5, 0, 0, at, TimeProvider.System);
        That(goldFact.EligibleCera == 0, "纯分摊负例：Gold商品行不计消费，非实际商城金币TCP");
        Console.WriteLine($"PASS BattlePassCeraConsumptionTests core ({Count - original} checks)");
    }
    static void Bad(Action action, string label)
    {
        try { action(); } catch (InvalidDataException) { That(true, label); return; }
        throw new InvalidOperationException(label);
    }
}
