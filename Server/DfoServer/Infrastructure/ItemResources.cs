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
    public EpicPieceCatalog EpicPieces { get; }
    public BoosterCatalog Boosters { get; }
    public RentalCatalog Rental { get; }
    public CashShopCatalog CashShop { get; }
    public RecoveryConsumableCatalog RecoveryConsumables { get; }
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
        EpicPieces = new(archive);
        var equipment = PvfScript.List(archive.ReadText("equipment/equipment.lst"));
        var stackable = PvfScript.List(archive.ReadText("stackable/stackable.lst"));
        Items = new(archive, equipment, stackable, Shops.EquipmentSellRate);
        RecoveryConsumables = new(archive, stackable);
        RandomOptions = new(archive, equipment);
        dungeonDropItems = new(() => DungeonItemDropCatalog.Load(Items));
        dungeonSpecialDrops = new(() => DungeonSpecialDropCatalog.Load(archive));
        Boosters = new(archive, stackable);
        CashShop = CashShopCatalog.Load(archive);
        Rental = RentalCatalog.Load(archive);
    }
}
