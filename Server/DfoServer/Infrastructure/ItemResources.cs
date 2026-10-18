using DfoServer.GameWorld.Items;
using PvfLib;
using DfoServer.GameWorld.Dungeons;

namespace DfoServer.Infrastructure;

// 仅用于启动装配及测试夹具；业务服务直接接收实际使用的目录。
internal sealed class ItemResources
{
    public ItemCatalog Items { get; }
    public GoldLimitCatalog GoldLimits { get; }
    public CargoCatalog Cargo { get; }
    public NpcShopCatalog Shops { get; }
    public EquipmentRepairCatalog Repair { get; }
    public EquipmentRefinementCatalog Refinement { get; }
    public RandomOptionCatalog RandomOptions { get; }
    public DisjointCatalog Disjoint { get; }
    public AvatarDisjointCatalog AvatarDisjoint { get; }
    public EpicPieceCatalog EpicPieces { get; }
    public BoosterCatalog Boosters { get; }
    public RentalCatalog Rental { get; }
    public CashShopCatalog CashShop { get; }
    public DevilContractCatalog DevilContracts { get; }
    public MagicBoxCatalog MagicBoxes { get; }
    public PremiumContractCatalog Contracts { get; }
    public RecoveryConsumableCatalog RecoveryConsumables { get; }
    public LimitCubeCatalog LimitCubes { get; }
    public EnchantmentCatalog Enchantments { get; }
    public EnhancementCatalog Enhancements { get; }
    public CreatureEggCatalog CreatureEggs { get; }
    public CollectBoxCatalog CollectBoxes { get; }
    public GameWorld.Creatures.CreatureCatalog Creatures { get; }
    public GameWorld.Creatures.CreatureGroupCatalog CreatureGroups { get; }
    public AvatarPackageCatalog AvatarPackages { get; }
    public EmblemCompoundCatalog EmblemCompounds { get; }
    public AvatarCompoundCatalog AvatarCompounds { get; }
    public AvatarDyeCatalog AvatarDyes { get; }
    public TeleportCatalog Teleports { get; }
    readonly Lazy<DungeonItemDropCatalog> dungeonDropItems;
    readonly Lazy<DungeonSpecialDropCatalog> dungeonSpecialDrops;
    public DungeonItemDropCatalog DungeonDropItems => dungeonDropItems.Value;
    public DungeonSpecialDropCatalog DungeonSpecialDrops => dungeonSpecialDrops.Value;

    public ItemResources(PvfArchive archive)
    {
        GoldLimits = new(archive);
        Cargo = new(archive);
        Shops = new(archive);
        Repair = new(archive);
        Refinement = new(archive);
        Disjoint = new(archive);
        AvatarDisjoint = new(archive);
        EpicPieces = new(archive);
        var equipment = PvfScript.List(archive.ReadText("equipment/equipment.lst"));
        var stackable = PvfScript.List(archive.ReadText("stackable/stackable.lst"));
        Creatures = new(archive, equipment, stackable);
        CreatureGroups = new(archive);
        Contracts = PremiumContractCatalog.Load(archive, equipment, stackable);
        Boosters = new(archive, stackable);
        Items = new(archive, equipment, stackable, Shops.EquipmentSellRate,
            Contracts.RecognizedItems.Union(Boosters.DirectActivationItems).ToHashSet(),
            Contracts.ByItem.Where(row => Contracts.TryAcquisitionContract(row.Key, out _)).ToDictionary(), creatures: Creatures);
        CollectBoxes = new(archive, Items);
        CashShop = CashShopCatalog.Load(archive);
        DevilContracts = new(archive, stackable, CashShop);
        RecoveryConsumables = new(archive, stackable, Items);
        LimitCubes = new(archive, stackable, equipment);
        Enchantments = new(archive, stackable);
        Enhancements = new(archive, stackable, equipment);
        CreatureEggs = new(archive, equipment, Items);
        AvatarPackages = new(archive, equipment, stackable);
        EmblemCompounds = new(archive, stackable);
        AvatarCompounds = new(archive);
        AvatarDyes = new(archive, equipment, stackable);
        Teleports = new(archive.ReadText, equipment, stackable);
        RandomOptions = new(archive, equipment);
        dungeonDropItems = new(() => DungeonItemDropCatalog.Load(Items));
        dungeonSpecialDrops = new(() => DungeonSpecialDropCatalog.Load(archive));
        MagicBoxes = new(archive, stackable);
        Rental = RentalCatalog.Load(archive);
    }
}
