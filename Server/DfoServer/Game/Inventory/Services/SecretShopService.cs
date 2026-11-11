using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Services;

internal sealed class SecretShopService(SecretShopCatalog rules, ItemCatalog items,
    GoldLimitCatalog goldLimits, EpicPieceCatalog pieces)
{
    public SecretShopCatalog Rules { get; } = rules;

    public bool Supports(DungeonEntryPlan entry) => entry.IsOrdinary && !entry.IsPractice &&
        !entry.IsTrainingRoom && !entry.IsClassExperience && entry.RaidEntry is null &&
        !entry.IsHellLegend && !entry.IsCosmo && !entry.IsInfernoParty && entry.HellParty is null;

    public int Merchant(uint dungeonId, byte level, byte difficulty, Func<int, int> draw)
    {
        if (difficulty > 4) return SecretShopCatalog.AbsentMerchant;
        var rows = Rules.DungeonAppearance.Where(row => row.Key == dungeonId).ToArray();
        if (rows.Length == 0)
        {
            var section = Rules.Sections.SingleOrDefault(row => level >= row.Minimum && level <= row.Maximum);
            if (section is null) return SecretShopCatalog.AbsentMerchant;
            rows = Rules.LevelAppearance.Where(row => row.Key == section.Index).ToArray();
        }
        if (rows.Length == 0) return SecretShopCatalog.AbsentMerchant;
        int roll = Roll(10000, draw);
        foreach (var row in rows)
        {
            if (roll < row.Weights[difficulty]) return row.Merchant;
            roll -= row.Weights[difficulty];
        }
        throw new InvalidDataException("神秘商店出现权重未覆盖骰值");
    }

    public SecretShopState Offer(int merchant, uint dungeonId, byte level, Func<int, int> draw)
    {
        if (merchant == SecretShopCatalog.AbsentMerchant) return new(merchant, []);
        if (!Rules.Merchants.TryGetValue(merchant, out var definition))
            throw new InvalidDataException("神秘商店商人未声明");
        var section = Rules.Sections.SingleOrDefault(row => level >= row.Minimum && level <= row.Maximum);
        // 副本池显式优先，包括零槽的禁售池；不能把空池补成等级商品。
        if (!definition.Dungeons.TryGetValue(checked((int)dungeonId), out var pool) &&
            (section is null || !definition.Levels.TryGetValue(section.Index, out pool)))
            return new(merchant, []);
        var candidates = pool.Products.Where(row => row.Weight > 0).ToList();
        var offers = ImmutableArray.CreateBuilder<SecretShopOffer>();
        for (int index = 0; index < pool.Slots && candidates.Count != 0; index++)
        {
            int value = Roll(checked(candidates.Sum(row => row.Weight)), draw);
            int selected = 0;
            while (value >= candidates[selected].Weight) value -= candidates[selected++].Weight;
            var product = candidates[selected];
            candidates.RemoveAll(row => row.ItemId == product.ItemId);
            // 当前匹配资源均为普通金币商品；未确认的额外币种不能当成免费。
            if (product.Option != 0 || product.Premium != 0)
                throw new InvalidDataException("神秘商店商品使用尚未支持的实例选项或支付币种");
            int price = checked((int)((long)product.Gold *
                (100 + Roll(checked(Rules.PriceRandom * 2 + 1), draw) - Rules.PriceRandom) / 100));
            offers.Add(new(product.ItemId, checked((byte)product.Option), price, product.Count));
        }
        return new(merchant, offers.ToImmutable());
    }

    public SecretShopPurchasePlan Buy(Character character, SecretShopState state, SecretShopPurchase request,
        DateTimeOffset now)
    {
        SecretShopPurchasePlan Reject(ushort error, string reason) => new(state,
            InventoryResult.Reject(error) with { RejectReason = reason });
        if (!state.Available) return Reject(17, "secret_shop_unavailable");
        int index = -1;
        for (int at = 0; at < state.Offers.Length; at++)
            if (state.Offers[at].ItemId == request.ItemId) { index = at; break; }
        if (index < 0) return Reject(17, "secret_shop_unknown_item");
        var offer = state.Offers[index];
        ItemTemplate template;
        try { template = items.Get(request.ItemId); }
        catch (InvalidDataException) { return Reject(17, "secret_shop_unknown_template"); }
        int count = request.Count == 0 && template.Kind == ItemKind.Equipment ? 1 : request.Count;
        if (count <= 0 || count > offer.Remaining || template.Kind == ItemKind.Equipment && count != 1)
            return Reject(19, "secret_shop_stock_required");
        long cost = (long)offer.UnitGold * count;
        if (cost > int.MaxValue || character.Inventory.Gold < cost) return Reject(10, "secret_shop_gold_required");
        var paid = character.Inventory with { Gold = character.Inventory.Gold - checked((uint)cost) };
        var grant = InventoryRewardGrantService.TryGrantBatch(character with { Inventory = paid },
            [new(request.ItemId, count)], items, goldLimits, pieces, now);
        if (!grant.Accepted) return Reject(4, "secret_shop_inventory_full");
        var after = grant.Plan!.After;
        // ACK296创建普通背包对象；虚拟货币、账号材料和特殊容器不伪装成Main实体。
        var received = after.Items.FirstOrDefault(item => item.Position.List == InventoryListType.Main &&
            item.Core.ItemId == request.ItemId && item.Core.Count >
            (paid.Find(item.Position)?.Core is { } old && old.ItemId == request.ItemId ? old.Count : 0));
        if (received is null) return Reject(17, "secret_shop_unsupported_receipt");
        int remaining = offer.Remaining - count;
        return new(state with { Offers = state.Offers.SetItem(index, offer with { Remaining = remaining }) },
            new(true, 0, new(character.Inventory, after, "secret_shop_buy", new(character, requirePosition: true))),
            new(received.Position, received.Core, remaining));
    }

    static int Roll(int bound, Func<int, int> draw)
    {
        int result = draw(bound);
        if (result < 0 || result >= bound) throw new InvalidDataException("神秘商店抽样超出权重");
        return result;
    }
}
