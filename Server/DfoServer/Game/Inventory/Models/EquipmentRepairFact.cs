using System.Collections.Immutable;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Models;

internal sealed record EquipmentRepairChange(InventoryItem Before, InventoryItem After);

// 普通修理的实际耐久恢复事实；全修仍是一笔用户行为，不按修复件数扩增次数。
internal sealed record EquipmentRepairFact(Guid Id, DateTimeOffset PlannedAt, TimeProvider Clock,
    RepairEquipmentRequest Request, ImmutableArray<EquipmentRepairChange> Repairs)
{
    public static EquipmentRepairFact? Capture(InventorySnapshot before, InventorySnapshot after,
        RepairEquipmentRequest request, DateTimeOffset at, TimeProvider clock)
    {
        // mode1可只有客户端战损回述和契约用量变化；本切片不据它推定普通修理动作。
        if (request.Mode != 0) return null;
        var index = new InventoryIndex(after);
        var rows = before.Items.Where(item => item.Core.Kind == ItemKind.Equipment)
            .Select(item => new EquipmentRepairChange(item, index.Find(item.Position) ?? item))
            .Where(row => row.After.Core.Durability > row.Before.Core.Durability).ToImmutableArray();
        return rows.IsEmpty ? null : new(Guid.NewGuid(), at, clock, request, rows);
    }

    public bool Matches(InventoryPlan plan, InventorySnapshot inventory)
    {
        if (Id == Guid.Empty || Request.Mode != 0 || Repairs.IsDefaultOrEmpty ||
            Repairs.Select(row => row.Before.Uid).Distinct().Count() != Repairs.Length) return false;
        var index = new InventoryIndex(inventory);
        foreach (var row in Repairs)
        {
            var old = row.Before; var after = row.After;
            if (old.Uid <= 0 || old.Core.Kind != ItemKind.Equipment || plan.BeforeIndex.Find(old.Position) != old ||
                after.Core.Durability <= old.Core.Durability || index.FindUid(old.Uid) != after ||
                after != (old with { Core = old.Core with { Durability = after.Core.Durability } })) return false;
        }
        // 绑定本次全部实际恢复目标，保持UID和完整核心；不重算原资源定价或修理算法。
        return inventory.Items.Count(item => plan.BeforeIndex.FindUid(item.Uid) is { Core.Kind: ItemKind.Equipment } old &&
            item.Core.Durability > old.Core.Durability) == Repairs.Length;
    }
}
