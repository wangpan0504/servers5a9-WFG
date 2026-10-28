using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Dungeons;

internal sealed record BossRushClearReward(byte Rank, int Score, DungeonSpecificCardItem? Item, DungeonCardDelivery Delivery)
{
    public const string Rule = "boss-rush-rank-exclusive-v1";

    public static BossRushClearReward Prepare(BossRushRunState rush, Character character, ItemCatalog items,
        EpicPieceCatalog pieces, Func<int, int> draw, DateTimeOffset now)
    {
        var selected = rush.Catalog.Roll(rush.Rank, draw);
        DungeonSpecificCardItem? item = selected is { } row ? new(row.ItemId, row.Weight, row.Count) : null;
        var inventory = character.Inventory;
        var cores = ImmutableArray.CreateBuilder<DungeonCardReward>();
        var mails = ImmutableArray.CreateBuilder<ItemCore>();
        if (item is { } granted)
        {
            var template = items.Get(granted.ItemId);
            var core = InventoryRewardGrantService.Create(template, granted.Count, now);
            if (!core.IsStackable || core.IsExpired(now.ToUnixTimeSeconds()))
                throw new NotSupportedException("BossRush 段位奖励仅支持本版未过期的堆叠物品");
            var result = InventoryRewardGrantService.TryGrantCore(character with { Inventory = inventory }, core, template, pieces, now);
            if (!result.Accepted && result.Error != 17)
                throw new InvalidDataException("BossRush 冻结奖励不能发放: " + result.Error);
            if (result.Accepted) inventory = result.Plan!.After;
            else mails.Add(core);
            cores.Add(new(0, core, Rule, BossRushCatalog.ResourcePath, now.ToUnixTimeSeconds()));
        }
        return new(rush.Rank, rush.Score, item, new(new(character.Inventory, inventory, "dungeon_card_reward"),
            cores.ToImmutable(), mails.ToImmutable(), 0));
    }
}
