using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Services;

internal static class InventoryDurabilityService
{
    public static InventoryResult Plan(Character character, byte slot, ItemTemplate template, DateTimeOffset now)
    {
        var before = character.Inventory;
        var source = before.Find(new(InventoryListType.Equipment, slot));
        if (source is null || source.Core.Kind != ItemKind.Equipment || template.Kind != ItemKind.Equipment ||
            source.Core.ItemId != template.Id || source.IsExpired(now.ToUnixTimeSeconds()))
            return InventoryResult.Reject();
        if (template.Durability == 0 || source.Core.Durability == 0)
            return new(true, 0, new(before, before, "equipment_durability_decrease"));
        // C2S48只报告穿戴槽，每次合法报告扣一格；安全锁不妨碍已穿戴装备的正常损耗。
        var worn = source with { Core = source.Core with { Durability = checked((ushort)(source.Core.Durability - 1)) } };
        return new(true, 0, new(before, before with { Items = before.Items.Replace(source, worn) }, "equipment_durability_decrease"));
    }
}
