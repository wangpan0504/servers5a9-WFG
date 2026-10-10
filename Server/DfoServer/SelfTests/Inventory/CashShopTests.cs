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

namespace DfoServer.Testing.Inventory;

internal static class CashShopTests
{
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
        var service = new InventoryCashShopService(resources.Items, resources.CashShop, resources.GoldLimits, resources.EpicPieces);
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
