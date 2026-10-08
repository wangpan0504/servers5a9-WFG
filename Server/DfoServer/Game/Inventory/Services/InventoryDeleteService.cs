using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Currency;
namespace DfoServer.Game.Inventory.Services;

internal static class InventoryDeleteService
{
    public static InventoryResult PlanBatch(InventorySnapshot before, InventoryDeleteRequest request)
    {
        if (!Allows(request.List) || request.Flag != 0 || request.Items.Length is < 1 or > 352 ||
            request.Items.Any(item => item.Type is not (0 or 2)) || request.Items.Select(item => item.Slot).Distinct().Count() != request.Items.Length)
            return InventoryResult.Reject();
        var after = before;
        foreach (var item in request.Items)
        {
            InventoryResult result;
            if (item.Type == 2)
            {
                // 技能材料必须按正数足额扣除，不能套普通丢弃的整堆归一约定。
                if (request.List != InventoryListType.Main || item.Count <= 0)
                    return InventoryResult.Reject();
                if (item.Slot is >= 354 and <= 365)
                {
                    if (CurrencyWallet.RewardKind(item.Value) is not { } kind || ItemSlotBoundService.CurrencySlot(kind) != item.Slot)
                        return InventoryResult.Reject();
                    result = CurrencyService.Spend(after, kind, item.Count);
                }
                else
                {
                    if (after.Find(new(request.List, item.Slot)) is not { } material || item.Count > material.Core.Count)
                        return InventoryResult.Reject();
                    result = Plan(after, material.Position, item.Value, item.Count);
                }
            }
            else result = Plan(after, new(request.List, item.Slot), item.Value, item.Count);
            if (!result.Accepted)
                return result;
            after = result.Plan!.After;
        }
        return new(true, 0, new(before, after, request.Items.Any(item => item.Type == 2) ? "delete_skill_material" : "delete_item"));
    }
    public static InventoryResult Plan(InventorySnapshot before, InventorySlot position, int value, int count)
    {
        var item = before.Find(position);
        if (!TryDelete(item, value, count, out var remaining))
            return InventoryResult.Reject();
        var after = before with { Items = remaining is null ? before.Items.Remove(item!) : before.Items.Replace(item!, remaining) };
        return new(true, 0, new(before, after, "delete_item"));
    }

    // 单实例规则供批量工作集复用；此处不创建快照，也不改变传入实例。
    public static bool TryDelete(InventoryItem? item, int value, int count, out InventoryItem? remaining)
    {
        remaining = null;
        // 时装的客户端 value 是递减的剩余秒数，不是服务端详情 UID，不能用它校验实例身份。
        if (item is null || !Allows(item.Position.List) || value < 0 || item.Avatar is null && item.Core.Value != value ||
            item.Core.EquipmentLockId != 0)
            return false;
        count = InventoryStackRuleService.NormalizeMoveCount(item.Core, count);
        if (count <= 0)
            return false;
        remaining = count == item.Core.Count ? null : item with { Core = item.Core with { Value = item.Core.Value - count } };
        return true;
    }
    static bool Allows(InventoryListType list) => list is InventoryListType.Main or InventoryListType.Avatar or
        InventoryListType.Pet or InventoryListType.GuildMedal or InventoryListType.Equipment or InventoryListType.PersonalCargo;
}
