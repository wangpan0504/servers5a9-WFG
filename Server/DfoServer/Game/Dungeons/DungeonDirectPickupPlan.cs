using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Dungeons;

internal sealed record DungeonDirectPickupEntry(DungeonGroundObject Ground, InventorySlot Destination,
    int? EpicPieceIndex, bool AccountMaterial, CurrencyKind? Currency, int Amount);

// 只消费同一次死亡已冻结的物品，不生成掉落、不重抽装备核心；提交仍由原死亡事务负责。
internal sealed class DungeonDirectPickupPlan
{
    public Character Expected { get; }
    public PreparedDungeonDeath Death { get; }
    public FrozenDungeonGroundDeath Loot { get; }
    public InventorySnapshot Before { get; }
    public InventorySnapshot After { get; }
    public ImmutableArray<DungeonDirectPickupEntry> Entries { get; }
    public ItemCatalog Items { get; }
    public EpicPieceCatalog Pieces { get; }
    readonly long? expiresAt;
    readonly TimeProvider clock;

    DungeonDirectPickupPlan(Character expected, PreparedDungeonDeath death, FrozenDungeonGroundDeath loot,
        InventorySnapshot before, InventorySnapshot after, ImmutableArray<DungeonDirectPickupEntry> entries,
        ItemCatalog items, EpicPieceCatalog pieces, long? expiresAt, TimeProvider clock)
    {
        Expected = expected;
        Death = death;
        Loot = loot;
        Before = before;
        After = after;
        Entries = entries;
        Items = items;
        Pieces = pieces;
        this.expiresAt = expiresAt;
        this.clock = clock;
    }

    public bool IsExpired => expiresAt is { } deadline && clock.GetUtcNow().ToUnixTimeSeconds() >= deadline;

    public static DungeonDirectPickupPlan Create(Character expected, PreparedDungeonDeath death,
        FrozenDungeonGroundDeath loot, InventorySnapshot before, ItemCatalog items, EpicPieceCatalog pieces,
        bool enabled, DateTimeOffset now, TimeProvider clock, GoldLimitCatalog? limits = null)
    {
        var after = before;
        var entries = ImmutableArray.CreateBuilder<DungeonDirectPickupEntry>();
        long? expiresAt = null;
        if (enabled)
            foreach (var ground in loot.Grounds)
            {
                if (ground.InventoryOrigin is not null || ground.PassiveOrigin is not null) continue;
                CurrencyKind? currency = ground.IsGold ? CurrencyKind.Gold : CurrencyWallet.RewardKind(ground.Core.ItemId);
                ItemTemplate? template = null;
                if ((ground.IsGold && limits is null) || (!ground.IsGold && !items.TryGet(ground.Core.ItemId, out template))) continue;
                var candidate = expected with { Inventory = after };
                var result = ground.IsGold ? DungeonPickupPlanning.Gold(candidate, ground.Content, limits!, now, clock) :
                    DungeonPickupPlanning.Item(candidate, ground.Content, template!, limits, pieces, now);
                if (!result.Accepted || result.Plan is not { } plan || !plan.Changed ||
                    plan.ChangedSlots.Length > 1) continue;
                int? piece = !ground.IsGold && pieces.ByPiece.TryGetValue(ground.Core.ItemId, out var definition) ? definition.Index : null;
                bool material = currency is null && piece is null && template?.AccountMaterial is not null;
                bool physical = currency is null && piece is null && !material;
                if (physical && plan.ChangedSlots.Length != 1) continue;
                int amount = ground.IsGold ? checked((int)((long)plan.After.Gold - plan.Before.Gold)) : ground.Core.Count;
                entries.Add(new(ground, physical ? plan.ChangedSlots[0] : new InventorySlot(InventoryListType.Main, 0), piece, material, currency, amount));
                after = plan.After;
                if (plan.CharacterPrecondition?.QualificationExpiresAt is { } deadline)
                    expiresAt = Math.Min(expiresAt ?? long.MaxValue, deadline);
                if (!ground.IsGold && ground.Core.ExpireTime > 0)
                    expiresAt = expiresAt is { } previous ? Math.Min(previous, ground.Core.ExpireTime) : ground.Core.ExpireTime;
            }
        return new(expected, death, loot, before, after, entries.ToImmutable(), items, pieces, expiresAt, clock);
    }
}

internal sealed record DungeonDirectPickupCommit(ImmutableArray<uint> GroundKeys,
    ImmutableArray<InventorySlot> ChangedSlots, ImmutableArray<int> EpicPieceIndexes, bool AccountMaterialsChanged,
    ImmutableArray<CurrencyKind> ChangedCurrencies)
{
    public static DungeonDirectPickupCommit Empty { get; } = new([], [], [], false, []);
}
