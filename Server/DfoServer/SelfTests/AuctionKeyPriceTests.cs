using static DfoServer.Testing.TestCheck;
using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Auction;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.Network.Builders;
using DfoServer.Persistence;

namespace DfoServer;

/// <summary>
/// CMD 182 应答里 <c>key</c>（参考单价）的取值：**有在售取均价、无在售取该模板的商店售价、取不到回 1**，
/// 且任何一档都**不能是 0**。
///
/// 这一组存在的直接原因是用户实测：「窗口里价格列表全『暂无拍卖品』、下方一直停在
/// 『正在统计拍卖行价格……』、售价框被预填 0，手填 100 又被客户端拒绝『不能低于商店销售的价格』」
/// ⇒ 无在售时回 0 同时踩中客户端的两条判定。
///
/// 依据（参考实现另一个客户端版本）：
///   - `Data/auction_avg_reply.txt:441`：key 兼作「有价格数据」的非零门（客户端 `0x1455900` 判 `[+0x1a4] != 0`），
///     **不能下发 0**，实现里被夹到至少 1；
///   - `:307-312`：`keyminprice` 打开后 key = 该模板当前「最低一口价」，**无在售时回落到 `keyprice`（1000）**；
///   - 商店售价字段来自本仓库自己的 PVF 解析：`ItemShopPrice.Gold`
///     （`NpcShopCatalog.Parse` 的 `gold` 归并：有材料时 `price + add price`，否则 `price`，无 `price` 取 `value`）。
///
/// 模板用 `ItemCatalog.PrefillTemplates` 显式预置（含商店价），挂牌走真实文件库，因此不需要 PVF。
/// </summary>
internal static class AuctionKeyPriceTests
{
    /// <summary>有商店价的模板（商店卖 5000）。</summary>
    const int ShopTemplate = 10400;
    /// <summary>商店价为 0 的模板（PVF 里既没有 [price] 也没有 [value]）。</summary>
    const int NoShopTemplate = 12400;

    public static void Run()
    {
        int checks = Count;
        Average();
        Floor();
        Console.WriteLine($"PASS {Count - checks} auction average-price key assertions");
    }

    /// <summary>有在售：key = 在售均价（与列表「当前竞拍」列同源），且夹到 ≥ 1。</summary>
    static void Average()
    {
        using var f = new Fixture();
        var none = f.Service.ResolveAverageKey(ShopTemplate, f.Now);
        That(none.Average == 0 && none.Count == 0, "无在售时统计量与件数都是 0（不编造行情）");

        f.Seed(ShopTemplate, starting: 12_000, buyout: 17_000);
        var one = f.Service.ResolveAverageKey(ShopTemplate, f.Now);
        That(one.Average == 12_000 && one.Count == 1 && one.Source == AuctionKeySource.Listings && one.Key == 12_000,
            "★ 一条在售：key = 它的显示价（无出价取起拍价），来源记为 Listings");
        That(one.Floor == 5_000, "同一时刻仍然报告商店售价下限（日志里 Floor 与 Key 分开）");

        f.Seed(ShopTemplate, starting: 16_000, buyout: -1);
        var two = f.Service.ResolveAverageKey(ShopTemplate, f.Now);
        That(two.Average == 14_000 && two.Count == 2 && two.Key == 14_000, "两条在售（12000 + 16000）：key = 14000 的向下取整均价");

        // 退化数据（0 价挂牌）不能让 key 变成 0：客户端把 0 当成「没有价格数据」。
        using var zero = new Fixture();
        zero.Seed(ShopTemplate, starting: 0, buyout: -1);
        var degenerate = zero.Service.ResolveAverageKey(ShopTemplate, zero.Now);
        That(degenerate.Average == 0 && degenerate.Key == AuctionKeyPrice.MinimumKey && degenerate.Source == AuctionKeySource.Listings,
            "★ 均价算出来是 0 时仍然下发 1（非零门），来源仍是 Listings（不改成商店价）");
    }

