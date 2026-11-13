using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Characters;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;
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
    GuildMedal = 38,
    AccountCommon = 45,
    Talisman = 46
}

internal sealed record AccountCargoState(long Revision = 0, ushort Capacity = 0, int Gold = 0);

internal sealed record DevilServiceUsage(byte Slot, int Day, byte Used, Guid InstanceId = default, byte Allowance = 0);

internal readonly record struct InventorySlot(InventoryListType List, short Index);
internal sealed record InventoryItem(long Uid, InventorySlot Position, ItemCore Core)
{
    public AvatarDetail? Avatar { get; init; }
    public CreatureDetail? Creature { get; init; }
    public int ExpireTime => Avatar?.ExpireTime ?? Creature?.ExpireTime ?? Core.ExpireTime;
    public bool IsExpired(long utcSeconds) => ExpireTime > 0 && ExpireTime <= utcSeconds;
    public int RemainingSeconds(long utcSeconds) => ExpireTime == 0 ? 0 :
        (int)Math.Clamp((long)ItemExpiration.ForClient(ExpireTime) - utcSeconds, 0, int.MaxValue);
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
    public DfoServer.Game.PrivilegeServices.PrivilegeServiceState PrivilegeService { get; init; } =
        DfoServer.Game.PrivilegeServices.PrivilegeServiceState.Empty;
    public SkinStorageState Skins { get; init; } = SkinStorageState.Empty;
    // 账号皮肤仓只保存账号归属的所有权，选中项始终跟随角色，此副本不写选中字段。
    public SkinStorageState AccountSkins { get; init; } = SkinStorageState.Empty;
    public AvatarPresetState AvatarPresets { get; init; } = AvatarPresetState.Empty;
    public ItemMakingSkillState ItemMakingSkills { get; init; } = ItemMakingSkillState.Empty;
    public ushort AvatarExpansion { get; init; } = 105;
    public ImmutableArray<DevilServiceUsage> DevilUses { get; init; } = [];
    public ImmutableArray<DailyBoosterUsage> DailyBoosterUses { get; init; } = [];
    public ImmutableArray<NpcFavorPoint> NpcFavor { get; init; } = [];
    public ServerItemUsageState ServerItems { get; init; } = ServerItemUsageState.Empty;
    public long StaminaUntil { get; init; }
    public ImmutableArray<ConsumableState> ConsumableStates { get; init; } = [];
    public ImmutableArray<VariousBuffState> VariousBuffs { get; init; } = [];
    public BossSelectionBuff? BossSelectionBuff { get; init; }
    public KnightShieldDeck ShieldDeck { get; init; } = KnightShieldDeck.Empty;
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
        PrivilegeService.Validate();
        Skins.Validate(AccountSkins);
        AccountSkins.Validate();
        AvatarPresets.Validate();
        ItemMakingSkills.Validate();
        DailyBoosterUsage.Validate(DailyBoosterUses);
        NpcFavorPoint.Validate(NpcFavor);
        ServerItems.Validate();
        ConsumableState.Validate(ConsumableStates);
        BossSelectionBuff?.Validate();
        ShieldDeck.Validate();
        if (StaminaUntil < 0 || DevilUses.IsDefault || DevilUses.Any(row => row.Slot > 7 || row.Day < 0 || row.Allowance > 30) ||
            DevilUses.Select(row => row.Slot).Distinct().Count() != DevilUses.Length)
            throw new InvalidDataException("魔王契约次数账本无效");
        EpicPieces.Validate();
        if (ShopPurchases.IsDefault || ShopPurchases.Any(row => row.NpcId < 0 || row.ItemId <= 0 || row.Count <= 0 || row.Day < 0 ||
                !Enum.IsDefined(row.Period) || row.Period != ItemPurchasePeriod.Legacy && (row.NpcId != 0 || row.ResetDaily ||
                    (row.Period == ItemPurchasePeriod.Accumulate ? row.Day != 0 : row.Day == 0 || row.Day > DateOnly.MaxValue.DayNumber ||
                        row.Day != ShopPurchaseCount.PeriodDay(row.Day, row.Period)))) ||
            ShopPurchases.Select(row => (row.NpcId, row.ItemId, row.AccountWide, row.ResetDaily, row.Period)).Distinct().Count() != ShopPurchases.Length)
            throw new InvalidDataException("无效商店限购快照");
        if (ReviveCoins < 0 || WinPoints < 0 || GoldCarryLimit < 0 || AuctionGoldLimit < 0 ||
            CargoCapacity is < 8 or > 264 || AccountCargo.Capacity > 240 || AccountCargo.Revision < 0 || AccountCargo.Gold < 0 ||
            Expansion is not (0 or 8 or 16 or 24) || Items.IsDefault)
            throw new InvalidDataException("无效背包快照");
        var positions = new HashSet<InventorySlot>(Items.Length);
        var uids = new HashSet<long>(Items.Length);
        foreach (var item in Items)
        {
            if (!positions.Add(item.Position) || item.Uid < 0 || !allocating && item.Uid == 0 ||
                item.Uid != 0 && !uids.Add(item.Uid))
                throw new InvalidDataException("无效背包快照");
        }
        foreach (var item in Items)
        {
            item.Core.Validate();
            item.ValidateDetails(allocating);
            if (!ItemSlotBoundService.Allows(this, item.Position, item.Core.Kind))
                throw new InvalidDataException("物品容器或槽位不合法");
            if (item.Position.List == InventoryListType.Talisman && item.Position.Index >= ItemSlotBoundService.EquippedRuneStart &&
                (item.Core.Kind != ItemKind.Rune || item.Core.Count != 1))
                throw new InvalidDataException("装载符文槽必须持有一枚真实符文");
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
internal sealed class InventoryQualificationExpiredException() : InvalidOperationException("库存规划资格期限已过");
internal sealed class InventoryEventRejectedException() : InvalidOperationException("商城活动已关闭、到期或版本变化");

// 移动资格必须属于生成计划时的角色；不能把旧计划重新绑定到刷新后的expected。
internal sealed class InventoryCharacterPrecondition(Character character, long? qualificationExpiresAt = null, TimeProvider? clock = null,
    bool requirePosition = false, bool requireMarriageState = false, long? qualificationExpiresAtMilliseconds = null, Func<bool>? currentQualification = null)
{
    readonly ushort id = character.Id;
    readonly long revision = character.Revision;
    readonly byte job = character.Job;
    readonly byte grow = character.GrowType;
    readonly byte level = character.Level;
    readonly byte equipmentSlots = character.EquipmentSlotFlags;
    readonly ImmutableArray<CharacterSkill> skills = character.Skills.OrderBy(skill => skill.Id).ToImmutableArray();
    readonly DfoServer.GameWorld.Towns.Location? position = requirePosition ? character.Position : null;
    readonly (Guid? Id, byte State)? marriage = requireMarriageState
        ? (character.Marriage?.Id, character.Marriage?.State ?? 0) : null;

    static bool SameSkills(ImmutableArray<CharacterSkill> expected, CharacterSkill[] actual)
    {
        if (expected.Length != actual.Length) return false;
        for (int index = 1; index < actual.Length; index++)
            if (actual[index - 1].Id > actual[index].Id)
                return expected.SequenceEqual(actual.OrderBy(skill => skill.Id));
        return expected.AsSpan().SequenceEqual(actual);
    }

    // 时间资格也在提交前复验，不能凭到期前的规划继续穿戴越级装备。
    public bool MatchesIdentity(Character value) => value.Id == id && value.Revision == revision && value.Job == job &&
        value.GrowType == grow && value.Level == level && value.EquipmentSlotFlags == equipmentSlots &&
        SameSkills(skills, value.Skills) && (position is null || value.Position == position) &&
        (marriage is null || marriage.Value.Id == value.Marriage?.Id && marriage.Value.State == (value.Marriage?.State ?? 0));
    public bool IsExpired(long? utcSeconds = null)
    {
        if (currentQualification?.Invoke() == false) return true;
        if (qualificationExpiresAt is null && qualificationExpiresAtMilliseconds is null) return false;
        if (utcSeconds is { } seconds)
            return qualificationExpiresAt is { } secondsDeadline && seconds >= secondsDeadline ||
                qualificationExpiresAtMilliseconds is { } millisecondsDeadline && (decimal)seconds * 1000 >= millisecondsDeadline;
        // 同一次时钟读取保留临时效果的毫秒边界，不提前取整，也不延长资格。
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        return qualificationExpiresAt is { } deadline && now.ToUnixTimeSeconds() >= deadline ||
            qualificationExpiresAtMilliseconds is { } deadlineMilliseconds && now.ToUnixTimeMilliseconds() >= deadlineMilliseconds;
    }
    // 组合多个移动步骤时取最早到期；只供同一程序集内的业务规划读取。
    internal long? QualificationExpiresAt => qualificationExpiresAt;
    internal DfoServer.GameWorld.Towns.Location? RequiredPosition => position;
    public bool Matches(Character value, long? utcSeconds = null) => MatchesIdentity(value) && !IsExpired(utcSeconds);

    internal InventoryCharacterPrecondition WithQualification(Character expected, long? expiresAt, Func<bool> qualified)
    {
        if (!MatchesIdentity(expected)) throw new InvalidOperationException("不能给旧库存资格重新绑定角色");
        long? deadline = qualificationExpiresAt is null ? expiresAt : expiresAt is null
            ? qualificationExpiresAt : Math.Min(qualificationExpiresAt.Value, expiresAt.Value);
        return new(expected, deadline, clock, requirePosition: position is not null, requireMarriageState: marriage is not null,
            qualificationExpiresAtMilliseconds: qualificationExpiresAtMilliseconds,
            currentQualification: () => currentQualification?.Invoke() != false && qualified());
    }
}

internal sealed record InventoryResult(bool Accepted, ushort Error, InventoryPlan? Plan = null, int MoveCount = 0,
    InventorySlot? MoveSource = null, InventorySlot? MoveDestination = null)
{
    public InventoryCommitReceipt? Receipt { get; init; }
    public string? RejectReason { get; init; }
    public bool MailboxChanged { get; init; }

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
