using DfoServer.Game.BattlePass;
using DfoServer.Game.Currency;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Models;

// 仅原商人名册购买成功后生成，保存扣料中间态；买入同名材料也不能把返还量冒成未支付。
internal sealed record NpcMaterialConsumeFact(Guid Id, DateTimeOffset PlannedAt, TimeProvider Clock,
    BuyItemRequest Request, int Quantity, int MaterialId, int Consumed, NpcShopCatalog Shops,
    ItemTemplate Template, ItemShopPrice Price, InventorySnapshot Before, InventorySnapshot Paid, InventorySnapshot After)
{
    public static NpcMaterialConsumeFact? Capture(NpcShopCatalog shops, ItemTemplate template, ItemShopPrice price,
        BuyItemRequest request, int quantity, InventorySnapshot before, InventorySnapshot paid,
        InventorySnapshot after, DateTimeOffset at, TimeProvider clock)
    {
        int material = BattlePassNpcMaterialConsumeSource.Material(request.NpcId);
        // 攻坚店原购买不核对请求NPC；不能据客户端声称的476/609替代真实商人身份。
        if (material == 0 || !MerchantMatches(shops, request)) return null;
        long spent = Count(before, material) - Count(paid, material);
        long expected = price.Materials.Where(row => row.ItemId == material).Sum(row => (long)row.Count * quantity);
        if (spent <= 0 || spent > int.MaxValue || spent != expected) return null;
        return new(Guid.NewGuid(), at, clock, request, quantity, material, checked((int)spent),
            shops, template, price, before, paid, after);
    }

    public bool Matches(InventoryPlan plan, InventorySnapshot inventory)
    {
        int quantity = Template.Kind is ItemKind.Equipment or ItemKind.Talisman or ItemKind.Avatar or
            ItemKind.Creature or ItemKind.CreatureEquipment or ItemKind.GuildMedal ? 1 : Request.Count;
        if (Id == Guid.Empty || plan.Reason != "npc_shop_buy" || !ReferenceEquals(plan.Before, Before) ||
            !ReferenceEquals(plan.After, After) || Template.Id != Request.ItemId || Quantity != quantity ||
            Quantity <= 0 || Request.Count is < 0 or > 1_000_000 || Consumed <= 0 ||
            MaterialId != BattlePassNpcMaterialConsumeSource.Material(Request.NpcId) || MaterialId == 0 ||
            !MerchantMatches(Shops, Request) || Shops.PurchasePrice(Request.NpcId, Request.ShopId, Request.ItemId,
                Template.ShopPrice) != Price ||
            Price.Materials.Where(row => row.ItemId == MaterialId).Sum(row => (long)row.Count * Quantity) != Consumed ||
            Count(Before, MaterialId) - Count(Paid, MaterialId) != Consumed ||
            inventory.Items.Length != After.Items.Length || !inventory.EpicPieces.Same(After.EpicPieces) ||
            !ShopPurchaseCount.Same(inventory.ShopPurchases, After.ShopPurchases) ||
            !inventory.ServerItems.Same(After.ServerItems) ||
            Enum.GetValues<CurrencyKind>().Any(kind => CurrencyWallet.Get(inventory, kind) != CurrencyWallet.Get(After, kind)))
            return false;
        var actual = new InventoryIndex(inventory);
        foreach (var expected in After.Items)
        {
            var current = actual.Find(expected.Position);
            if (current is null || current.Uid <= 0 || expected.Uid > 0 && current.Uid != expected.Uid ||
                expected.Uid == 0 && plan.BeforeIndex.FindUid(current.Uid) is not null) return false;
            var normalized = expected.Uid == 0 && (expected.Avatar is not null || expected.Creature is not null)
                ? expected with { Uid = current.Uid, Core = expected.Core with { Value = checked((int)current.Uid) } }
                : expected with { Uid = current.Uid };
            if (current != normalized) return false;
        }
        return true;
    }

    static bool MerchantMatches(NpcShopCatalog shops, BuyItemRequest request) =>
        shops.Allows(request.NpcId, request.ShopId, request.ItemId) &&
        (request.ShopId < 0 || shops.Shops.TryGetValue(request.ShopId, out var shop) && shop.NpcId == request.NpcId);

    static long Count(InventorySnapshot inventory, int itemId) => inventory.Items
        .Where(item => item.Position.List == InventoryListType.Main && item.Core.ItemId == itemId)
        .Sum(item => (long)item.Core.Count);
}
