using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Characters;
using DfoServer.GameWorld.Characters;
using System.Collections.Immutable;
using DfoServer.Game.Currency;

namespace DfoServer.Game.Inventory.Models;

internal enum InventoryListType : byte
{
    Main = 0,
    Avatar = 1,
    PersonalCargo = 2,
    Equipment = 3,
    Pet = 7,
    AccountCargo = 12,
    GuildMedal = 38
}

internal sealed record AccountCargoState(long Revision = 0, ushort Capacity = 0, int Gold = 0);

internal readonly record struct InventorySlot(InventoryListType List, short Index);
internal sealed record InventoryItem(long Uid, InventorySlot Position, ItemCore Core)
{
    public AvatarDetail? Avatar { get; init; }
    public CreatureDetail? Creature { get; init; }
    public int ExpireTime => Avatar?.ExpireTime ?? Creature?.ExpireTime ?? Core.ExpireTime;
    public bool IsExpired(long utcSeconds) => ExpireTime > 0 && ExpireTime <= utcSeconds;
    public int RemainingSeconds(long utcSeconds) => ExpireTime == 0 ? 0 : (int)Math.Clamp(ExpireTime - utcSeconds, 0, int.MaxValue);
    public void ValidateDetails(bool allocating)
    {
        if ((Core.Kind == DfoServer.GameWorld.Items.ItemKind.Avatar) != (Avatar is not null) ||
            (Core.Kind == DfoServer.GameWorld.Items.ItemKind.Creature) != (Creature is not null) ||
            (Avatar is not null || Creature is not null) && (Core.ExpireTime != 0 || Core.Value != Uid || !allocating && Uid == 0))
            throw new InvalidDataException("特殊物品核心与详情不一致");
        Avatar?.Validate();
        Creature?.Validate();
    }
}
internal sealed record InventorySnapshot(ImmutableArray<InventoryItem> Items, ushort Expansion = 24, uint Gold = 0)
{
    public static InventorySnapshot Empty { get; } = new([]);
    public int ReviveCoins { get; init; }
    public int WinPoints { get; init; }
    public int GoldCarryLimit { get; init; }
    public int AuctionGoldLimit { get; init; } = CurrencyService.BaseAuctionLimit;
    public AccountWallet AccountWallet { get; init; } = AccountWallet.Empty;
    public ushort CargoCapacity { get; init; } = 8;
    public AccountCargoState AccountCargo { get; init; } = new();
    public ImmutableArray<ShopPurchaseCount> ShopPurchases { get; init; } = [];
    public EpicPieceBook EpicPieces { get; init; } = EpicPieceBook.Empty;
    public InventoryItem? Find(InventorySlot slot) => Items.SingleOrDefault(item => item.Position == slot);
    public void Validate(bool allocating = false)
    {
        AccountWallet.Validate();
        EpicPieces.Validate();
        if (ShopPurchases.IsDefault || ShopPurchases.Any(row => row.NpcId < 0 || row.ItemId <= 0 || row.Count <= 0 || row.Day < 0) ||
            ShopPurchases.Select(row => (row.NpcId, row.ItemId, row.AccountWide, row.ResetDaily)).Distinct().Count() != ShopPurchases.Length)
            throw new InvalidDataException("无效商店限购快照");
        if (ReviveCoins < 0 || WinPoints < 0 || GoldCarryLimit < 0 || AuctionGoldLimit < 0 ||
            CargoCapacity is < 8 or > 264 || AccountCargo.Capacity > 240 || AccountCargo.Revision < 0 || AccountCargo.Gold < 0 ||
            Expansion is not (0 or 8 or 16 or 24) || Items.IsDefault ||
            Items.Select(item => item.Position).Distinct().Count() != Items.Length ||
            Items.Any(item => item.Uid < 0 || !allocating && item.Uid == 0) ||
            Items.Where(item => item.Uid != 0).Select(item => item.Uid).Distinct().Count() != Items.Count(item => item.Uid != 0))
            throw new InvalidDataException("无效背包快照");
        foreach (var item in Items)
        {
            item.Core.Validate();
            item.ValidateDetails(allocating);
            if (!ItemSlotBoundService.Allows(this, item.Position, item.Core.Kind))
                throw new InvalidDataException("物品容器或槽位不合法");
        }
    }
}

// Source/Destination 保留请求顺序；卸装时 Source 是空背包格，Destination 是穿戴格。
internal sealed record InventoryMoveRequest(InventorySlot Source, int SourceItemId, int Count,
    InventorySlot Destination, int DestinationItemId, int DestinationCount, int Actor, byte Flag0, byte Flag1);
internal sealed record InventorySortRequest(InventoryListType List, DfoServer.GameWorld.Items.ItemKind Kind, byte Condition);
internal enum InventoryLockOperation { SortLock, SortUnlock, EquipmentLock, EquipmentUnlock, CancelUnlock }
internal sealed record InventoryDeleteEntry(byte Type, short Slot, int Value, int Count);
internal sealed record InventoryDeleteRequest(InventoryListType List, InventoryDeleteEntry[] Items, int Flag)
{
    public byte? ClientState { get; init; }
}
// 移动资格必须属于生成计划时的角色；不能把旧计划重新绑定到刷新后的expected。
internal sealed class InventoryCharacterPrecondition(Character character)
{
    readonly ushort id = character.Id;
    readonly long revision = character.Revision;
    readonly byte job = character.Job;
    readonly byte grow = character.GrowType;
    readonly byte level = character.Level;
    readonly ImmutableArray<CharacterSkill> skills = character.Skills.OrderBy(skill => skill.Id).ToImmutableArray();

    public bool Matches(Character value) => value.Id == id && value.Revision == revision && value.Job == job &&
        value.GrowType == grow && value.Level == level && skills.SequenceEqual(value.Skills.OrderBy(skill => skill.Id));
}

internal sealed record InventoryResult(bool Accepted, ushort Error, InventoryPlan? Plan = null, int MoveCount = 0,
    InventorySlot? MoveSource = null, InventorySlot? MoveDestination = null)
{
    public InventoryCommitReceipt? Receipt { get; init; }
    public static InventoryResult Reject(ushort error = 1) => new(false, error);
}
