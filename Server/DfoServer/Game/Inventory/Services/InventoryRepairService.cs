using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Services;

internal sealed class InventoryRepairService(ItemCatalog catalog, EquipmentRepairCatalog repair)
{
    public InventoryResult Repair(Character character, RepairEquipmentRequest request, DateTimeOffset? utcNow = null, bool clientBattleWear = false)
    {
        if (request.Position.List is not (InventoryListType.Main or InventoryListType.Equipment or InventoryListType.PersonalCargo))
            return InventoryResult.Reject(17);
        bool automatic = request.Mode == 1;
        if (request.Mode > 1 || request.RepairItemSlot != -1 || automatic &&
            (request.Position != new InventorySlot(InventoryListType.Main, -1) || request.Quick))
            return InventoryResult.Reject(19);
        var before = character.Inventory;
        var time = utcNow ?? DateTimeOffset.UtcNow;
        var charged = before;
        var planned = before.Items;
        long total = 0;
        long now = time.ToUnixTimeSeconds();
        bool all = request.Position.Index == -1;
        var candidates = all ? before.Items.Where(item => item.Position.List == InventoryListType.Equipment ||
            item.Position.List == InventoryListType.Main && item.Position.Index is >= 3 and <= 8).ToArray() :
            before.Find(request.Position) is { } single ? [single] : Array.Empty<InventoryItem>();
        if (automatic && (!candidates.Any(item => item.Core.Kind == ItemKind.Equipment && (item.Core.Durability == 0 || clientBattleWear) &&
                !item.IsExpired(now) && catalog.Get(item.Core.ItemId) is { Durability: > 0, EquipmentSlot: not (13 or 24 or 30) }) ||
            !DevilContractPolicy.TryConsume(before, 6, 6, time, out charged)))
            return InventoryResult.Reject(19);
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
            if (all && template.EquipmentSlot is 13 or 24 or 30)
                continue;
            if (item.Core.Durability >= template.Durability)
                continue;
            int price = 0;
            if (!automatic && !repair.TryPrice(template, item.Core.Durability, item.Core.Upgrade, request.Quick, out price))
                return InventoryResult.Reject(10);
            total += price;
            if (total > before.Gold)
                return InventoryResult.Reject(10);
            // 修理只恢复耐久；安全锁、排序锁、封装和全部扩展属性原样保留。
            planned = planned.Replace(item, item with { Core = item.Core with { Durability = template.Durability } });
        }
        return new(true, 0, new(before, charged with { Items = planned, Gold = checked(before.Gold - (uint)total) }, "equipment_repair",
            automatic ? new(character, DevilContractPolicy.Expiry(before.AccountWallet, 6)) : null))
            { WholeListRefresh = automatic && clientBattleWear };
    }
}