    /// <summary>无在售：key = 商店售价；商店价取不到 → 1，并把原因写进日志字段。</summary>
    static void Floor()
    {
        using var f = new Fixture();
        var shop = f.Service.ResolveAverageKey(ShopTemplate, f.Now);
        That(shop.Key == 5_000 && shop.Floor == 5_000 && shop.Source == AuctionKeySource.ShopPrice && shop.Note is null,
            "★ 无在售：key = 该模板的商店售价 5000（Source = ShopPrice）");

        var noShop = f.Service.ResolveAverageKey(NoShopTemplate, f.Now);
        That(noShop.Key == AuctionKeyPrice.MinimumKey && noShop.Floor == AuctionKeyPrice.MinimumKey &&
            noShop.Source == AuctionKeySource.Minimum && noShop.Note is not null,
            "★ 商店价为 0 的模板：key 回落 1 并带上原因（Source = Minimum）");

        var missing = f.Service.ResolveAverageKey(999_999, f.Now);
        That(missing.Key == AuctionKeyPrice.MinimumKey && missing.Source == AuctionKeySource.Minimum && missing.Note is not null,
            "模板解析不到时同样回 1 并记原因（不抛给客户端、也不回 0）");

        foreach (int template in new[] { ShopTemplate, NoShopTemplate, 999_999, 0, -1 })
        {
            var quote = f.Service.ResolveAverageKey(template, f.Now);
            That(quote.Key >= AuctionKeyPrice.MinimumKey, $"模板 {template}：key 永远 ≥ 1（0 会被客户端判成「没有价格数据」）");
        }

        // 在售挂牌会改变 key，但不会改变「下限」的来源：两者是同一份报价里的不同字段。
        f.Seed(ShopTemplate, starting: 30_000, buyout: -1);
        var withListing = f.Service.ResolveAverageKey(ShopTemplate, f.Now);
        That(withListing is { Count: 1, Floor: 5_000, Key: 30_000, Source: AuctionKeySource.Listings },
            "有在售时 key 用均价，Floor 仍报告商店价（日志可分辨「这个价是哪来的」）");
    }

    /// <summary>真实文件库 + 预置模板 + 可插入挂牌的最小夹具（不需要 PVF）。</summary>
    sealed class Fixture : IDisposable
    {
        readonly string root = Path.Combine(Path.GetTempPath(), "S5A9-auction-key-" + Guid.NewGuid().ToString("N"));
        readonly AccountOwners owners = new();
        readonly AccountOwners.Lease owner;

        public CharacterRepository Repository { get; }
        public AuctionService Service { get; }
        public long Now { get; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        public Fixture()
        {
            Directory.CreateDirectory(root);
            string path = Path.Combine(root, "game.db");
            owner = owners.Acquire("key-price", () => { });
            Repository = new(path);
            var catalog = new ItemCatalog(null!, null!, null!, 0);
            catalog.PrefillTemplates(Templates);
            Service = new AuctionService(Repository, new AuctionCategoryResolver(catalog), catalog,
                new EpicPieceCatalog("[piece list]\n60000 70001\n[/piece list]"),
                AuctionPackets.ItemEntrySnapshot, AuctionPackets.ItemCoreFromEntry);
        }

        /// <summary>插入一条在售挂牌（卖家为 NULL 的机器人，不会命中「排除自己」）：价格完全由调用方控制。</summary>
        public void Seed(int templateId, int starting, int buyout)
        {
            var core = new ItemCore(templateId, ItemKind.Equipment, InventoryRewardGrantService.InitialQuality);
            Repository.InsertListings([new(null, "拍卖机器人甲", templateId, (byte)ItemKind.Equipment, 1,
                AuctionPackets.ItemEntrySnapshot(core), starting, buyout, Now, Now + 86_400)]);
        }

        /// <summary>两个模板：一个有商店价（5000），一个商店价为 0；两者都可自由交易。</summary>
        static readonly Dictionary<int, ItemTemplate> Templates = new()
        {
            [ShopTemplate] = new(ShopTemplate, ItemKind.Equipment, 15, 1, 45, 1, 1, 0, 1, "[free]", 65535, 0, "有商店价的胸甲")
            {
                ShopPrice = new(5_000, 1_000, [], null)
            },
            [NoShopTemplate] = new(NoShopTemplate, ItemKind.Equipment, 15, 1, 45, 1, 1, 0, 1, "[free]", 65535, 0, "无商店价的短裤")
        };

        public void Dispose()
        {
            owner.Dispose();
            TestFiles.DeleteDirectory(root, true);
        }
    }
}
