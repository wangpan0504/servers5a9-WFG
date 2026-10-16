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
    TitleGeneral = 18,
    TitleSpecific = 19,
    TitlePvp = 20,
    TitleDespair = 21,
    TitleEvent = 22,
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
    public CubeContractState CubeContract { get; init; } = CubeContractState.Empty;
    public ushort AvatarExpansion { get; init; } = 105;
    public ushort CargoCapacity { get; init; } = 8;
    public AccountCargoState AccountCargo { get; init; } = new();
    public ImmutableArray<ShopPurchaseCount> ShopPurchases { get; init; } = [];
    public EpicPieceBook EpicPieces { get; init; } = EpicPieceBook.Empty;
    public InventoryItem? Find(InventorySlot slot) => Items.SingleOrDefault(item => item.Position == slot);
    public void Validate(bool allocating = false)
    {
        if (AvatarExpansion > 315 || AvatarExpansion % 7 != 0) throw new InvalidDataException("装扮栏扩展值无效");
        AccountWallet.Validate();
        CubeContract.Validate();
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
internal sealed class InventoryCharacterPrecondition(Character character, long? qualificationExpiresAt = null)
{
    readonly ushort id = character.Id;
    readonly long revision = character.Revision;
    readonly byte job = character.Job;
    readonly byte grow = character.GrowType;
    readonly byte level = character.Level;
    readonly ImmutableArray<CharacterSkill> skills = character.Skills.OrderBy(skill => skill.Id).ToImmutableArray();

    // 时间资格也在提交前复验，不能凭到期前的规划继续穿戴越级装备。
    public bool Matches(Character value, long? utcSeconds = null) => value.Id == id && value.Revision == revision && value.Job == job &&
        value.GrowType == grow && value.Level == level && skills.SequenceEqual(value.Skills.OrderBy(skill => skill.Id)) &&
        (qualificationExpiresAt is null || (utcSeconds ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds()) < qualificationExpiresAt.Value);
}

internal sealed record InventoryResult(bool Accepted, ushort Error, InventoryPlan? Plan = null, int MoveCount = 0,
    InventorySlot? MoveSource = null, InventorySlot? MoveDestination = null)
{
    public InventoryCommitReceipt? Receipt { get; init; }

    /// <summary>
    /// 要求投影把**整个容器**重发一遍（而不是只发变化槽的差量）。
    ///
    /// 用于「客户端 UI 还持有源槽」的场景：拍卖行上架成功后客户端把源槽视为被上架窗口持有，
    /// 只发差量解不开这个残留状态（参考实现 round13 实测：单槽 `0x000E` 刷新不足以解除拖入锁定，
    /// 表现为背包不可操作、无法再次把道具拖进上架框，需重选角色才恢复）。整列重建强制客户端
    /// 丢弃并重新装载该容器，是唯一可靠的解除方式，与仓库扩容等「背包大变动」场景同一口径。
    /// 只有明确需要时才置位：整列刷新的代价是帧体变大，不能当成差量的默认行为。
    /// </summary>
    public bool WholeListRefresh { get; init; }

    public static InventoryResult Reject(ushort error = 1) => new(false, error);
}
