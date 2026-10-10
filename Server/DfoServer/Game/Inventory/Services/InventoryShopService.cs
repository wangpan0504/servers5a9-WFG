using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;
using System.Collections.Immutable;

namespace DfoServer.Game.Inventory.Services;

internal sealed class InventoryShopService(ItemCatalog catalog, NpcShopCatalog shops, GoldLimitCatalog goldLimits, EpicPieceCatalog pieces)
{
    public InventoryResult Buy(Character character, BuyItemRequest request, out ShopPurchaseReceipt? receipt, DateTimeOffset? utcNow = null)
    {
        receipt = null;
        if (!shops.Allows(request.NpcId, request.ShopId, request.ItemId))
            return InventoryResult.Reject(17);
        // 匹配PVF普通商店不出售图鉴碎片；特殊取得回执未确认前不能伪造实体购买结果。
        if (pieces.ByPiece.ContainsKey(request.ItemId))
            return InventoryResult.Reject(17);
        if (request.Count is < 0 or > 1_000_000)
            return InventoryResult.Reject(0x85);
        ItemTemplate template;
        try { template = catalog.Get(request.ItemId); }
        catch (InvalidDataException) { return InventoryResult.Reject(17); }
        // Client 普通装备购买实收 count=0，仍按一件购买；其他商品数量须为正。
        if (request.Count == 0 && template.Kind != ItemKind.Equipment)
            return InventoryResult.Reject(0x85);
        int count = template.Kind is ItemKind.Equipment or ItemKind.Avatar or ItemKind.Creature or ItemKind.CreatureEquipment or ItemKind.GuildMedal ? 1 : request.Count;
        var price = template.ShopPrice;
        var before = character.Inventory;
        var after = before;
        var now = utcNow ?? DateTimeOffset.UtcNow;
        int today = ShopPurchaseCount.Today(now);
        if (price.Limit is { } limit)
        {
            var key = new ShopPurchaseCount(request.NpcId, request.ItemId, limit.AccountWide, limit.ResetDaily, limit.ResetDaily ? today : 0, count);
            var previous = before.ShopPurchases.SingleOrDefault(key.SameKey);
            int current = previous?.Effective(today) ?? 0;
            if ((long)current + count > limit.Count)
                return InventoryResult.Reject(0x85);
            key = key with { Count = current + count };
            after = after with { ShopPurchases = previous is null ? after.ShopPurchases.Add(key) : after.ShopPurchases.Replace(previous, key) };
        }
        long goldCost = (long)price.Gold * count;
        if (goldCost > int.MaxValue || after.Gold < goldCost)
            return InventoryResult.Reject(10);
        after = after with { Gold = checked(after.Gold - (uint)goldCost) };
        var spent = InventoryMaterialConsumptionService.Plan(after, price.Materials, pieces, now, multiplier: count);
        if (!spent.Accepted)
            return InventoryResult.Reject(10);
        var paid = spent.Plan!.After;
        var materials = ImmutableArray.CreateBuilder<ShopMaterialBalance>();
        foreach (int itemId in price.Materials.Select(material => material.ItemId).Distinct())
        {
            if (CurrencyWallet.RewardKind(itemId) is { } currency)
            {
                if (currency < CurrencyKind.Cera)
                    materials.Add(new(itemId, CurrencyWallet.Get(paid, currency)));
            }
            else if (!pieces.ByPiece.ContainsKey(itemId))
            {
                var first = before.Items.Where(item => item.Position.List == InventoryListType.Main && item.Core.ItemId == itemId)
                    .OrderBy(item => item.Position.Index).First();
                var remaining = paid.Find(first.Position);
                // 客户端只按模板找到首个旧格。该格未参与消耗时不触碰它，其余实际扣料格交给最终差量。
                if (remaining != first)
                    materials.Add(new(itemId, remaining?.Core.Count ?? 0));
            }
            // 图鉴和非Main钱包不伪装成实物材料，统一投影发送各自的最终余额。
        }
        if (materials.Count > byte.MaxValue)
            return InventoryResult.Reject(17); // 本版ACK的材料条数使用u8，必须在提交之前验证。
        var rewardKind = CurrencyWallet.RewardKind(request.ItemId);
        if (rewardKind is { } rewardCurrency)
        {
            // 点券券的取得展示尚属独立协议分支，不能借金币槽伪装实体。
            if (rewardCurrency >= CurrencyKind.Cera)
                return InventoryResult.Reject(17);
            long target = (long)CurrencyWallet.Get(paid, rewardCurrency) + count;
            int maximum = rewardCurrency == CurrencyKind.Gold ? new CurrencyService(goldLimits).CarryLimit(character) : int.MaxValue;
            if (target > maximum)
                return InventoryResult.Reject(0x85);
        }
        var grant = InventoryRewardGrantService.TryGrantBatch(character with { Inventory = paid }, [new(request.ItemId, count)], catalog, goldLimits, pieces, now);
        if (!grant.Accepted)
            return InventoryResult.Reject(grant.Error == 19 ? (ushort)17 : (ushort)4);
        after = grant.Plan!.After;
        InventorySlot position;
        ItemCore? receivedCore = null;
        if (rewardKind is { } kind)
            position = new(InventoryListType.Main, ItemSlotBoundService.CurrencySlot(kind)!.Value);
        else
        {
            var received = after.Items.First(item => item.Core.ItemId == request.ItemId &&
                (paid.Find(item.Position)?.Core is { } old && old.ItemId == request.ItemId ? old.Count : 0) < item.Core.Count);
            position = received.Position;
            receivedCore = received.Core;
        }
        receipt = new(request.ItemId, count, position, receivedCore, paid, materials.ToImmutable());
        return new(true, 0, new(before, after, "npc_shop_buy"));
    }

