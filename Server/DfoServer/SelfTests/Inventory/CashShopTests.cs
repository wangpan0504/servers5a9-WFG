using static DfoServer.Testing.TestCheck;
using System.Buffers.Binary;
using DfoServer.Infrastructure;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Currency;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using DfoServer.GameWorld.Items;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using DfoServer.Development;

namespace DfoServer.Testing.Inventory;

internal static class CashShopTests
{
    public static void RunCore()
    {
        int checks = Count;
        var now = new DateTimeOffset(2026, 10, 11, 10, 0, 0, TimeSpan.FromHours(8));
        var absolute = ItemExpiration.Parse(new PvfLib.PvfDocument("[expiration date]\n`2020-04-09 06:00:00`"));
        That(absolute.Resolve(now) < now.ToUnixTimeSeconds() && absolute.ResolveShop(now) == int.MaxValue,
            "正常规则保留绝对截止，调试使用2038协议上限");
        foreach (int days in new[] { 1, 7, 30 })
        {
            var relative = absolute with { Days = days };
            That(relative.Resolve(now) == now.ToUnixTimeSeconds() + (long)days * 86400 &&
                relative.ResolveShop(now) == int.MaxValue, "正常规则保留相对期限，调试统一覆盖");
        }
        That(new ItemExpiration().Resolve(now) == 0 && new ItemExpiration().ResolveShop(now) == int.MaxValue,
            "无期限配置正常保持永久，调试同样统一截止");
        That(DateTimeOffset.FromUnixTimeSeconds(absolute.ResolveShop(now)).ToOffset(TimeSpan.FromHours(8)) ==
            new DateTimeOffset(2038, 1, 19, 11, 14, 7, TimeSpan.FromHours(8)), "调试截止为UTC+8的2038协议上限");
        foreach (var kind in new[] { ItemKind.Equipment, ItemKind.Consumable, ItemKind.Avatar, ItemKind.Creature })
        {
            var template = new ItemTemplate(1106, kind, 0, 1, 10, 1, 0, 0, 1, "[free]", ushort.MaxValue, 0, "期限夹具")
                { Expiration = absolute with { Days = 7 } };
            var core = InventoryRewardGrantService.Create(template, utcNow: now, shopExpiration: true);
            var item = InventoryRewardGrantService.CreateItem(template, new(InventoryListType.Main, 65), core);
            That((item.Avatar?.ExpireTime ?? item.Creature?.ExpireTime ?? item.Core.ExpireTime) == int.MaxValue,
                "2038覆盖进入实际核心或独立详情期限 " + kind);
            That(InventoryRewardGrantService.Create(template, utcNow: now).ExpireTime == now.ToUnixTimeSeconds() + 7 * 86400,
                "其他创建入口保留原相对期限 " + kind);
        }
        That(CashShopPackets.Failure().SequenceEqual(new byte[] { 0, 0, 0 }), "商城失败包完整3字节，u16错误码为0");
        foreach (var (line, expected) in new[] { ("cash-shop-expiration", -1), ("cash-shop-expiration on", 1), ("cash-shop-expiration off", 0) })
            That(DevelopmentRequest.Parse(line, "10000") is { Action: DevelopmentAction.CashShopExpiration, Name: "" } request &&
                request.Primary == expected, "商城调试查询和切换不依赖角色");
        foreach (string line in new[] { "cash-shop-expiration true", "cash-shop-expiration on extra", "cash-shop-expiration 1" })
        {
            bool rejected = false;
            try { DevelopmentRequest.Parse(line, "10000"); }
            catch (InvalidDataException) { rejected = true; }
            That(rejected, "未知商城调试参数不能开启放宽");
        }
        Console.WriteLine($"PASS {Count - checks} 商城期限、失败包与调试命令核心断言");
    }

