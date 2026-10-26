using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;
using System.Collections.Immutable;

namespace DfoServer.Game.Inventory.Services;

internal sealed class InventoryShopService(ItemCatalog catalog, NpcShopCatalog shops, GoldLimitCatalog goldLimits, EpicPieceCatalog pieces, TimeProvider? clock = null)
{
    public TimeProvider Clock { get; } = clock ?? TimeProvider.System;
    static readonly int[] CubeItemIds = [3033, 3034, 3035, 3036, 3037, 3262];

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
        // ACK21必需实体核心并会解引用购入对象；服务商品不能伪造空槽或临时库存回执。
        if (template.IsPremiumService) return InventoryResult.Reject(17);
        // Client 普通装备购买实收 count=0，仍按一件购买；其他商品数量须为正。
        if (request.Count == 0 && template.Kind is not (ItemKind.Equipment or ItemKind.Talisman))
            return InventoryResult.Reject(0x85);
        int count = template.Kind is ItemKind.Equipment or ItemKind.Talisman or ItemKind.Avatar or ItemKind.Creature or ItemKind.CreatureEquipment or ItemKind.GuildMedal ? 1 : request.Count;
        // 交易窗口(ShopId<0)按名册买价结算；名册内没有金币买价也没有兑换材料的商品必须拒绝。
        if (shops.PurchasePrice(request.NpcId, request.ShopId, request.ItemId, template.ShopPrice) is not { } price)
            return InventoryResult.Reject(17);
        var before = character.Inventory;
        var after = before;
        var now = utcNow ?? Clock.GetUtcNow();
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
        // 休亚（NPC 486）用材料兑换的装备带交换资格位：客户端据此显示绿字并允许拖入交换窗口。
        // 其他商人、金币商品与非装备保持模板默认核心。
        bool exchangeOrigin = price.Materials.Length != 0 && template.Kind == ItemKind.Equipment &&
            shops.Shops.TryGetValue(request.ShopId, out var boughtShop) && boughtShop.NpcId == EpicExchangeCatalog.MerchantNpcId;
        var grant = exchangeOrigin
            ? InventoryRewardGrantService.TryGrantItems(character with { Inventory = paid },
                [new(0, new(InventoryListType.Main, 0),
                    InventoryRewardGrantService.Create(template, utcNow: now) with { Extension119 = 0x01 })],
                catalog, goldLimits, pieces, now)
            : InventoryRewardGrantService.TryGrantBatch(character with { Inventory = paid }, [new(request.ItemId, count)], catalog, goldLimits, pieces, now);
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
        long? deadline = price.Limit is { ResetDaily: true } ? DailyBoosterUsage.NextReset(now).ToUnixTimeSeconds() : null;
        foreach (var consumedSlot in spent.Plan.ChangedSlots)
            if (spent.Plan.BeforeIndex.Find(consumedSlot) is { ExpireTime: > 0 } input &&
                (deadline is null || input.ExpireTime < deadline)) deadline = input.ExpireTime;
        foreach (var item in after.Items)
            if (item.ExpireTime > 0 && grant.Plan.BeforeIndex.Find(item.Position) != item &&
                (deadline is null || item.ExpireTime < deadline)) deadline = item.ExpireTime;
        return new(true, 0, new(before, after, "npc_shop_buy", new(character, deadline, Clock, requirePosition: true)));
    }

    public ShopPurchaseCount[]? PurchaseCounts(Character character, int npcId, DateTimeOffset? utcNow = null)
    {
        if (!shops.Shops.Values.Any(shop => shop.NpcId == npcId) && !shops.IsTradingNpc(npcId))
            return null;
        int today = ShopPurchaseCount.Today(utcNow ?? DateTimeOffset.UtcNow);
        return character.Inventory.ShopPurchases.Where(row => row.NpcId == npcId && row.Effective(today) > 0 &&
            catalog.Get(row.ItemId).ShopPrice.Limit is { } limit && limit.AccountWide == row.AccountWide && limit.ResetDaily == row.ResetDaily)
            .OrderBy(row => row.ItemId).ToArray();
    }

    public InventoryResult Sell(Character character, SellItemRequest request)
    {
        var before = character.Inventory;
        if (request.Position.List is not (InventoryListType.Main or InventoryListType.Pet or InventoryListType.GuildMedal or InventoryListType.Talisman or InventoryListType.PersonalCargo or InventoryListType.Equipment))
            return InventoryResult.Reject();
        // 晶块槽是账号钱包的投影，没有可供删除或解锁的实体 UID。
        if (request.Position.List == InventoryListType.Main)
            foreach (int itemId in CubeItemIds)
                if (CurrencyWallet.RewardKind(itemId) is { } kind && ItemSlotBoundService.CurrencySlot(kind) == request.Position.Index)
                    return SellWalletCube(character, request, kind, itemId);
        var source = before.Find(request.Position);
        if (source is null || source.Core.EquipmentLockId != 0)
            return InventoryResult.Reject(0xd6);
        if (!shops.CanSell(source.Core.ItemId))
            return InventoryResult.Reject();
        var now = Clock.GetUtcNow();
        if (source.IsExpired(now.ToUnixTimeSeconds()))
            return InventoryResult.Reject();
        int count = InventoryStackRuleService.NormalizeMoveCount(source.Core, request.Count);
        var template = catalog.Get(source.Core.ItemId);
        // 时装穿戴后位于 Equipment，不能只靠容器排除；宠物蛋和成宠仍按原事务出售。
        if (template.Kind == ItemKind.Avatar || template.PvpFreeEquipment)
            return InventoryResult.Reject();
        // 交易商人按名册收购价结算；名册只列兑换/特殊交易或不在名册内的商品沿用模板价。
        int price = shops.TradingSellGold(request.NpcContext, source.Core.ItemId)
            ?? template.ShopPrice.GetSellGold(source.Core.Durability, template.Durability);
        long gold = (long)before.Gold + (long)price * count;
        if (gold > int.MaxValue || gold > new CurrencyService(goldLimits).CarryLimit(character))
            return InventoryResult.Reject(22);
        long? deadline = source.ExpireTime > 0 ? source.ExpireTime : null;
        var planned = before;
        if (template.ShopPrice.SaleLimit is { } saleLimit)
        {
            // [sale limit] 是物品级账号每日收购上限；用 npc_id=0 与各商人的购买计数分开存放。
            var key = new ShopPurchaseCount(0, source.Core.ItemId, saleLimit.AccountWide, true, ShopPurchaseCount.Today(now), count);
            var previous = before.ShopPurchases.SingleOrDefault(key.SameKey);
            int used = previous?.Effective(key.Day) ?? 0;
            if ((long)used + count > saleLimit.Count)
                return InventoryResult.Reject(0x85);
            key = key with { Count = used + count };
            deadline = Math.Min(deadline ?? long.MaxValue, DailyBoosterUsage.NextReset(now).ToUnixTimeSeconds());
            planned = before with { ShopPurchases = previous is null ? before.ShopPurchases.Add(key) : before.ShopPurchases.Replace(previous, key) };
        }
        var deletion = InventoryDeleteService.Plan(planned, request.Position, source.Core.Value, count);
        if (!deletion.Accepted)
            return deletion;
        return new(true, 0, new(before, deletion.Plan!.After with { Gold = checked((uint)gold) }, "npc_shop_sell", new(character, deadline, Clock)), count);
    }

    InventoryResult SellWalletCube(Character character, SellItemRequest request, CurrencyKind kind, int itemId)
    {
        var before = character.Inventory;
        // 钱包与邮件相同，必须明确出售正数且足额；畸形数量不能扩大成整笔余额。
        if (request.Count <= 0 || request.Count > CurrencyWallet.Get(before, kind))
            return InventoryResult.Reject();
        var template = catalog.Get(itemId);
        if (template.Kind != ItemKind.Material || !shops.CanSell(itemId))
            return InventoryResult.Reject();
        int price = template.ShopPrice.GetSellGold(0, template.Durability);
        long gold = (long)before.Gold + (long)price * request.Count;
        if (gold > int.MaxValue || gold > new CurrencyService(goldLimits).CarryLimit(character))
            return InventoryResult.Reject(22);
        var spent = CurrencyService.Spend(before, kind, request.Count);
        if (!spent.Accepted)
            return spent;
        return new(true, 0, new(before, spent.Plan!.After with { Gold = checked((uint)gold) }, "npc_shop_sell"), request.Count);
    }
}
