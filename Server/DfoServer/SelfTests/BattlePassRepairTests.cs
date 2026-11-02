using DfoServer.Game.BattlePass;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.BattlePass;
using DfoServer.GameWorld.Items;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class BattlePassRepairTests
{
    public static void RunCore()
    {
        int before = Count;
        var at = new DateTimeOffset(2026, 10, 29, 7, 0, 0, TimeSpan.FromHours(8));
        var one = new InventoryItem(17, new(InventoryListType.Main, 3), new(10400, ItemKind.Equipment, 0) { Marker16 = 3719 });
        var two = new InventoryItem(31, new(InventoryListType.Equipment, 14), new(10402, ItemKind.Equipment, 0) { Marker16 = 3719 });
        var initial = new InventorySnapshot([one, two], Gold: 1000);
        var changed = initial with { Gold = 821, Items = [one with { Core = one.Core with { Durability = 33 } }, two with { Core = two.Core with { Durability = 33 } }] };
        var request = new RepairEquipmentRequest(new(InventoryListType.Main, -1), -1, 0, false);
        var fact = EquipmentRepairFact.Capture(initial, changed, request, at, TimeProvider.System)!;
        var plan = new InventoryPlan(initial, changed, "core_repair") { EquipmentRepair = fact };
        That(fact is { Repairs.Length: 2 } && fact.Matches(plan, changed), "两真实UID的全修收据仍只有一个来源GUID");
        That(ReferenceEquals(plan.WithAfter(changed).EquipmentRepair, fact) && plan.WithAfter(changed).EquipmentRepair!.Id == fact.Id,
            "同计划复制保留原修理事实和GUID");
        That(EquipmentRepairFact.Capture(initial, initial, request, at, TimeProvider.System) is null,
            "全耐久零变化不制造来源");
        That(EquipmentRepairFact.Capture(initial, changed, request with { Mode = 1 }, at, TimeProvider.System) is null,
            "自动契约clientBattleWear不冒普通用户动作");
        That(!fact.Matches(plan, changed with { Items = changed.Items.SetItem(0, changed.Items[0] with { Uid = 18 }) }),
            "修理目标UID替换拒绝");
        That(!fact.Matches(plan, changed with { Items = changed.Items.SetItem(0,
            changed.Items[0] with { Core = changed.Items[0].Core with { Marker16 = 0 } }) }), "修理完整核心其他字段不得丢失");
        That(!(fact with { Id = Guid.Empty }).Matches(plan, changed) &&
            !(fact with { Repairs = [fact.Repairs[0], fact.Repairs[0]] }).Matches(plan, changed), "空GUID及重复目标不计来源");
        That(!(fact with { Repairs = [fact.Repairs[0]] }).Matches(plan, changed), "来源绑定本次全部实际受修UID");
        var singleAfter = initial with { Items = [changed.Items[0], two] };
        var single = EquipmentRepairFact.Capture(initial, singleAfter, request with { Position = one.Position }, at, TimeProvider.System)!;
        That(single.Repairs.Length == 1 && single.Matches(new(initial, singleAfter, "single"), singleAfter), "真实单修至少一点恢复即可计一笔，免费不误排");
        var cargo = one with { Position = new(InventoryListType.PersonalCargo, 0) };
        var cargoBefore = new InventorySnapshot([cargo]); var cargoAfter = new InventorySnapshot([cargo with { Core = cargo.Core with { Durability = 1 } }]);
        That(EquipmentRepairFact.Capture(cargoBefore, cargoAfter, request with { Position = cargo.Position, Quick = true }, at, TimeProvider.System) is { Repairs.Length: 1 },
            "合法quick/仓库来源不按容器或付费方式收窄");
        That(EquipmentRepairFact.Capture(initial, changed, request with { RepairItemSlot = 65 }, at, TimeProvider.System) is { Repairs.Length: 2 },
            "合法道具全修的真实恢复仍计一笔");
        var daily = new BattlePassMission(19, 1, 100, 18, "", -1); var weekly = new BattlePassMission(47, 3, 1000, 18, "", -1);
        That(BattlePassRepairSource.Matches(daily) && BattlePassRepairSource.Matches(weekly), "仅已核两修理定义接入");
        That(!BattlePassRepairSource.Matches(daily with { Id = 71 }) && !BattlePassRepairSource.Matches(weekly with { RequiredCount = 4 }) &&
            !BattlePassRepairSource.Matches(daily with { RawParameter = 0 }), "未来同raw、目标和参数不泛化计数");
        Console.WriteLine($"PASS BattlePassRepairTests core ({Count - before} checks)");
    }
}
