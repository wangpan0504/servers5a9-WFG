using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Services;

// 自动处理采用比手动操作更保守的资格。只读当前快照，不移动、不销毁、不写存档。
internal static class HelperSafetyPolicy
{
    internal sealed record Candidate(InventoryItem Item, byte Operations);

    internal static Candidate[] Read(Character character, ItemCatalog catalog)
    {
        var registered = character.SkillSwitchInventory.Items.Select(binding => binding.ItemUid).ToHashSet();
        var result = new List<Candidate>();
        foreach (var item in character.Inventory.Items)
        {
            if (item.Position.List != InventoryListType.Main || item.Position.Index < 3 || item.Position.Index > 4095) continue;
            var core = item.Core;
            if (core.EquipmentLockId != 0 || core.SortLockFlag != 0 || core.IsRental || core.ExpireTime != 0) continue;
            ItemTemplate template;
            try { template = catalog.Get(core.ItemId); }
            catch (Exception error) when (error is InvalidDataException or FileNotFoundException or NotSupportedException) { continue; }
            byte operations = Operations(item, template, registered.Contains(item.Uid));
            if (operations != 0) result.Add(new(item, operations));
        }
        return result.OrderBy(row => row.Item.Position.Index).ToArray();
    }
    internal static byte Operations(InventoryItem item, ItemTemplate template, bool registered)
    {
        if (item.Position.List != InventoryListType.Main || item.Position.Index < 3 || item.Position.Index > 4095) return 0;
        var core = item.Core;
        if (core.EquipmentLockId != 0 || core.SortLockFlag != 0 || core.IsRental || core.ExpireTime != 0) return 0;
        if (template.Id != core.ItemId || template.Kind != core.Kind) return 0;
        string attach = template.AttachType.Replace("`", "").Trim().Trim('[', ']').Trim();
        if (core.Kind == ItemKind.Material && core.Count > 0 && core.Count <= template.StackLimit &&
            (attach.Equals("free", StringComparison.OrdinalIgnoreCase) || attach.Equals("account", StringComparison.OrdinalIgnoreCase)) &&
            InventoryCargoService.AllowsInsert(core, InventoryListType.AccountCargo, template))
            return 1;
        if (core.Kind != ItemKind.Equipment || item.Position.Index <= ItemSlotBoundService.QuickSlotEnd || core.Count != 1 || registered ||
            template.EquipmentSlot == 13 || template.DisjointForbidden || core.DisjointForbidden ||
            template.GrowthEquipment || template.PeriodEquipment || core.Upgrade != 0 ||
            core.EnchantCardId != 0 || core.EnchantUpgrade != 0 || HasInvalidAmplification(core) ||
            core.GenuineUpgrade != 0 || core.EmancipateLevel != 0 || core.EmblemSocketCount != 0 ||
            core.Emblem1 != 0 || core.Emblem2 != 0 || core.GuardianGemKeys != 0 || core.Rune != 0 ||
            core.EchonSet is not null || attach.Equals("trade delete", StringComparison.OrdinalIgnoreCase)) return 0;
        return 2;
    }

    // 气息／净化后的基础属性不表示增幅等级；真正强化或增幅共用 Upgrade，已在上方保护。
    static bool HasInvalidAmplification(ItemCore core) => core.AmplifyType == 0
        ? core.AmplifyValue != 0
        : (core.AmplifyType & 0x7F) is < 1 or > 4 || core.AmplifyValue == 0;
}