    public ShopPurchaseCount[]? PurchaseCounts(Character character, int npcId, DateTimeOffset? utcNow = null)
    {
        if (!shops.Shops.Values.Any(shop => shop.NpcId == npcId))
            return null;
        int today = ShopPurchaseCount.Today(utcNow ?? DateTimeOffset.UtcNow);
        return character.Inventory.ShopPurchases.Where(row => row.NpcId == npcId && row.Effective(today) > 0 &&
            catalog.Get(row.ItemId).ShopPrice.Limit is { } limit && limit.AccountWide == row.AccountWide && limit.ResetDaily == row.ResetDaily)
            .OrderBy(row => row.ItemId).ToArray();
    }

    public InventoryResult Sell(Character character, SellItemRequest request)
    {
        var before = character.Inventory;
        if (request.Position.List is not (InventoryListType.Main or InventoryListType.Avatar or InventoryListType.Pet or InventoryListType.GuildMedal or InventoryListType.PersonalCargo or InventoryListType.Equipment))
            return InventoryResult.Reject();
        var source = before.Find(request.Position);
        if (source is null || source.Core.EquipmentLockId != 0)
            return InventoryResult.Reject(0xd6);
        if (source.IsExpired(DateTimeOffset.UtcNow.ToUnixTimeSeconds()))
            return InventoryResult.Reject();
        int count = InventoryStackRuleService.NormalizeMoveCount(source.Core, request.Count);
        var template = catalog.Get(source.Core.ItemId);
        int price = template.ShopPrice.GetSellGold(source.Core.Durability, template.Durability);
        long gold = (long)before.Gold + (long)price * count;
        if (gold > int.MaxValue || gold > new CurrencyService(goldLimits).CarryLimit(character))
            return InventoryResult.Reject(22);
        var deletion = InventoryDeleteService.Plan(before, request.Position, source.Core.Value, count);
        if (!deletion.Accepted)
            return deletion;
        return new(true, 0, new(before, deletion.Plan!.After with { Gold = checked((uint)gold) }, "npc_shop_sell"), count);
    }
}
