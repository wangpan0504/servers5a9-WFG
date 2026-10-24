using DfoServer.Game.Inventory.Models;

namespace DfoServer.Game.Dungeons;

// #510/#511 普通入场在提交时一次结清的费用：入场票背包计划与入场一次性疲劳，与尝试号预留同一事务。
// Identity/MapId 是本次待安装实例的身份与起始房间，写入疲劳扣点账本。
// Weekly 是本次入场计入的冒险团每周地下城列；没有费用时走普通预留，同样按同一事务累加。
internal sealed record DungeonEntryCharge(InventoryPlan? Items, ushort Fatigue, DungeonRunIdentity Identity, int MapId,
    WeeklyDungeonEntry? Weekly = null, GriefTowerAdmission? Grief = null)
{
    public bool IsEmpty => Items is null && Fatigue == 0 && Grief is null;
}
