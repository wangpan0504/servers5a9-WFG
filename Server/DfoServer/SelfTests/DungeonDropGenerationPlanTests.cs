using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class DungeonDropGenerationPlanTests
{
    public static void RunCore()
    {
        int before = Count;
        var stack = new ItemTemplate(1000, ItemKind.Consumable, 0, 1, 0, 3, 1, 0, 1,
            "[free]", ushort.MaxValue, 0, "合成数量模板");
        var pool = new DungeonItemDropPool(new DungeonWeightedDropItem[] { new(stack, 1) });
        var plan = new DungeonDropGenerationPlan("明确合成概率/数量", [new(1000, pool, 7, 7)]);
        var result = plan.Generate(_ => 0, DateTimeOffset.FromUnixTimeSeconds(1800000000));
        That(plan.MaximumObjects == 3 && result.Select(asset => asset.Item!.Count).SequenceEqual(new[] { 3, 3, 1 }),
            "超单堆数量按原上限分成完整地面对象，总量不截断或另抽模板");
        var individual = new DungeonDropGenerationPlan("合成逐个落地", [new(10000, pool, 1, 7, IndividualObjects: true)]);
        That(individual.MaximumObjects == 7 && individual.Generate(bound => bound == 7 ? 6 : 0, DateTimeOffset.UnixEpoch)
            .Select(row => row.Item!.Count).SequenceEqual(Enumerable.Repeat(1, 7)), "可变数量一次抽完后逐个落地，完整容量按物品数预留");
        Reject<InvalidDataException>(() => new DungeonDropGenerationPlan("逐个超容量", [new(10000, pool, 501, 501, IndividualObjects: true)]),
            "材料逐个落地不能通过原堆叠上限绕过500对象容量");
        var miss = plan.Generate(bound => bound - 1, DateTimeOffset.UtcNow);
        That(miss.IsEmpty, "可控随机未命中保持零结果，不通过增加触发率确保掉落");
        var equipment = stack with { Id = 2000, Kind = ItemKind.Equipment, StackLimit = 1, Durability = 41 };
        var equipmentPool = new DungeonItemDropPool(new DungeonWeightedDropItem[] { new(equipment, 7) });
        var gear = new DungeonDropGenerationPlan("明确合成装备数量", [new(1000, equipmentPool, 2, 2)]);
        int quality = 0;
        var generated = gear.Generate(bound => bound == int.MaxValue ? quality++ : 0, DateTimeOffset.UtcNow);
        That(generated.Length == 2 && generated[0].Item!.Value == 0 && generated[1].Item!.Value == 1 &&
            generated.All(asset => asset.Item!.Durability == 41), "多件装备分别创建品级种子和完整Core，不用一个实例重复授予");
        Reject<InvalidDataException>(() => new DungeonDropGenerationPlan("容量fixture", [new(1000, equipmentPool, 501, 501)]),
            "合法通知无法容纳时拒绝完整声明，不能只保留500件");
        Reject<InvalidDataException>(() => plan.Generate(_ => -1, DateTimeOffset.UtcNow), "负抽样不取模或重新抽奖");
        var independent = new DungeonDropGenerationPlan("合成百万分母边界", [new(500000, pool, 1, 1, ProbabilityDenominator: 1000000)]);
        That(independent.Generate(bound => bound == 1000000 ? 499999 : 0, DateTimeOffset.UtcNow).Length == 1 &&
            independent.Generate(bound => bound == 1000000 ? 500000 : 0, DateTimeOffset.UtcNow).IsEmpty,
            "不同随机分母保留原临界值，百万分母不能被固定万分母截断");
        var deadline = DateTimeOffset.FromUnixTimeSeconds(1800000000);
        var expired = stack with { Id = 3000, Expiration = new(deadline.ToUnixTimeSeconds()) };
        var mixed = new DungeonItemDropPool(new DungeonWeightedDropItem[] { new(expired, 1), new(stack, 1) });
        var dated = new DungeonDropGenerationPlan("合成到期候选与合法奖励", [new(10000, mixed, 1, 1), new(10000, pool, 1, 1)]);
        int draws = 0;
        var afterExpiry = dated.Generate(_ => { draws++; return 0; }, deadline);
        That(afterExpiry.Length == 1 && afterExpiry.Single().Item!.ItemId == stack.Id && draws == 4 && mixed.TotalWeight == 2,
            "命中过期候选不重抽、不改权重，后续合法奖励仍完整生成");
        That(dated.Generate(_ => 0, deadline.AddSeconds(-1)).Select(row => row.Item!.ItemId).SequenceEqual(new[] { expired.Id, stack.Id }),
            "截止之前仍可生成有效活动物品，不能在启动或入图时永久删掉候选");
        var emptyPlan = new DungeonDropGenerationPlan("全部候选到期", [new(10000, mixed, 1, 1)]);
        That(emptyPlan.Generate(_ => 0, deadline.AddSeconds(1)).IsEmpty,
            "全部抽样到期可冻结空集合，不制造替代物或抛出连接异常");
        var relative = new DungeonItemDropPool(new DungeonWeightedDropItem[] { new(expired with { Expiration = new(deadline.ToUnixTimeSeconds(), Days: 1) }, 1) });
        var relativePlan = new DungeonDropGenerationPlan("相对期限优先", [new(10000, relative, 1, 1)]);
        That(relativePlan.Generate(_ => 0, deadline).Single().Item!.ExpireTime == deadline.ToUnixTimeSeconds() + 86400,
            "绝对日期已过但相对期限有效时不误删奖励");
        Reject<InvalidDataException>(() => DungeonDropCreation.Create(expired, 1, _ => 0, deadline),
            "底层创建仍拒绝过期资产，不借掉落容错延长其它入口期限");
        Console.WriteLine($"PASS {Count - before} 完整掉落生成数量、未命中及独立装备属性断言");
    }
}
