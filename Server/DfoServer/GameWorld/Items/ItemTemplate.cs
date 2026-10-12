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
    GuardianGem = 13
}

internal sealed record ItemTemplate(int Id, ItemKind Kind, byte EquipmentSlot, byte MinimumLevel,
    ushort Durability, int StackLimit, int Weight, int Rarity, int Grade, string AttachType,
    ushort Jobs, ushort RequiredSkill, string Name, string StackableType = "")
{
    public int WeaponSubType { get; init; } = -1;
    public bool PersonalCargoForbidden { get; init; }
    public bool AccountCargoForbidden { get; init; }
    public ushort PersonalCargoUpgradeTarget { get; init; }
    public ItemShopPrice ShopPrice { get; init; } = new(0, 0, [], null);
    public ItemExpiration Expiration { get; init; } = new();
    public int RepairPrice { get; init; }
    public int CreationRate { get; init; }
    public bool DisjointForbidden { get; init; }
    public int DisjointValue { get; init; }
    public int PortableDisjointLevel { get; init; } = -1;
    public bool UseForbidden { get; init; }
    public RefinementTicketDefinition? RefinementTicket { get; init; }
    public AvatarSocketTypes AvatarSockets { get; init; }
    public bool CloneAvatar { get; init; }
    public bool BorrowAvatarAppearance { get; init; }
    public ImmutableHashSet<string> GuardianGemEffects { get; init; } = [];
}

internal readonly record struct AvatarSocketTypes(ushort First, ushort Second, ushort Third, ushort Fourth, ushort Fifth);

internal sealed record StarterEquipment(byte Slot, ItemTemplate Template);
