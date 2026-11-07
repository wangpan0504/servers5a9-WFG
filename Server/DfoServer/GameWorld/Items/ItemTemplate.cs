using System.Collections.Immutable;

namespace DfoServer.GameWorld.Items;

internal enum ItemKind : byte
{
    Equipment = 1,
    Consumable = 2,
    Material = 3,
    Quest = 4,
    Creature = 5,
    CreatureEquipment = 6,
    CreatureConsumable = 7,
    Avatar = 8,
    Profession = 10,
    Emblem = 9,
    GuildMedal = 12,
    GuardianGem = 13,
    Rune = 14,
    Talisman = 11
}

internal sealed record ItemTemplate(int Id, ItemKind Kind, byte EquipmentSlot, byte MinimumLevel,
    ushort Durability, int StackLimit, int Weight, int Rarity, int Grade, string AttachType,
    ushort Jobs, ushort RequiredSkill, string Name, string StackableType = "")
{
    public int AntiEvil { get; init; }
    public bool UsableExpired { get; init; }
    public ImmutableArray<int> PartSetIds { get; init; } = [];
    public decimal ExperiencePercent { get; init; }
    public bool IsQuickSlotEquipment => Kind == ItemKind.Equipment && EquipmentSlot == QuickSlotEquipmentDefinition.EquipmentType;
    public QuickSlotEquipmentDefinition? QuickSlotEquipment { get; init; }
    public int QuestItemDropRateBonus { get; init; }
    public ImmutableArray<EquipmentQuestDropSetBonus> QuestItemDropSetBonuses { get; init; } = [];
    public ItemWorlds UsableWorlds { get; init; } = ItemWorlds.Common;
    public ImmutableHashSet<string> ExperienceForbiddenModules { get; init; } = ImmutableHashSet<string>.Empty;
    public bool CreatureEgg { get; init; }
    public int WeaponSubType { get; init; } = -1;
    public int CreatureSubType { get; init; } = -1;
    public byte CreatureInitialMode { get; init; }
    public string? CreatureName { get; init; }
    public bool IsPremiumService { get; init; }
    public PremiumContract? AcquisitionContract { get; init; }
    public AccountCargoAcquisition? AcquisitionAccountCargo { get; init; }
    public bool HasAcquisitionEffect => AcquisitionContract is not null || PersonalCargoUpgradeTarget > 0 || AcquisitionAccountCargo is not null;
    public bool PvpFreeEquipment { get; init; }
    public bool PersonalCargoForbidden { get; init; }
    public bool AccountCargoForbidden { get; init; }
    public AccountMaterialDefinition? AccountMaterial { get; init; }
    public bool AccountCommonItem { get; init; }
    public bool AccountBoundAfterTrade { get; init; }
    public int AccountBindingTradeLimit { get; init; }
    public ushort PersonalCargoUpgradeTarget { get; init; }
    public bool AdvancesPersonalCargo { get; init; }
    public ItemShopPrice ShopPrice { get; init; } = new(0, 0, [], null);
    public ItemExpiration Expiration { get; init; } = new();
    public bool EpicRoad2019FixedExpirationRestored { get; init; }
    public int RepairPrice { get; init; }
    public int CreationRate { get; init; }
    public EchonEquipmentDefinition? Echon { get; init; }
    public bool DisjointForbidden { get; init; }
    public bool IsOperationHope { get; init; }
    public bool BossDrop { get; init; }
    public bool IsLegacyEquipment { get; init; }
    public bool IsMonsterCard { get; init; }
    public int? MonsterCardBindType { get; init; }
    public bool AmplifyPollutionForbidden { get; init; }
    public int DisjointValue { get; init; }
    public int PortableDisjointLevel { get; init; } = -1;
    public bool UseForbidden { get; init; }
    public bool RequiresPcRoom { get; init; }
    public bool OpensCreatureSkinSlot { get; init; }
    public bool OpensAuraSkinSlot { get; init; }
    public int CharacterSlotMaximum { get; init; }
    public int MaximumLevel { get; init; } = int.MaxValue;
    public int AvatarExpansionStage { get; init; }
    // STK激活的职业与模板副本名单由客户端判断；装备穿戴和本职业候选仍使用AllowsUse。
    public bool AllowsActivation(byte level, byte job) => job < 16 && !UseForbidden && level >= MinimumLevel && level <= MaximumLevel;
    public bool AllowsUse(byte level, byte job) => AllowsActivation(level, job) &&
        (Jobs & (1 << job)) != 0;
    public RefinementTicketDefinition? RefinementTicket { get; init; }
    public EquipmentQualityBox? QualityBox { get; init; }
    public AvatarAbilityChangeDefinition? AvatarAbilityChange { get; init; }
    public EquipmentResealConsumable? ResealConsumable { get; init; }
    public bool GrowthEquipment { get; init; }
    public bool RandomOptionEquipment { get; init; }
    public bool PeriodEquipment { get; init; }
    public AvatarSocketTypes AvatarSockets { get; init; }
    public bool CloneAvatar { get; init; }
    public bool BorrowAvatarAppearance { get; init; }
    public int? AvatarGiftType { get; init; }
    public ushort EmblemTargetSockets { get; init; }
    public ImmutableHashSet<string> GuardianGemEffects { get; init; } = [];
}

internal readonly record struct AvatarSocketTypes(ushort First, ushort Second, ushort Third, ushort Fourth, ushort Fifth)
{
    public AvatarSocketTypes WithDefaults(AvatarSocketTypes defaults) => new(
        First == 0 ? defaults.First : First, Second == 0 ? defaults.Second : Second,
        Third == 0 ? defaults.Third : Third, Fourth == 0 ? defaults.Fourth : Fourth, Fifth == 0 ? defaults.Fifth : Fifth);
}

internal sealed record StarterEquipment(byte Slot, ItemTemplate Template);
