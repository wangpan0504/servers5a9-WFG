using System.Collections.Frozen;

namespace DfoServer.GameWorld.Items;

internal sealed class EpicWishCatalog(EpicPieceCatalog pieces, ItemCatalog items)
{
    readonly FrozenSet<int> equipment = pieces.Entries.Select(entry => entry.EquipmentId).ToFrozenSet();

    // 原生祈愿窗口从史诗图鉴名册筛选Lv70以上装备，不要求属于角色职业或已经拥有。
    public bool Contains(int id) => equipment.Contains(id) && items.TryGet(id, out var item) &&
        item.Kind == ItemKind.Equipment && item.Rarity == 4 && item.MinimumLevel >= 70;
}
