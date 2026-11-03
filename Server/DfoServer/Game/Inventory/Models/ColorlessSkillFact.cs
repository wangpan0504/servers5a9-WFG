using DfoServer.Game.BattlePass;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Services;

namespace DfoServer.Game.Inventory.Models;

// 一次技能施法的无色钱包扣除。修炼场免耗不产生事实，装备脚本材料也不借用这条来源。
internal sealed record ColorlessSkillFact(Guid Id, DateTimeOffset PlannedAt, TimeProvider Clock, int Spent)
{
    public static ColorlessSkillFact? Capture(InventorySnapshot before, InventorySnapshot after,
        InventoryDeleteRequest request, bool preserveMaterials, DateTimeOffset at, TimeProvider clock)
    {
        short? slot = ItemSlotBoundService.CurrencySlot(CurrencyKind.ClearCube);
        if (preserveMaterials || slot is not short cubeSlot || request.List != InventoryListType.Main || request.Flag != 0 ||
            request.Items.Any(item => item.Type == 3)) return null;
        var cubes = request.Items.Where(item => item.Type == 2 && item.Slot == cubeSlot &&
            item.Value == BattlePassColorlessSkillSource.ItemId).ToArray();
        if (cubes is not [{ Count: > 0 and var spent }]) return null;
        long beforeBalance = CurrencyWallet.Get(before, CurrencyKind.ClearCube);
        long afterBalance = CurrencyWallet.Get(after, CurrencyKind.ClearCube);
        if (beforeBalance - afterBalance != spent) return null;
        return new(Guid.NewGuid(), at, clock, spent);
    }

    public bool Matches(InventoryPlan plan, InventorySnapshot inventory) =>
        Id != Guid.Empty && Spent > 0 && plan.Reason == "delete_skill_material" && !plan.PreserveMaterials &&
        CurrencyWallet.Get(plan.Before, CurrencyKind.ClearCube) - CurrencyWallet.Get(plan.After, CurrencyKind.ClearCube) == Spent &&
        CurrencyWallet.Get(plan.After, CurrencyKind.ClearCube) == CurrencyWallet.Get(inventory, CurrencyKind.ClearCube);
}
