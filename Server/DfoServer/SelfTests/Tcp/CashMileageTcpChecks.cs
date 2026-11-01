using DfoServer.GameWorld.Items;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

// 独立保存的匹配中国区积分规则金样；不调用生产Rebate或略过未知通知。
internal static class CashMileageTcpChecks
{
    static readonly Dictionary<int, int> Specific = new()
    {
        [102925] = 3,
        [102926] = 12,
        [102927] = 24,
        [102928] = 118,
        [102929] = 140,
        [102930] = 232,
        [100817] = 3,
        [100882] = 2,
        [100542] = 2,
        [100539] = 3,
        [100540] = 2,
        [100541] = 3,
        [100537] = 2,
        [100538] = 2,
        [100818] = 5,
        [100883] = 3,
        [100550] = 5,
        [100547] = 5,
        [100548] = 3,
        [100549] = 5,
        [100545] = 3,
        [100546] = 3,
        [103938] = 0,
        [103939] = 0,
        [103940] = 0,
        [103941] = 0
    };

    internal sealed record MileageExpected(string Account, int Before, int EarnedBefore, int Delta);

    internal static int ExpectedCashMileage(CashShopProduct product, int? settledPrice = null)
    {
        int price = settledPrice ?? product.CeraPrice;
        if (price <= 0 || product.IsMileage) return 0;
        if (Specific.TryGetValue(product.CommodityId, out int fixedAmount)) return fixedAmount;
        string section = product.Section.Replace(" ", "", StringComparison.Ordinal).ToLowerInvariant();
        float rate = section switch
        {
            "itemevent" or "package" or "characpremiumpackage" => 0.01f,
            "itemetc" or "avatardye" or "avatar" or "item" or "premium" or "selectablecharacterpremium" or
            "creature" or "material" or "recoveryitem" or "nametag" or "visual" or "coin" or
            "communitypackage" or "communityitem" or "regularpackage" => 0.03f,
            _ => 0f
        };
        return checked((int)(price * rate + 0.5f));
    }

    internal static MileageExpected MileageBefore(TcpTestFixture fixture, int expected, string? account = null)
    {
        account ??= fixture.Options.DevelopmentAccount;
        var row = fixture.Query("SELECT mileage,mileage_cash_earned FROM account_wallets WHERE account=$p0", account).Single();
        int balance = Convert.ToInt32(row[0]), earned = Convert.ToInt32(row[1]);
        That(balance >= 0 && earned >= 0 && expected >= 0, "购买前独立读真实账号积分与消费累计");
        return new(account, balance, earned, Math.Min(expected, Math.Max(0, 20000 - earned)));
    }

    internal static void MileageAfter(TcpPeer peer, TcpTestFixture fixture, MileageExpected expected)
    {
        if (expected.Delta > 0)
            peer.Expect(0, 537, Build(writer =>
            {
                writer.Write((byte)0); writer.Write(expected.Before);
                writer.Write(checked(expected.Before + expected.Delta)); writer.Write(checked(expected.EarnedBefore + expected.Delta));
            }));
        var rows = fixture.Query("SELECT mileage,mileage_cash_earned FROM account_wallets WHERE account=$p0", expected.Account);
        That(rows is [[long balance, long earned]] && balance == expected.Before + expected.Delta && earned == expected.EarnedBefore + expected.Delta,
            "成功购买按独立资源金样精确同步537和真实账号积分，零返分/已封顶保持原值");
    }

    // 原生108替换活动目录后，266开放时还会发布账号的完整限购706；不能让屏障吞掉它。
    internal static void MileageLimitsAfterRefresh(TcpPeer peer, TcpTestFixture fixture, string? account = null)
    {
        if (!peer.ExpectedOpenEvents.Contains(DfoServer.Game.Events.ServerEventService.MileageShopId)) return;
        account ??= fixture.Options.DevelopmentAccount;
        string saved = (string)fixture.Query("SELECT mileage_shop_purchases FROM account_wallets WHERE account=$p0", account).Single()[0];
        var purchases = System.Text.Json.JsonSerializer.Deserialize<Dictionary<int, int>>(saved)
            ?? throw new InvalidDataException("积分限购账号计数不是完整JSON对象");
        That(purchases.Values.All(value => value >= 0), "限购刷新使用实际账号的非负已购份数");
        // 独立保存的匹配资源七商品金样，不调用生产Limits Builder或回显收到的706。
        int[] commodities = [1000146, 1000147, 1000157, 1000158, 1000159, 1000160, 1000161];
        peer.Expect(0, 706, Build(writer =>
        {
            writer.Write((byte)0); writer.Write(checked((byte)commodities.Length));
            foreach (int commodity in commodities)
            {
                writer.Write(commodity);
                writer.Write(purchases.GetValueOrDefault(commodity));
            }
        }));
    }
}
