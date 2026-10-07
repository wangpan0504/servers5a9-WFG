using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Services;

internal sealed class InventoryRepairService(ItemCatalog catalog, EquipmentRepairCatalog repair)
{
    public InventoryResult Repair(Character character, RepairEquipmentRequest request, DateTimeOffset? utcNow = null)
    {
        if (request.Position.List is not (InventoryListType.Main or InventoryListType.Equipment or InventoryListType.PersonalCargo))
            return InventoryResult.Reject(17);
        // 契约次数和修理券尚未接入；不能把请求标志当免费资格，也不能忽略券槽后改扣金币。
        if (request.Mode != 0 || request.RepairItemSlot != -1)
            return InventoryResult.Reject(19);
        var before = character.Inventory;
        var planned = before.Items;
        long total = 0;
        long now = (utcNow ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
        bool all = request.Position.Index == -1;
        var candidates = all ? before.Items.Where(item => item.Position.List == InventoryListType.Equipment ||
            item.Position.List == InventoryListType.Main && item.Position.Index is >= 3 and <= 8).ToArray() :
            before.Find(request.Position) is { } single ? [single] : Array.Empty<InventoryItem>();
        if (!all && candidates.Length == 0)
            return InventoryResult.Reject(17);
        foreach (var item in candidates)
        {
            if (item.Core.Kind != ItemKind.Equipment || item.Core.IsExpired(now))
            {
                if (!all)
                    return InventoryResult.Reject(19);
                continue;
            }
            var template = catalog.Get(item.Core.ItemId);
            // 一键修理覆盖普通穿戴与快捷装备，不把背包装备页、仓库或特殊盾牌一起收费。
            if (all && template.EquipmentSlot is 13 or 24)
                continue;
            if (item.Core.Durability >= template.Durability)
                continue;
            if (!repair.TryPrice(template, item.Core.Durability, item.Core.Upgrade, request.Quick, out int price))
                return InventoryResult.Reject(10);
            total += price;
            if (total > before.Gold)
                return InventoryResult.Reject(10);
            // 修理只恢复耐久；安全锁、排序锁、封装和全部扩展属性原样保留。
            planned = planned.Replace(item, item with { Core = item.Core with { Durability = template.Durability } });
        }
        return new(true, 0, new(before, before with { Items = planned, Gold = checked(before.Gold - (uint)total) }, "equipment_repair"));
    }
}
