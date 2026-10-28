using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Marriage;

namespace DfoServer.Game.Marriage;

internal sealed class MarriageShopService(ItemCatalog catalog, NpcShopCatalog shops, GoldLimitCatalog goldLimits,
    EpicPieceCatalog pieces, MarriageCatalog marriage, TimeProvider? clock = null)
{
    // 匹配资源用此材料编号标价；余额来自关系成员，不是主背包中的同编号实体。
    internal const int LoveMaterialId = 10002606;
    readonly TimeProvider timer = clock ?? TimeProvider.System;

    public bool Handles(int itemId)
    {
        if (marriage.Furniture.ContainsKey(itemId)) return true;
        try { return catalog.Get(itemId).ShopPrice.Materials.Any(row => row.ItemId == LoveMaterialId); }
        catch (InvalidDataException) { return false; }
    }

    public InventoryResult Plan(Character character, MarriageRecord before, BuyItemRequest request,
        out MarriageRecord? after, out ShopPurchaseReceipt? receipt)
    {
        after = null;
        receipt = null;
        if (before.State != 4 || !before.IsMember(character.Id) ||
            !before.Members.TryGetValue(character.Id, out var member) ||
            !shops.Allows(request.NpcId, request.ShopId, request.ItemId)) return InventoryResult.Reject(17);
        ItemTemplate template;
        try { template = catalog.Get(request.ItemId); }
        catch (InvalidDataException) { return InventoryResult.Reject(17); }
        if (!Handles(request.ItemId) || template.IsPremiumService || pieces.ByPiece.ContainsKey(request.ItemId) ||
            shops.PurchasePrice(request.NpcId, request.ShopId, request.ItemId, template.ShopPrice) is not { } price)
            return InventoryResult.Reject(17);
        if (request.Count is < 0 or > 1_000_000 || request.Count == 0 && template.Kind is not (ItemKind.Equipment or ItemKind.Talisman))
            return InventoryResult.Reject(0x85);
        int count = template.Kind is ItemKind.Equipment or ItemKind.Talisman or ItemKind.Avatar or ItemKind.Creature or
            ItemKind.CreatureEquipment or ItemKind.GuildMedal ? 1 : request.Count;
        var now = timer.GetUtcNow();
        var inventory = character.Inventory;
        var paid = inventory;
        if (price.Limit is { } limit)
        {
            int today = ShopPurchaseCount.Today(now);
            var key = new ShopPurchaseCount(request.NpcId, request.ItemId, limit.AccountWide, limit.ResetDaily,
                limit.ResetDaily ? today : 0, count);
            var previous = inventory.ShopPurchases.SingleOrDefault(key.SameKey);
            int current = previous?.Effective(today) ?? 0;
            if ((long)current + count > limit.Count) return InventoryResult.Reject(0x85);
            key = key with { Count = current + count };
            paid = paid with { ShopPurchases = previous is null ? paid.ShopPurchases.Add(key) : paid.ShopPurchases.Replace(previous, key) };
        }
        long gold = (long)price.Gold * count;
        long love = 0;
        foreach (var cost in price.Materials.Where(row => row.ItemId == LoveMaterialId))
        {
            if (cost.Count <= 0) return InventoryResult.Reject(17);
            love += (long)cost.Count * count;
        }
        if (gold < 0 || gold > int.MaxValue || paid.Gold < gold || love > member.LovePoints)
            return InventoryResult.Reject(10);
        paid = paid with { Gold = checked(paid.Gold - (uint)gold) };
        var spent = InventoryMaterialConsumptionService.Plan(paid,
            price.Materials.Where(row => row.ItemId != LoveMaterialId).ToArray(), pieces, now, count);
        if (!spent.Accepted) return InventoryResult.Reject(10);
        paid = spent.Plan!.After;
        var materials = ImmutableArray.CreateBuilder<ShopMaterialBalance>();
        foreach (int id in price.Materials.Select(row => row.ItemId).Distinct())
        {
            if (id == LoveMaterialId || pieces.ByPiece.ContainsKey(id)) continue;
            if (CurrencyWallet.RewardKind(id) is { } currency)
            {
                if (currency < CurrencyKind.Cera) materials.Add(new(id, CurrencyWallet.Get(paid, currency)));
            }
            else
            {
                var first = inventory.Items.Where(item => item.Position.List == InventoryListType.Main && item.Core.ItemId == id)
                    .OrderBy(item => item.Position.Index).First();
                var remaining = paid.Find(first.Position);
                if (remaining != first) materials.Add(new(id, remaining?.Core.Count ?? 0));
            }
        }
        if (materials.Count > byte.MaxValue) return InventoryResult.Reject(17);
        var updated = before with
        {
            Revision = checked(before.Revision + 1),
            Members = before.Members.SetItem(character.Id, member with { LovePoints = checked(member.LovePoints - (int)love) })
        };
        InventorySlot position;
        ItemCore? core;
        InventorySnapshot resultInventory;
        if (marriage.Furniture.ContainsKey(request.ItemId))
        {
            if (count > template.StackLimit) return InventoryResult.Reject(0x85);
            ushort slot = 0;
            while (slot < MarriageCoupleRules.StorageSlots && before.Storage.ContainsKey(slot)) slot++;
            if (slot >= MarriageCoupleRules.StorageSlots) return InventoryResult.Reject(4);
            updated = updated with { Storage = updated.Storage.Add(slot, new(request.ItemId, count)) };
            // BUY_ITEM 消费者按模板选择共享家具容器，核心索引为该容器内的槽位。
            position = new(InventoryListType.Main, checked((short)slot));
            core = InventoryRewardGrantService.Create(template, count, now);
            resultInventory = paid;
        }
        else
        {
            if (CurrencyWallet.RewardKind(request.ItemId) is { } rewardCurrency)
            {
                if (rewardCurrency >= CurrencyKind.Cera) return InventoryResult.Reject(17);
                long balance = (long)CurrencyWallet.Get(paid, rewardCurrency) + count;
                int maximum = rewardCurrency == CurrencyKind.Gold ? new CurrencyService(goldLimits).CarryLimit(character) : int.MaxValue;
                if (balance > maximum) return InventoryResult.Reject(0x85);
            }
            var grant = InventoryRewardGrantService.TryGrantBatch(character with { Inventory = paid },
                [new(request.ItemId, count)], catalog, goldLimits, pieces, now);
            if (!grant.Accepted) return InventoryResult.Reject(grant.Error == 19 ? (ushort)17 : (ushort)4);
            resultInventory = grant.Plan!.After;
            if (CurrencyWallet.RewardKind(request.ItemId) is { } kind)
            {
                position = new(InventoryListType.Main, ItemSlotBoundService.CurrencySlot(kind)!.Value);
                core = null;
            }
            else
            {
                var received = resultInventory.Items.First(item => item.Core.ItemId == request.ItemId &&
                    (paid.Find(item.Position)?.Core is { } old && old.ItemId == request.ItemId ? old.Count : 0) < item.Core.Count);
                position = received.Position;
                core = received.Core;
            }
        }
        after = updated;
        receipt = new(request.ItemId, count, position, core, paid, materials.ToImmutable());
        return new(true, 0, new(inventory, resultInventory, "marriage_shop_buy"));
    }
}