    public static void Run(TownCatalog towns, CharacterCatalog characters, ItemResources resources)
    {
        var product = resources.CashShop.Products.Values
            .First(row => row.CeraPrice > 0 && row.Section is not ("visual" or "selectable character premium" or "charac premium package") && resources.Items.Get(row.ItemId).Kind is not ItemKind.Equipment);
        string path = Path.Combine(Path.GetTempPath(), "s5a9-cash-shop-" + Guid.NewGuid().ToString("N"), "game.db");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var repo = new CharacterRepository(path);
        var owners = new AccountOwners();
        using var owner = owners.Acquire("cash-shop", () => { });
        var character = repo.Create(owner, new("CashShop", towns.Room, towns.InitialReturn)
        {
            Job = 0,
            Skills = characters.Get(0).Skills,
            Inventory = InventoryRewardGrantService.CreateStarter(characters.Get(0)) with { Expansion = 24 }
        }) with
        {
            Inventory = InventoryRewardGrantService.CreateStarter(characters.Get(0)) with
            {
                Expansion = 24,
                AccountWallet = AccountWallet.Empty.Set(CurrencyKind.Cera, 100_000)
            }
        };
        var service = new InventoryCashShopService(resources.Items, resources.CashShop, resources.GoldLimits, resources.EpicPieces)
            { IgnoreAbsoluteExpiration = false };
        var now = DateTimeOffset.UtcNow;
        var expired = resources.CashShop.Products.Values.First(row => row.CeraPrice > 0 && row.CeraPrice < 100_000 &&
            row.Section is not ("visual" or "selectable character premium" or "charac premium package") &&
            resources.Items.Get(row.ItemId).Expiration is { Days: 0, AbsoluteTime: > 0 } expiration && expiration.AbsoluteTime <= now.ToUnixTimeSeconds());
        var expiredRequest = new DfoServer.Game.Inventory.Models.CashShopPurchaseRequest(0, [new(expired.CommodityId, 0)]);
        That(!service.IgnoreAbsoluteExpiration && !service.Buy(character, expiredRequest, out _, now).Accepted,
            "商城调试关闭时拒绝已过绝对期限商品");
        That(DevelopmentGm.Answer("cash-shop-expiration", "10000", repo, owners, _ => throw new Exception("不应查询角色"), cashShop: service)
            == "OK {\"ignoreAbsoluteExpiration\":false}", "GM回读真实初始状态且不查询玩家");
        That(DevelopmentGm.Answer("cash-shop-expiration on", "10000", repo, owners, _ => null, cashShop: service)
            == "OK {\"ignoreAbsoluteExpiration\":true}", "GM在线开启同一商城服务的调试状态");
        var debug = service.Buy(character, expiredRequest, out var debugReceipt, now);
        That(debug.Accepted && debugReceipt is not null && debug.Plan!.After.AccountWallet.Get(CurrencyKind.Cera) ==
            100_000 - expired.CeraPrice && debug.Plan.After.Items.Single(row => row.Core.ItemId == expired.ItemId).Core.ExpireTime == int.MaxValue,
            "显式调试开启后历史商品可购买并正常规划扣款");
        That(!InventoryRewardGrantService.TryGrantBatch(character, [new(expired.ItemId, expired.Count)], resources.Items,
            resources.GoldLimits, resources.EpicPieces, now).Accepted, "调试开关不改变普通奖励绝对期限");
        var debugItem = debug.Plan!.After.Items.Single(row => row.Core.ItemId == expired.ItemId);
        DevelopmentGm.Answer("cash-shop-expiration off", "10000", repo, owners, _ => null, cashShop: service);
        That(!service.IgnoreAbsoluteExpiration && !service.Buy(character, expiredRequest, out _, now).Accepted && debugItem.Core.ExpireTime == int.MaxValue,
            "关闭恢复正常规则，不追溯改写已规划实例");
        That(new InventoryCashShopService(resources.Items, resources.CashShop, resources.GoldLimits, resources.EpicPieces).IgnoreAbsoluteExpiration,
            "重新装配商城服务默认开启");
        That(DevelopmentGm.Answer("cash-shop-expiration on", "10000", repo, owners, _ => null).StartsWith("ERR "),
            "未装配在线商城的GM不能伪报调试成功");
        var requestBody = new byte[19];
        requestBody[2] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(requestBody.AsSpan(7), checked((uint)product.CommodityId));
        var parsed = CashShopRequests.Buy(requestBody);
        That(parsed.PaymentMode == 0 && parsed.Items.Count == 1 && parsed.Items[0].CommodityId == product.CommodityId,
            "cash shop request parser reads commodity id and count");
        try { CashShopRequests.Buy(requestBody[..4]); throw new InvalidOperationException("malformed request accepted"); }
        catch (InvalidDataException) { }
        That(CashShopPackets.Success(product).Length == 24 && CashShopPackets.Success(product)[0] == 1 &&
            CashShopPackets.Failure()[0] == 0, "cash shop ACK builders keep success and failure envelope");
        var result = service.Buy(character, new(0, [new(product.CommodityId, 0)]), out var receipt);
        That(result.Accepted && receipt is not null, $"cash shop purchase charges Cera and returns receipt (error={result.Error}, item={product.ItemId}, section={product.Section}, count={product.Count})");
        That(result.Plan!.After.AccountWallet.Get(DfoServer.Game.Currency.CurrencyKind.Cera) ==
            100_000 - product.CeraPrice, "cash shop purchase deducts exact Cera price");
        var item = result.Plan.After.Items.FirstOrDefault(row => row.Core.ItemId == product.ItemId);
        That(item is not null && item.Position.List == ItemSlotBoundService.List(resources.Items.Get(product.ItemId).Kind),
            "cash shop reward enters the PVF-defined inventory list");
        That(!service.Buy(character, new(0, [new(int.MaxValue, 0)]), out _).Accepted,
            "unknown cash shop commodity is rejected without a charge");
        Console.WriteLine("PASS cash shop purchase parser, Cera charge, reward placement and ACK assertions");
    }
}
