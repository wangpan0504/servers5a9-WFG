using DfoServer.Game.BattlePass;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.BattlePass;
using DfoServer.GameWorld.Items;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class BattlePassMonsterCardUpgradeTests
{
    public static void RunCore()
    {
        int before = Count;
        var at = new DateTimeOffset(2026, 10, 29, 7, 0, 0, TimeSpan.FromHours(8));
        var target = new InventoryItem(17, new(InventoryListType.Main, 233), new(1001, ItemKind.Profession, 1) { Marker16 = 3719 });
        var material = target with { Uid = 19, Position = new(InventoryListType.Main, 234) };
        var other = new InventoryItem(23, new(InventoryListType.Main, 3), new(10400, ItemKind.Equipment, 0) { Marker16 = 111 });
        var initial = new InventorySnapshot([target, material, other], Gold: 1000);
        var paid = initial with { Items = [target, other] };
        var upgraded = target with { Core = target.Core with { EnchantUpgrade = 1 } };
        var after = paid with { Items = [upgraded, other] };
        var request = new MonsterCardUpgradeRequest(233, [234]);
        var receipt = new MonsterCardUpgradeReceipt(true, 1001, 1);
        var fact = new MonsterCardUpgradeFact(Guid.NewGuid(), at, TimeProvider.System, request, receipt, target,
            [new(material, 1)], initial, paid, after);
        var plan = new InventoryPlan(initial, after, "monster_card_upgrade") { MonsterCardUpgrade = fact };
        That(fact.Matches(plan, after), "成功一张卡沿原UID升一级并完整扣材，不以耗材推断成功");
        That(ReferenceEquals(plan.WithAfter(after).MonsterCardUpgrade, fact) && plan.WithAfter(after).MonsterCardUpgrade!.Id == fact.Id,
            "WithAfter保原成功事实和GUID，重试不重抽");
        That(!(fact with { Receipt = receipt with { Upgraded = false } }).Matches(plan, after), "概率失败收据不能计升级成功");
        That(!(fact with { Id = Guid.Empty }).Matches(plan, after) &&
            !(fact with { Receipt = receipt with { Level = 2 } }).Matches(plan, after) &&
            !(fact with { Receipt = receipt with { ItemId = 1002 } }).Matches(plan, after), "空GUID、越级或另卡号拒绝");
        That(!fact.Matches(plan, after with { Items = [upgraded with { Uid = 31 }, other] }), "单张升级仍须保留原目标UID");
        That(!fact.Matches(plan, after with { Items = [upgraded with { Core = upgraded.Core with { Marker16 = 0 } }, other] }), "完整目标扩展核心不得丢失");
        That(!(fact with { Materials = [new(material, 2)] }).Matches(plan, after) &&
            !(fact with { Materials = [new(material, 1), new(material, 1)] }).Matches(plan, after), "实际请求数量和每源唯一性完整复验");
        That(!fact.Matches(plan.WithAfter(after with { Gold = 999 }), after), "另一个After不能借成功事实额外扣费");
        var stack = target with { Core = target.Core with { Value = 3 } };
        var stackedBefore = initial with { Items = [stack, material, other] };
        var stackedPaid = stackedBefore with { Items = [stack, other] };
        var remaining = stack with { Core = stack.Core with { Value = 2 } };
        var freshUpgrade = upgraded with { Uid = 0, Position = material.Position };
        var splitAfter = stackedPaid with { Items = [remaining, other, freshUpgrade] };
        var split = fact with { Target = stack, Before = stackedBefore, Paid = stackedPaid, After = splitAfter };
        var splitActual = splitAfter with { Items = [remaining, other, freshUpgrade with { Uid = 31 }] };
        That(split.Matches(new(stackedBefore, splitAfter, "monster_card_upgrade"), splitActual), "整堆只拆一张升级，新UID由原writer赋值");
        That(!split.Matches(new(stackedBefore, splitAfter, "monster_card_upgrade"), splitAfter) &&
            !split.Matches(new(stackedBefore, splitAfter, "monster_card_upgrade"), splitActual with { Items = [remaining, other, freshUpgrade with { Uid = material.Uid }] }),
            "拆出的升级卡不得保持零UID或重用已消耗材料UID");
        var existing = upgraded with { Uid = 41, Position = new(InventoryListType.Main, 235), Core = upgraded.Core with { Value = 4 } };
        var mergeBefore = stackedBefore with { Items = [stack, material, other, existing] };
        var mergePaid = mergeBefore with { Items = [stack, other, existing] };
        var mergeAfter = mergePaid with { Items = [remaining, other, existing with { Core = existing.Core with { Value = 5 } }] };
        var merge = fact with { Target = stack, Before = mergeBefore, Paid = mergePaid, After = mergeAfter };
        That(merge.Matches(new(mergeBefore, mergeAfter, "monster_card_upgrade"), mergeAfter), "升级卡叠入原完整核心相同的堆仍只计一次");
        That(!merge.Matches(new(mergeBefore, mergeAfter, "monster_card_upgrade"), mergeAfter with { Items = [remaining, other with { Uid = 29 }, mergeAfter.Items[2]] }),
            "未参与升级的原实例也必须完整保持");
        var sortedExisting = existing with { Core = existing.Core with { SortLockFlag = 1 } };
        var sortedBefore = mergeBefore with { Items = [stack, material, other, sortedExisting] };
        var sortedPaid = sortedBefore with { Items = [stack, other, sortedExisting] };
        var sortedReward = sortedExisting with { Core = sortedExisting.Core with { Value = 5 } };
        var sortedAfter = sortedPaid with { Items = [remaining, other, sortedReward] };
        var sorted = fact with { Target = stack, Before = sortedBefore, Paid = sortedPaid, After = sortedAfter };
        That(sorted.Matches(new(sortedBefore, sortedAfter, "monster_card_upgrade"), sortedAfter),
            "升级奖励可合入排序锁不同的同级堆，既有堆的原锁和UID保持");
        foreach (var changed in new[] { sortedReward with { Uid = 43 },
            sortedReward with { Position = new(InventoryListType.Main, 236) },
            sortedReward with { Core = sortedReward.Core with { Value = 4 } },
            sortedReward with { Core = sortedReward.Core with { Value = 6 } },
            sortedReward with { Core = sortedReward.Core with { SortLockFlag = 0 } },
            sortedReward with { Core = sortedReward.Core with { EquipmentLockId = 1 } },
            sortedReward with { Core = sortedReward.Core with { Marker16 = 0 } } })
        {
            var changedAfter = sortedAfter with { Items = [remaining, other, changed] };
            var changedFact = sorted with { After = changedAfter };
            That(!changedFact.Matches(new(sortedBefore, changedAfter, "monster_card_upgrade"), changedAfter),
                "异排序锁合堆仍拒绝换UID、换槽、漏发、超发、改锁和改其他完整属性");
        }
        var changedSourceAfter = sortedAfter with { Items = [remaining with { Core = remaining.Core with { SortLockFlag = 1 } }, other, sortedReward] };
        That(!(sorted with { After = changedSourceAfter }).Matches(new(sortedBefore, changedSourceAfter, "monster_card_upgrade"), changedSourceAfter),
            "奖励身份忽略排序锁不允许改动未升级的原目标余堆排序锁");
        var changedFreshAfter = splitAfter with { Items = [remaining, other, freshUpgrade with { Core = freshUpgrade.Core with { SortLockFlag = 1 } }] };
        var changedFreshActual = changedFreshAfter with { Items = [remaining, other, changedFreshAfter.Items[2] with { Uid = 53 }] };
        That(!(split with { After = changedFreshAfter }).Matches(new(stackedBefore, changedFreshAfter, "monster_card_upgrade"), changedFreshActual),
            "新槽奖励仍须精确保留源排序锁，不沿已有堆的合并例外放宽");
        foreach (int count in new[] { 3, 4 })
        {
            var source = target with { Core = target.Core with { Value = count } };
            var sameBefore = initial with { Items = [source, other] };
            var samePaid = sameBefore with { Items = [source with { Core = source.Core with { Value = count - 2 } }, other] };
            var sameAfter = count == 3 ? samePaid with { Items = [upgraded, other] } :
                samePaid with { Items = [target, other, freshUpgrade] };
            var actual = count == 3 ? sameAfter : sameAfter with { Items = [target, other, freshUpgrade with { Uid = 47 }] };
            var same = fact with { Request = new(233, [233, 233]), Target = source, Materials = [new(source, 2)],
                Before = sameBefore, Paid = samePaid, After = sameAfter };
            That(same.Matches(new(sameBefore, sameAfter, "monster_card_upgrade"), actual), "目标同源堆扣两张材料后只升级一张，count=" + count);
        }
        var weekly = new BattlePassMission(45, 3, 1000, 31, "", -1);
        var season = new BattlePassMission(70, 30, 1600, 31, "", -1);
        That(BattlePassMonsterCardUpgradeSource.Matches(weekly) && BattlePassMonsterCardUpgradeSource.Matches(season), "仅原周45与季70精确定义接线");
        That(!BattlePassMonsterCardUpgradeSource.Matches(weekly with { Id = 71 }) &&
            !BattlePassMonsterCardUpgradeSource.Matches(weekly with { RawParameter = 1 }) &&
            !BattlePassMonsterCardUpgradeSource.Matches(season with { RequiredCount = 31 }) &&
            !BattlePassMonsterCardUpgradeSource.Matches(season with { Experience = 1000 }) &&
            !BattlePassMonsterCardUpgradeSource.Matches(weekly with { RawTypeCode = 21 }), "不泛化未来同raw、参数或卡片合成任务");
        Console.WriteLine($"PASS BattlePassMonsterCardUpgradeTests core ({Count - before} checks)");
    }
}
