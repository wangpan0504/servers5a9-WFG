using System.Collections.Immutable;
using DfoServer.Game.Currency;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Skills;

namespace DfoServer.Game.Inventory.Models;

internal sealed record ItemUseIntent(int ItemId, int Count);
internal sealed record AccountItemRefillClaim(int ItemId, AccountItemRefillState? Before, AccountItemRefillState After);
internal sealed record InventoryEventPrecondition(DfoServer.Game.Events.ServerEventSchedule Schedule, TimeProvider Clock)
{
    public bool Matches(DfoServer.Game.Events.ServerEventSchedule actual) => actual == Schedule && actual.IsActive(Clock.GetUtcNow());
}

internal sealed class InventoryPlan
{
    public InventorySnapshot Before { get; }
    public InventorySnapshot After { get; }
    public string Reason { get; }
    public InventoryCharacterPrecondition? CharacterPrecondition { get; }
    public ImmutableArray<InventoryItem> OverflowItems { get; }
    public bool PreserveMaterials { get; init; }
    public ImmutableArray<ItemUseIntent> UsedItems { get; init; } = [];
    public ImmutableArray<AccountItemRefillClaim> AccountRefillClaims { get; init; } = [];
    public ImmutableArray<DfoServer.Game.Mailbox.MailboxDelivery> DailyItemMails { get; init; } = [];
    public ImmutableArray<int> ResetItemIds { get; init; } = [];
    // 同一次扣药事务写入的限时经验加成。Until 为 Unix 秒，Permille 1000 表示在原经验上加 100%。
    public (long Until, int Permille)? ExpBonus { get; set; }
    public byte? EquipmentSlotFlagsAfter { get; init; }
    public bool UnlockSkillTree { get; init; }
    public DfoServer.Game.Guild.GuildHongbaoPurchase? GuildHongbaoPurchase { get; init; }
    public InventoryEventPrecondition? RequiredEvent { get; init; }
    public int? AccountMaterialRaidWeek { get; init; }
    public DfoServer.Game.PcRoom.PcRoomDailyClaim? PcRoomClaim { get; set; }
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
    // 业务在规划完成后补写同事务状态（如装扮预设实时保存）：复制全部字段，惰性索引按新快照重算。
    public InventoryPlan WithAfter(InventorySnapshot after, InventoryCharacterPrecondition? precondition = null,
        ImmutableArray<ItemUseIntent>? usedItems = null) => new(Before, after, Reason, precondition ?? CharacterPrecondition, OverflowItems)
    {
        PreserveMaterials = PreserveMaterials,
        UsedItems = usedItems ?? UsedItems,
        AccountRefillClaims = AccountRefillClaims,
        DailyItemMails = DailyItemMails,
        ResetItemIds = ResetItemIds,
        EquipmentSlotFlagsAfter = EquipmentSlotFlagsAfter,
        UnlockSkillTree = UnlockSkillTree,
        GuildHongbaoPurchase = GuildHongbaoPurchase,
        RequiredEvent = RequiredEvent,
        AccountMaterialRaidWeek = AccountMaterialRaidWeek,
        PcRoomClaim = PcRoomClaim,
        ExpBonus = ExpBonus
    };
    public InventoryIndex BeforeIndex => beforeIndex.Value;
    public InventoryIndex AfterIndex => afterIndex.Value;
    public ImmutableArray<InventorySlot> ChangedSlots => slots.Value;
    public ImmutableArray<CurrencyKind> ChangedCurrencies => currencies.Value;
    public ImmutableArray<int> ChangedEpicPieces => pieces.Value;
    public bool AccountMaterialsChanged => !Before.AccountWallet.SameMaterials(After.AccountWallet);
    public bool Changed => Before.PrivilegeService != After.PrivilegeService || !Before.ShieldDeck.Same(After.ShieldDeck) || !Before.ServerItems.Same(After.ServerItems) || !AccountRefillClaims.IsEmpty || !BossSelectionBuff.Same(Before.BossSelectionBuff, After.BossSelectionBuff) || !Before.NpcFavor.SequenceEqual(After.NpcFavor) || UnlockSkillTree || !Before.ItemMakingSkills.Same(After.ItemMakingSkills) || !Before.Skins.Same(After.Skins) || !Before.AvatarPresets.Same(After.AvatarPresets) || !Before.ConsumableStates.SequenceEqual(After.ConsumableStates) || EquipmentSlotFlagsAfter is not null || Before.StaminaUntil != After.StaminaUntil || !Before.DevilUses.SequenceEqual(After.DevilUses) || Before.AvatarExpansion != After.AvatarExpansion || Before.AccountWallet.CharacterSlotCapacity != After.AccountWallet.CharacterSlotCapacity || OverflowItems.Length != 0 || ChangedSlots.Length != 0 || ChangedCurrencies.Length != 0 || Before.CubeContract != After.CubeContract || Before.AccountWallet.SeriaLuck != After.AccountWallet.SeriaLuck || !Before.AccountWallet.PremiumContracts.SequenceEqual(After.AccountWallet.PremiumContracts) || Before.Expansion != After.Expansion ||
        AccountMaterialsChanged || !Before.AccountWallet.MileageShop.Same(After.AccountWallet.MileageShop) ||
        Before.GoldCarryLimit != After.GoldCarryLimit || Before.AuctionGoldLimit != After.AuctionGoldLimit ||
        Before.CargoCapacity != After.CargoCapacity || Before.AccountCargo.Capacity != After.AccountCargo.Capacity ||
        Before.AccountCargo.Gold != After.AccountCargo.Gold || !ShopPurchaseCount.Same(Before.ShopPurchases, After.ShopPurchases) ||
        !Before.EpicPieces.Same(After.EpicPieces) || !DailyBoosterUsage.Same(Before.DailyBoosterUses, After.DailyBoosterUses) ||
        !Before.AccountSkins.Same(After.AccountSkins);
    public bool AccountCargoChanged => Before.AccountCargo.Capacity != After.AccountCargo.Capacity ||
        Before.AccountCargo.Gold != After.AccountCargo.Gold || ChangedSlots.Any(slot => slot.List == InventoryListType.AccountCargo);
    public bool AccountSkinsChanged => !Before.AccountSkins.Same(After.AccountSkins);
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
