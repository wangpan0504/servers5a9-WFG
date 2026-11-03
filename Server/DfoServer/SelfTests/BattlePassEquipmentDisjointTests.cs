using DfoServer.Game.BattlePass;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.BattlePass;
using DfoServer.GameWorld.Items;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class BattlePassEquipmentDisjointTests
{
    public static void RunCore()
    {
        int before = Count;
        var at = new DateTimeOffset(2026, 10, 29, 7, 0, 0, TimeSpan.FromHours(8));
        var source = new InventoryItem(17, new(InventoryListType.Main, 3), new(10400, ItemKind.Equipment, 0) { Marker16 = 3719 });
        var other = source with { Uid = 19, Position = new(InventoryListType.Main, 9) };
        var initial = new InventorySnapshot([source, other], Gold: 1000);
        var paid = initial with { Items = [other] };
        var after = CurrencyWallet.Set(CurrencyWallet.Set(paid, CurrencyKind.ClearCube, 1), CurrencyKind.CommonSoul, 2);
        var request = new DisjointRequest(source.Position, -1, 0);
        var receipt = new DisjointReceipt(request, [new(0, 3037, 1), new(0, 10100115, 2)]);
        var fact = EquipmentDisjointFact.Capture(initial, paid, after, request, receipt, DisjointMode.Ordinary,
            "equipment_disjoint", source, null, at, TimeProvider.System);
        var plan = new InventoryPlan(initial, after, "equipment_disjoint") { EquipmentDisjoint = fact };
        That(fact.Matches(plan, after), "实际一件装备删除且完整钱包产物冻结，不按产物数量扩次");
        That(ReferenceEquals(plan.WithAfter(after).EquipmentDisjoint, fact), "WithAfter保原装备分解事实与GUID");
        That(!(fact with { Id = Guid.Empty }).Matches(plan, after) && !(fact with { Reason = "sell" }).Matches(plan, after), "空来源或出售不能冒分解");
        That(!(fact with { Source = source with { Uid = 23 } }).Matches(plan, after) &&
            !(fact with { Source = source with { Core = source.Core with { Marker16 = 0 } } }).Matches(plan, after), "原独立UID及完整源core必须匹配");
        That(!(fact with { Source = source with { Core = new(10400, ItemKind.Avatar, 0) } }).Matches(plan, after), "装扮分解不冒装备分解");
        That(!fact.Matches(plan, after with { Items = [other, source] }), "源实例未实际删除拒绝");
        That(!fact.Matches(plan, CurrencyWallet.Set(after, CurrencyKind.CommonSoul, 1)), "任一真实钱包产物数量不匹配拒绝");
        That(!fact.Matches(plan, after with { Items = [other with { Core = other.Core with { Marker16 = 0 } }] }), "未参与实例完整核心不丢失");
        That(!fact.Matches(plan.WithAfter(after with { Gold = 999 }), after), "未冻结After不能借事实另扣费用");
        var changedReceipt = receipt with { Rewards = [new(0, 3037, 2), new(0, 10100115, 2)] };
        That(!(fact with { Receipt = changedReceipt }).Matches(plan, after), "冻结原全部产品回执，不能事后换数量");
        var tool = new InventoryItem(23, new(InventoryListType.Main, 165), new(2600468, ItemKind.Consumable, 2));
        var portableBefore = initial with { Items = [source, other, tool] };
        var portablePaid = portableBefore with { Items = [other, tool with { Core = tool.Core with { Value = 1 } }] };
        var portableAfter = CurrencyWallet.Set(CurrencyWallet.Set(portablePaid, CurrencyKind.ClearCube, 1), CurrencyKind.CommonSoul, 2);
        var portableRequest = request with { ToolSlot = tool.Position.Index };
        var portableReceipt = receipt with { Request = portableRequest };
        var portable = EquipmentDisjointFact.Capture(portableBefore, portablePaid, portableAfter, portableRequest, portableReceipt,
            DisjointMode.Ordinary, "equipment_disjoint", source, tool, at, TimeProvider.System);
        That(portable.Matches(new(portableBefore, portableAfter, "equipment_disjoint"), portableAfter), "便携原工具恰扣一张但仍只一件装备来源");
        That(!(portable with { Tool = null }).Matches(new(portableBefore, portableAfter, "equipment_disjoint"), portableAfter), "便携请求不能漏实际工具");
        var machinePaid = paid with { Gold = 990 };
        var machineAfter = after with { Gold = 990 };
        var machine = EquipmentDisjointFact.Capture(initial, machinePaid, machineAfter, request, receipt, DisjointMode.OwnMachine,
            "expert_job_machine_disjoint", source, null, at, TimeProvider.System);
        That(machine.Matches(new(initial, machineAfter, "expert_job_machine_disjoint"), machineAfter), "自机最终计划绑定原系统费用和完整产物");
        That(!(machine with { GoldCost = 0 }).Matches(new(initial, machineAfter, "expert_job_machine_disjoint"), machineAfter), "机器系统费用不能丢失");
        var otherMachine = machine with { Mode = DisjointMode.OtherMachine };
        var original = new InventoryPlan(initial, machineAfter, "expert_job_machine_disjoint") { EquipmentDisjoint = otherMachine };
        var tradeAfter = machineAfter with { Gold = 983 };
        var trade = otherMachine.ForTrade(original, initial, tradeAfter, 7);
        var customerPlan = new InventoryPlan(initial, tradeAfter, "expert_job_trade_customer") { EquipmentDisjoint = trade };
        That(trade.Matches(customerPlan, tradeAfter) && trade.GoldCost == 17 && trade.Id == otherMachine.Id, "他机原顾客来源保GUID并恰叠原摊位费，不给店主计分");
        That(!trade.Matches(new(initial, tradeAfter, "expert_job_trade_owner"), tradeAfter), "店主收入计划不能接顾客分解来源");
        That(!trade.Matches(new(initial with { Items = [other] }, tradeAfter, "expert_job_trade_customer"), tradeAfter), "另一顾客快照不能替代实际装备所有者");
        var reward = new InventoryItem(0, new(InventoryListType.Main, 165), new(1001, ItemKind.Consumable, 3));
        var itemAfter = paid with { Items = [other, reward] };
        var itemReceipt = new DisjointReceipt(request, [new(165, 1001, 3)]);
        var itemFact = EquipmentDisjointFact.Capture(initial, paid, itemAfter, request, itemReceipt, DisjointMode.Ordinary,
            "equipment_disjoint", source, null, at, TimeProvider.System);
        var actual = itemAfter with { Items = [other, reward with { Uid = 29 }] };
        That(itemFact.Matches(new(initial, itemAfter, "equipment_disjoint"), actual), "实体产物新UID由原writer赋值且完整core保留");
        That(!itemFact.Matches(new(initial, itemAfter, "equipment_disjoint"), itemAfter) &&
            !itemFact.Matches(new(initial, itemAfter, "equipment_disjoint"), actual with { Items = [other, reward with { Uid = source.Uid }] }), "产物不得保持零UID或重用被分解装备UID");
        foreach (long revision in new long[] { 0, 7 })
        {
            var oldBook = revision == 0 ? EpicPieceBook.Empty : new(revision, new string('A', 64), [40, 9]);
            var pieceBefore = initial with { EpicPieces = oldBook };
            var piecePaid = paid with { EpicPieces = oldBook };
            var pieceAfter = after with { EpicPieces = new(revision, new string('A', 64), [140, 9]) };
            var pieceFact = EquipmentDisjointFact.Capture(pieceBefore, piecePaid, pieceAfter, request, receipt,
                DisjointMode.Ordinary, "equipment_disjoint", source, null, at, TimeProvider.System);
            var piecePlan = new InventoryPlan(pieceBefore, pieceAfter, "equipment_disjoint") { EquipmentDisjoint = pieceFact };
            var committed = pieceAfter with { EpicPieces = pieceAfter.EpicPieces with { Revision = revision + 1 } };
            That(pieceFact.Matches(piecePlan, committed), "首次与已有图鉴提交恰递增一版且保留冻结余额");
            That(!pieceFact.Matches(piecePlan, pieceAfter) && !pieceFact.Matches(piecePlan,
                committed with { EpicPieces = committed.EpicPieces with { Revision = revision + 2 } }), "碎片变化不接受未递增或跳版");
            That(!pieceFact.Matches(piecePlan, committed with { EpicPieces = committed.EpicPieces with { OrderHash = new string('B', 64) } }) &&
                !pieceFact.Matches(piecePlan, committed with { EpicPieces = committed.EpicPieces with { Counts = [140, 10] } }),
                "完整图鉴顺序与非目标碎片余额仍严格比对");
        }
        var daily = new BattlePassMission(18, 3, 100, 17, "", -1);
        var weekly = new BattlePassMission(42, 9, 1000, 17, "", -1);
        That(BattlePassEquipmentDisjointSource.Matches(daily) && BattlePassEquipmentDisjointSource.Matches(weekly), "精确原日18及周42定义");
        That(!BattlePassEquipmentDisjointSource.Matches(daily with { RawTypeCode = 42 }) &&
            !BattlePassEquipmentDisjointSource.Matches(daily with { RawParameter = 1 }) &&
            !BattlePassEquipmentDisjointSource.Matches(weekly with { RequiredCount = 10 }) &&
            !BattlePassEquipmentDisjointSource.Matches(daily with { Experience = 1000 }), "不泛化装扮分解或未来同码变体");
        Console.WriteLine($"PASS BattlePassEquipmentDisjointTests core ({Count - before} checks)");
    }
}
