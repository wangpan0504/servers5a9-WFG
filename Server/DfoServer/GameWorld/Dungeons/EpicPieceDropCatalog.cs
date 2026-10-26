using System.Collections.Immutable;
using DfoServer.GameWorld.Items;

namespace DfoServer.GameWorld.Dungeons;

internal sealed record EpicPieceDropEntry(EpicPieceEntry Piece, ItemTemplate Equipment, ItemTemplate Fragment);

// 碎片自身通常是1级材料；掉落等级及部位必须来自piece list对应的装备，不能按STK等级选池。
internal sealed class EpicPieceDropCatalog
{
    public ImmutableArray<EpicPieceDropEntry> Entries { get; }
    public IReadOnlyDictionary<int, EpicPieceDropEntry> ByEquipment { get; }

    public EpicPieceDropCatalog(EpicPieceCatalog pieces, Func<int, ItemTemplate> resolve)
    {
        Entries = pieces.Entries.Select(piece =>
        {
            var equipment = resolve(piece.EquipmentId);
            var fragment = resolve(piece.PieceId);
            if (equipment.Id != piece.EquipmentId || equipment.Kind != ItemKind.Equipment || equipment.Rarity != 4 ||
                equipment.MinimumLevel == 0 || fragment.Id != piece.PieceId || fragment.Kind != ItemKind.Material ||
                fragment.StackLimit <= 0 || !fragment.Expiration.SupportsCreation)
                throw new InvalidDataException("史诗碎片映射不是完整的史诗装备与可创建材料: " + piece.PieceId);
            return new EpicPieceDropEntry(piece, equipment, fragment);
        }).ToImmutableArray();
        ByEquipment = Entries.ToDictionary(row => row.Piece.EquipmentId);
    }

    public DungeonItemDropPool Pool(int minimumEquipmentLevel, int maximumEquipmentLevel) => new(
        Entries.Where(row => row.Equipment.MinimumLevel >= minimumEquipmentLevel && row.Equipment.MinimumLevel <= maximumEquipmentLevel &&
            row.Fragment.CreationRate > 0).Select(row => new DungeonWeightedDropItem(row.Fragment, row.Fragment.CreationRate)));
}
