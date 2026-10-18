using System.Collections.Immutable;
using DfoServer.Game.Currency;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Skills;

namespace DfoServer.Game.Inventory.Models;

internal sealed class InventoryPlan
{
    public InventorySnapshot Before { get; }
    public InventorySnapshot After { get; }
    public string Reason { get; }
    public InventoryCharacterPrecondition? CharacterPrecondition { get; }
    public ImmutableArray<InventoryItem> OverflowItems { get; }
    public bool PreserveMaterials { get; init; }
    // 同一次扣药事务写入的限时经验加成。Until 为 Unix 秒，Permille 1000 表示在原经验上加 100%。
    public (long Until, int Permille)? ExpBonus { get; set; }
    public byte? EquipmentSlotFlagsAfter { get; init; }
    readonly Lazy<InventoryIndex> beforeIndex;
    readonly Lazy<InventoryIndex> afterIndex;
    readonly Lazy<ImmutableArray<CurrencyKind>> currencies;
    readonly Lazy<ImmutableArray<int>> pieces;
    readonly Lazy<ImmutableArray<InventorySlot>> slots;

    public InventoryPlan(InventorySnapshot before, InventorySnapshot after, string reason,
        InventoryCharacterPrecondition? characterPrecondition = null,
        IReadOnlyList<InventoryItem>? overflowItems = null)
    {
        Before = before;
        After = after;
        Reason = reason;
        CharacterPrecondition = characterPrecondition;
        OverflowItems = overflowItems is null ? [] : overflowItems.ToImmutableArray();
        beforeIndex = new(() => new(before));
        afterIndex = new(() => new(after));
        currencies = new(() => Enum.GetValues<CurrencyKind>()
            .Where(kind => CurrencyWallet.Get(before, kind) != CurrencyWallet.Get(after, kind)).ToImmutableArray());
        pieces = new(() => before.EpicPieces.ChangedIndexes(after.EpicPieces).ToImmutableArray());
        slots = new(() => Before.Items.Where(item => item != AfterIndex.Find(item.Position)).Select(item => item.Position)
            .Concat(After.Items.Where(item => BeforeIndex.Find(item.Position) is null).Select(item => item.Position))
            .OrderBy(slot => slot.List).ThenBy(slot => slot.Index).ToImmutableArray());
    }
    public InventoryIndex BeforeIndex => beforeIndex.Value;
    public InventoryIndex AfterIndex => afterIndex.Value;
    public ImmutableArray<InventorySlot> ChangedSlots => slots.Value;
    public ImmutableArray<CurrencyKind> ChangedCurrencies => currencies.Value;
    public ImmutableArray<int> ChangedEpicPieces => pieces.Value;
    public bool Changed => EquipmentSlotFlagsAfter is not null || Before.StaminaUntil != After.StaminaUntil || !Before.DevilUses.SequenceEqual(After.DevilUses) || Before.AvatarExpansion != After.AvatarExpansion || Before.AccountWallet.CharacterSlotCapacity != After.AccountWallet.CharacterSlotCapacity || OverflowItems.Length != 0 || ChangedSlots.Length != 0 || ChangedCurrencies.Length != 0 || Before.CubeContract != After.CubeContract || Before.AccountWallet.SeriaLuck != After.AccountWallet.SeriaLuck || !Before.AccountWallet.PremiumContracts.SequenceEqual(After.AccountWallet.PremiumContracts) || Before.Expansion != After.Expansion ||
        Before.GoldCarryLimit != After.GoldCarryLimit || Before.AuctionGoldLimit != After.AuctionGoldLimit ||
        Before.CargoCapacity != After.CargoCapacity || Before.AccountCargo.Capacity != After.AccountCargo.Capacity ||
        Before.AccountCargo.Gold != After.AccountCargo.Gold || !ShopPurchaseCount.Same(Before.ShopPurchases, After.ShopPurchases) ||
        !Before.EpicPieces.Same(After.EpicPieces);
    public bool AccountCargoChanged => Before.AccountCargo.Capacity != After.AccountCargo.Capacity ||
        Before.AccountCargo.Gold != After.AccountCargo.Gold || ChangedSlots.Any(slot => slot.List == InventoryListType.AccountCargo);
}

// 只有 Session 完成同步提交、安装在线快照后创建；规划中的 UID=0 不进入发布路径。
internal sealed class InventoryCommitReceipt(Character character, long generation, long selectionEpoch)
{
    public Character Character { get; } = character;
    public long Generation { get; } = generation;
    public long SelectionEpoch { get; } = selectionEpoch;
    public DfoServer.Game.Session.SessionPhase Phase { get; init; } = DfoServer.Game.Session.SessionPhase.Town;
    public DfoServer.Game.Dungeons.DungeonRunIdentity? DungeonIdentity { get; init; }
    public IReadOnlyList<ActiveMainQuest>? PreviousQuests { get; init; }
    public SkillSwitchInventoryState PreviousSkillSwitchInventory { get; init; } = character.SkillSwitchInventory;
    readonly Lazy<InventoryIndex> index = new(() => new(character.Inventory));
    public InventoryIndex Index => index.Value;
}
