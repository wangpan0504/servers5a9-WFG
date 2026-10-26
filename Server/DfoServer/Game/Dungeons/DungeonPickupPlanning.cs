using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Dungeons;

internal static class DungeonPickupPlanning
{
    // 这里只规划资产；当前Run、本房key和同一准备仍由Session/地面owner复核。
    public static InventoryResult Gold(Character character, DungeonGroundContent content, GoldLimitCatalog limits)
    {
        content.Validate();
        if (!content.IsGold)
            throw new InvalidDataException("物品地面对象不能借金币拾取入口改写余额");
        return Currency(character, CurrencyKind.Gold, content.GoldAmount, limits);
    }

    public static InventoryResult Item(Character character, DungeonGroundContent content, ItemTemplate template,
        GoldLimitCatalog limits, EpicPieceCatalog pieces, DateTimeOffset now)
    {
        content.Validate();
        if (content.Item is not { } core || core.ItemId != template.Id || core.Kind != template.Kind)
            return InventoryResult.Reject();
        if (pieces.ByPiece.TryGetValue(core.ItemId, out var piece))
        {
            if (core.Kind != ItemKind.Material || core.Count > template.StackLimit || core.IsExpired(now.ToUnixTimeSeconds()) ||
                (core with { Value = 1, ExpireTime = 0 }) != new ItemCore(core.ItemId, ItemKind.Material, 1))
                return InventoryResult.Reject(19);
            var before = character.Inventory;
            before.EpicPieces.Validate(pieces);
            long balance = (long)before.EpicPieces.Get(piece.Index) + core.Count;
            if (balance > int.MaxValue) return InventoryResult.Reject(17);
            var after = before with { EpicPieces = before.EpicPieces.Set(pieces, piece.Index, (int)balance) };
            return new(true, 0, new(before, after, "dungeon_pickup_epic_piece",
                new(character, qualificationExpiresAt: core.ExpireTime > 0 ? core.ExpireTime : null)));
        }
        if (CurrencyWallet.RewardKind(core.ItemId) is { } kind)
        {
            if (core.IsExpired(now.ToUnixTimeSeconds()) || core.Count > template.StackLimit)
                return InventoryResult.Reject();
            return Currency(character, kind, core.Count, limits);
        }
        return InventoryRewardGrantService.TryGrantCore(character, core, template, pieces, now, usePickupSlotPolicy: true);
    }

    static InventoryResult Currency(Character character, CurrencyKind kind, int amount, GoldLimitCatalog limits)
    {
        var result = new CurrencyService(limits).Grant(character, kind, amount);
        if (!result.Accepted || result.Plan is not { } plan ||
            CurrencyWallet.Get(plan.After, kind) - CurrencyWallet.Get(plan.Before, kind) != amount)
            return InventoryResult.Reject(17);
        // 地面来源只有一份正增量；携带上限不足时保留整件，不消费被夹掉的剩余资产。
        return result with { Plan = new(plan.Before, plan.After, "dungeon_pickup_currency", new(character)) };
    }
}
