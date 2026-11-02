using System.Collections.Immutable;
using DfoServer.Game.Currency;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Models;

internal sealed record CashShopPaymentLine(CashShopProduct Product, bool OnlyCera);
internal sealed record CashShopPaymentAllocation(CashShopProduct Product, bool OnlyCera,
    long Cera, long TokenCera, long HappyTokenCera);

// 原支付循环冻结的实扣事实；不使用最终钱包净差，赠币和返利不能抵消或扩大实付。
internal sealed record CashShopPaymentFact(Guid Id, DateTimeOffset PlannedAt, TimeProvider Clock,
    InventorySnapshot Before, InventorySnapshot PaymentStart, InventorySnapshot Paid, InventorySnapshot After,
    long Cera, long TokenCera, long HappyTokenCera,
    ImmutableArray<CashShopPaymentAllocation> Lines)
{
    public long EligibleCera => Lines.Where(row => CurrencyWallet.RewardKind(row.Product.ItemId) != CurrencyKind.Gold)
        .Aggregate(0L, (sum, row) => checked(sum + row.Cera));

    // 本服确定分摊：先预留仅点券行，其余行按原请求顺序依次分摊欢乐/普通代币、真实点券。
    public static ImmutableArray<CashShopPaymentAllocation> Allocate(IReadOnlyList<CashShopPaymentLine> lines,
        long cera, long token, long happy)
    {
        if (cera < 0 || token < 0 || happy < 0 || lines.Count is < 1 or > 64 ||
            lines.Any(row => row.Product.CeraPrice < 0 || row.Product.IsMileage))
            throw new InvalidDataException("商城实际支付分摊输入无效");
        long reserved = lines.Where(row => row.OnlyCera).Aggregate(0L, (sum, row) => checked(sum + row.Product.CeraPrice));
        if (reserved > cera) throw new InvalidDataException("仅点券商品没有实际点券支付");
        long remainingCera = cera - reserved, remainingToken = token, remainingHappy = happy;
        var result = ImmutableArray.CreateBuilder<CashShopPaymentAllocation>(lines.Count);
        foreach (var line in lines)
        {
            long price = line.Product.CeraPrice, h = 0, t = 0, c;
            if (line.OnlyCera) c = price;
            else
            {
                h = Math.Min(remainingHappy, price); remainingHappy -= h;
                t = Math.Min(remainingToken, price - h); remainingToken -= t;
                c = price - h - t;
                if (c > remainingCera) throw new InvalidDataException("商城实际点券不足以分摊");
                remainingCera -= c;
            }
            result.Add(new(line.Product, line.OnlyCera, c, t, h));
        }
        if (remainingCera != 0 || remainingToken != 0 || remainingHappy != 0)
            throw new InvalidDataException("商城分摊与原支付循环不一致");
        return result.ToImmutable();
    }

    public static CashShopPaymentFact Capture(InventorySnapshot before, InventorySnapshot paymentStart,
        InventorySnapshot paid, InventorySnapshot after,
        IReadOnlyList<CashShopPaymentLine> lines, long cera, long token, long happy,
        DateTimeOffset at, TimeProvider clock) =>
        new(Guid.NewGuid(), at, clock, before, paymentStart, paid, after, cera, token, happy, Allocate(lines, cera, token, happy));

    public bool Matches(InventoryPlan plan, InventorySnapshot inventory)
    {
        if (Id == Guid.Empty || PlannedAt.ToUnixTimeSeconds() < 0 || plan.Reason != "cash_shop_buy" ||
            !ReferenceEquals(plan.Before, Before) || !ReferenceEquals(plan.CashShopPayment, this) || Lines.IsDefaultOrEmpty)
            return false;
        try
        {
            if (!Allocate(Lines.Select(row => new CashShopPaymentLine(row.Product, row.OnlyCera)).ToArray(),
                Cera, TokenCera, HappyTokenCera).SequenceEqual(Lines)) return false;
            foreach (var kind in new[] { CurrencyKind.Cera, CurrencyKind.TokenCera, CurrencyKind.HappyTokenCera })
            {
                long amount = kind == CurrencyKind.Cera ? Cera : kind == CurrencyKind.TokenCera ? TokenCera : HappyTokenCera;
                if (CurrencyWallet.Get(Before, kind) < amount ||
                    CurrencyWallet.Get(PaymentStart, kind) != CurrencyWallet.Get(Before, kind) ||
                    CurrencyWallet.Get(PaymentStart, kind) - CurrencyWallet.Get(Paid, kind) != amount ||
                    CurrencyWallet.Get(plan.After, kind) != CurrencyWallet.Get(After, kind) ||
                    CurrencyWallet.Get(inventory, kind) != CurrencyWallet.Get(After, kind)) return false;
            }
            return true;
        }
        catch (Exception error) when (error is InvalidDataException or OverflowException) { return false; }
    }
}
