using DfoServer.Game.Inventory.Models;

namespace DfoServer.Game.Inventory.Services;

internal static class InventoryStackRuleService
{
    // 排序锁属于位置行为，不阻止收取同类物品；合并保留目标锁，其他实例属性必须一致。
    public static bool CanShareStack(ItemCore source, ItemCore destination) => source.IsStackable && destination.IsStackable &&
        source.EquipmentLockId == 0 && destination.EquipmentLockId == 0 &&
        (source with { Value = destination.Value, SortLockFlag = destination.SortLockFlag }) == destination;

    public static bool CanMerge(ItemCore source, ItemCore destination, int count, int stackLimit) =>
        count > 0 && CanShareStack(source, destination) && (long)destination.Count + count <= stackLimit;

    // 普通装备始终一件；堆叠请求的非正数或超出现有数量表示整堆。
    public static int NormalizeMoveCount(ItemCore source, int requested) => !source.IsStackable ? 1 :
        requested <= 0 || requested > source.Count ? source.Count : requested;
}
