using DfoServer.Game.BattlePass;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.BattlePass;
using DfoServer.GameWorld.Items;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class BattlePassEmblemTests
{
    public static void RunCore()
    {
        int before = Count;
        var at = new DateTimeOffset(2026, 10, 29, 7, 0, 0, TimeSpan.FromHours(8));
        var source = new InventoryItem(17, new(InventoryListType.Main, 289), new(2050, ItemKind.Emblem, 2) { Marker16 = 3719 });
        var other = new InventoryItem(19, new(InventoryListType.Main, 3), new(10400, ItemKind.Equipment, 0) { Marker16 = 111 });
        var initial = new InventorySnapshot([source, other], Gold: 1000);
        var paid = initial with { Items = [other] };
        var reward = new InventoryItem(0, source.Position, new(2520022, ItemKind.Emblem, 1) { Marker16 = 552 });
        var after = paid with { Items = [other, reward] };
        var actual = after with { Items = [other, reward with { Uid = 31 }] };
        var request = new EmblemCompoundRequest([new(2050, 289), new(2050, 289)]);
        var receipt = new EmblemCompoundReceipt(10095581, 1, 2, 2520022, 1, false);
        var fact = new EmblemCompoundFact(Guid.NewGuid(), at, TimeProvider.System, request, receipt,
            [new(source, 2, 1)], new(2520022, 2, 2), initial, paid, after);
        var plan = new InventoryPlan(initial, after, "emblem_compound") { EmblemCompound = fact };
        That(fact.Matches(plan, actual), "原槽替换绑定完整扣源和随机结果，允许writer为奖励赋新UID");
        That(ReferenceEquals(plan.WithAfter(after).EmblemCompound, fact) && plan.WithAfter(after).EmblemCompound!.Id == fact.Id,
            "WithAfter保持同一徽章事实和GUID，不重抽奖励");
        That(!fact.Matches(plan, after), "尚未分配的奖励UID不能作为已提交来源");
        That(!fact.Matches(plan, actual with { Items = [other, actual.Items[1] with { Uid = source.Uid }] }), "新奖励不得复用本笔已删除源UID");
        That(!fact.Matches(plan, actual with { Items = [other, actual.Items[1] with { Core = reward.Core with { Marker16 = 0 } }] }), "奖励完整扩展核心必须和原冻结结果相同");
        That(!fact.Matches(plan, actual with { Items = [other with { Uid = 20 }, actual.Items[1]] }), "未参与合成的原UID也须完整保持");
        That(!(fact with { Id = Guid.Empty }).Matches(plan, actual) &&
            !(fact with { Materials = [new(source, 1, 1)] }).Matches(plan, actual), "空来源GUID和少扣一枚来源被拒");
        That(!(fact with { Receipt = receipt with { InputCount = 3 } }).Matches(plan, actual) &&
            !(fact with { Receipt = receipt with { RewardCount = 2 } }).Matches(plan, actual), "原请求投入数量与原随机奖数量不得伪改");
        That(!(fact with { Materials = [new(source, 2, 1), new(source, 2, 1)] }).Matches(plan, actual), "重复输入源不能扩大合成次数或伪造扣源");
        That(!fact.Matches(plan.WithAfter(after with { Gold = 999 }), actual), "复制计划不能以新扣费或另一个After复用旧事实");
        var retained = source with { Core = source.Core with { Value = 3 } };
        var sameBefore = initial with { Items = [retained, other] };
        var samePaid = sameBefore with { Items = [retained with { Core = retained.Core with { Value = 1 } }, other] };
        var sameAfter = sameBefore with { Items = [source, other] };
        var same = fact with { Receipt = receipt with { RewardItemId = 2050 }, Reward = new(2050, 1, 1),
            Materials = [new(retained, 2, 1)], Before = sameBefore, Paid = samePaid, After = sameAfter };
        That(same.Matches(new(sameBefore, sameAfter, "emblem_compound"), sameAfter), "同ID奖励叠回输入剩余，最终净减一也仍是一次真实合成");
        var existingReward = reward with { Uid = 51, Position = new(InventoryListType.Main, 290), Core = reward.Core with { Value = 4 } };
        var stackBefore = initial with { Items = [source, other, existingReward] };
        var stackPaid = stackBefore with { Items = [other, existingReward] };
        var stackAfter = stackPaid with { Items = [other, existingReward with { Core = existingReward.Core with { Value = 5 } }] };
        var stack = fact with { Before = stackBefore, Paid = stackPaid, After = stackAfter };
        That(stack.Matches(new(stackBefore, stackAfter, "emblem_compound"), stackAfter), "不同ID奖励叠入现有原UID，未新分配UID仍可计来源");
        var second = source with { Uid = 53, Position = new(InventoryListType.Main, 290), Core = source.Core with { Value = 1 } };
        var mixedBefore = initial with { Items = [source, other, second] };
        var mixed = fact with { Request = new([new(2050, 289), new(2050, 289), new(2050, 290)]),
            Receipt = receipt with { InputCount = 3 }, Materials = [new(source, 2, 1), new(second, 1, 2)], Before = mixedBefore };
        That(mixed.Matches(new(mixedBefore, after, "emblem_compound"), actual), "多堆混级输入按原最低grade，三枚仍一笔操作");
        var daily = new BattlePassMission(20, 1, 100, 19, "", -1);
        That(BattlePassEmblemSource.Matches(daily, fact) && BattlePassEmblemSource.Matches(new(44, 3, 1000, 19, "", -1), fact) &&
            BattlePassEmblemSource.Matches(new(65, 20, 1600, 19, "", -1), fact), "三种原raw19尝试定义精确接线");
        That(!BattlePassEmblemSource.Matches(daily with { Id = 71 }, fact) && !BattlePassEmblemSource.Matches(daily with { RequiredCount = 2 }, fact) &&
            !BattlePassEmblemSource.Matches(daily with { RawParameter = 0 }, fact), "未来同raw定义或原目标参数变化不被泛化");
        var upgrade2 = new BattlePassMission(66, 2, 1600, 20, "", 2);
        var upgrade3 = new BattlePassMission(67, 2, 1600, 20, "", 3);
        That(BattlePassEmblemSource.Matches(upgrade2, fact) && !BattlePassEmblemSource.Matches(upgrade3, fact), "最低闪耀1到实际华丽2只匹配66");
        var next = fact with { Receipt = receipt with { MinimumGrade = 2 }, Reward = new(2520022, 3, 3) };
        That(BattlePassEmblemSource.Matches(upgrade3, next) && !BattlePassEmblemSource.Matches(upgrade2, next), "最低华丽2到实际灿烂3只匹配67");
        That(!BattlePassEmblemSource.Matches(upgrade2, same) && BattlePassEmblemSource.Matches(daily, same), "同品级成功合成只计尝试，不冒升级");
        That(!BattlePassEmblemSource.Matches(upgrade2, fact with { Reward = new(2520022, 3, 3) }) &&
            !BattlePassEmblemSource.Matches(upgrade3, fact with { Reward = new(2520022, 3, 3) }), "最低1直接出3不冒两种恰升一级");
        That(!BattlePassEmblemSource.Matches(upgrade2, fact with { Reward = new(2520022, 4, 6) }) &&
            !BattlePassEmblemSource.Matches(upgrade3, next with { Reward = new(2520022, 4, 6) }), "白金4/6不冒华丽或灿烂");
        That(!BattlePassEmblemSource.Matches(upgrade2 with { RawParameter = 3 }, fact) &&
            !BattlePassEmblemSource.Matches(upgrade2 with { Experience = 100 }, fact) &&
            !BattlePassEmblemSource.Matches(upgrade2, fact with { Reward = new(2520022, 2, 6) }), "升档定义原参数XP及真实目标稀有度都需匹配");
        var repairAfter = initial with { Items = [source, other with { Core = other.Core with { Durability = 1 } }] };
        var repair = EquipmentRepairFact.Capture(initial, repairAfter, new(other.Position, -1, 0, false), at, TimeProvider.System)!;
        var material = new InventoryItem(61, new(InventoryListType.Main, 121), new(3242, ItemKind.Material, 4));
        var enhancement = new EquipmentEnhancementFact(Guid.NewGuid(), at, TimeProvider.System, OrdinaryEnhancementSource.Material,
            material, 1, other, new(EnhancementMode.Reinforce, other.Position, 121, 3, 0, 1, true));
        var triple = new InventoryPlan(initial, after, "emblem_compound") { EmblemCompound = fact, EquipmentRepair = repair, EquipmentEnhancement = enhancement };
        var copied = triple.WithAfter(after);
        That(ReferenceEquals(copied.EmblemCompound, fact) && ReferenceEquals(copied.EquipmentRepair, repair) &&
            ReferenceEquals(copied.EquipmentEnhancement, enhancement), "WithAfter三种既有事实均保原对象与GUID");
        Console.WriteLine($"PASS BattlePassEmblemTests core ({Count - before} checks)");
    }
}
