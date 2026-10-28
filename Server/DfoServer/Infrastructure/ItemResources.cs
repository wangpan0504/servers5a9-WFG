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
    public GameWorld.Guilds.GuildCargoCatalog GuildCargo { get; }
    public GameWorld.Guilds.GuildContentCatalog GuildContents { get; }
    public GameWorld.Guilds.GuildHongbaoCatalog GuildHongbao { get; }
    public NpcShopCatalog Shops { get; }
    public EquipmentRepairCatalog Repair { get; }
    public EquipmentTransportCatalog EquipmentTransports { get; }
    public EquipmentResealCatalog EquipmentReseals { get; }
    public EquipmentRefinementCatalog Refinement { get; }
    public RandomOptionCatalog RandomOptions { get; }
    public EquipmentTextureChangeCatalog TextureChanges { get; }
    public EquipmentUpgradeCatalog EquipmentUpgrades { get; }
    public EquipmentTransmissionCatalog EquipmentTransmissions { get; }
    public DarkRevelationCatalog DarkRevelations { get; }
    public EchonEquipmentCatalog Echon { get; }
    public EchonEquipmentCatalog NewEchon { get; }
    public DisjointCatalog Disjoint { get; }
    public AvatarDisjointCatalog AvatarDisjoint { get; }
    public EpicPieceCatalog EpicPieces { get; }
    public EpicPieceShopCatalog? EpicPieceShop { get; }
    public EpicExchangeCatalog EpicExchanges { get; }
    public BoosterCatalog Boosters { get; }
    public RentalCatalog Rental { get; }
    public CashShopCatalog CashShop { get; }
    public DevilContractCatalog DevilContracts { get; }
    public MagicBoxCatalog MagicBoxes { get; }
    public PremiumContractCatalog Contracts { get; }
    public RecoveryConsumableCatalog RecoveryConsumables { get; }
    public UpgradeChronicleCatalog UpgradeChronicles { get; }
    public ChronicleEnchantCatalog ChronicleEnchants { get; }
    public ItemRecipeCatalog ItemRecipes { get; }
    public ItemMakingSkillCatalog ItemMakingSkills { get; }
    public LimitCubeCatalog LimitCubes { get; }
    public EnchantmentCatalog Enchantments { get; }
    public CharacterInvestmentMetadataCatalog CharacterInvestmentMetadata { get; }
    public EnhancementCatalog Enhancements { get; }
    public AmplificationCatalog Amplifications { get; }
    public AmplifyUpgradeCatalog AmplifyUpgrades { get; }
    public MedalUpgradeCatalog MedalUpgrades { get; }
    public CreatureEggCatalog CreatureEggs { get; }
    public CollectBoxCatalog CollectBoxes { get; }
    public GameWorld.Creatures.CreatureCatalog Creatures { get; }
    public GameWorld.Creatures.CreatureGroupCatalog CreatureGroups { get; }
    public AvatarPackageCatalog AvatarPackages { get; }
    public EmblemCompoundCatalog EmblemCompounds { get; }
    public MonsterCardBindCatalog MonsterCardBinds { get; }
    public GuildDungeonCompoundCatalog GuildCompounds { get; }
    public AvatarCompoundCatalog AvatarCompounds { get; }
    public AvatarDyeCatalog AvatarDyes { get; }
    public TeleportCatalog Teleports { get; }
    public SkinStorageCatalog Skins { get; }
    readonly Lazy<ExpertJobScriptCatalog> expertJobs;
    public ExpertJobScriptCatalog ExpertJobs => expertJobs.Value;
    readonly Lazy<DungeonItemDropCatalog> dungeonDropItems;
    readonly Lazy<DungeonItemDropCatalog> dungeonAltarItems;
    readonly Lazy<DungeonSpecialDropCatalog> dungeonSpecialDrops;
    readonly Lazy<EpicPieceDropCatalog> epicPieceDrops;
    public DungeonItemDropCatalog DungeonDropItems => dungeonDropItems.Value;
    public DungeonItemDropCatalog DungeonAltarItems => dungeonAltarItems.Value;
    public DungeonSpecialDropCatalog DungeonSpecialDrops => dungeonSpecialDrops.Value;
    public EpicPieceDropCatalog EpicPieceDrops => epicPieceDrops.Value;

    public ItemResources(PvfArchive archive)
    {
        GoldLimits = new(archive);
        Cargo = new(archive);
        Shops = new(archive);
        Repair = new(archive);
        EquipmentTransports = new(archive);
        EquipmentReseals = new(archive);
        Refinement = new(archive);
        Disjoint = new(archive);
        AvatarDisjoint = new(archive);
        EpicPieces = new(archive);
        EpicPieceShop = archive.Contains(EpicPieceShopCatalog.Path) ? EpicPieceShopCatalog.Load(archive) : null;
        EpicExchanges = new(archive);
        var equipment = PvfScript.List(archive.ReadText("equipment/equipment.lst"));
        var stackable = PvfScript.List(archive.ReadText("stackable/stackable.lst"));
        GuildCargo = new(archive, equipment, stackable);
        GuildContents = new(archive);
        GuildHongbao = new(archive);
        Creatures = new(archive, equipment, stackable);
        CreatureGroups = new(archive);
        Contracts = PremiumContractCatalog.Load(archive, equipment, stackable);
        Boosters = new(archive, stackable, Shops.Stock(EpicExchangeCatalog.MerchantNpcId));
        Echon = new(archive);
        NewEchon = new(PvfLib.CustomAbilityFile.Parse(archive.ReadText("etc/customability/newwarriorscustomability.etc")),
            new([], [], []), PvfScript.List(archive.ReadText("etc/customability/newwarriorscustomability.lst")));
        Items = new(archive, equipment, stackable, Shops.EquipmentSellRate,
            Contracts.RecognizedItems.Union(Boosters.DirectActivationItems).ToHashSet(),
            Contracts.ByItem.Where(row => Contracts.TryAcquisitionContract(row.Key, out _)).ToDictionary(), creatures: Creatures, echon: Echon,
            newEchon: NewEchon);
        epicPieceDrops = new(() => new(EpicPieces, Items.Get));
        expertJobs = new(() => ExpertJobScriptCatalog.Load(archive, Items, stackable));
        CollectBoxes = new(archive, Items);
        CashShop = CashShopCatalog.Load(archive);
        DevilContracts = new(archive, stackable, CashShop);
        RecoveryConsumables = new(archive, stackable, Items);
        UpgradeChronicles = new(archive, stackable, equipment);
        ChronicleEnchants = new(archive, stackable, equipment);
        ItemRecipes = new(archive, stackable);
        ItemMakingSkills = new(archive);
        LimitCubes = new(archive, stackable, equipment);
        Enchantments = new(archive, stackable);
        CharacterInvestmentMetadata = new(archive, equipment, stackable);
        Enhancements = new(archive, stackable, equipment);
        Amplifications = new(archive, stackable, equipment);
        AmplifyUpgrades = new(archive, stackable);
        MedalUpgrades = new(archive);
        CreatureEggs = new(archive, equipment, Items);
        AvatarPackages = new(archive, equipment, stackable);
        EmblemCompounds = new(archive, stackable);
        MonsterCardBinds = new(archive);
        GuildCompounds = new(archive);
        AvatarCompounds = new(archive);
        AvatarDyes = new(archive, equipment, stackable);
        Teleports = new(archive.ReadText, equipment, stackable);
        Skins = new(archive, stackable);
        RandomOptions = new(archive, equipment);
        TextureChanges = new(archive, equipment);
        EquipmentUpgrades = new(archive);
        EquipmentTransmissions = new(archive);
        DarkRevelations = new(archive, equipment);
        dungeonDropItems = new(() => DungeonItemDropCatalog.Load(Items));
        dungeonAltarItems = new(() => DungeonItemDropCatalog.LoadAltar(Items));
        dungeonSpecialDrops = new(() => DungeonSpecialDropCatalog.Load(archive));
        MagicBoxes = new(archive, stackable);
        Rental = RentalCatalog.Load(archive);
    }
}
