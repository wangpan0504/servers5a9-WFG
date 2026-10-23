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
        Console.WriteLine($"PASS {Count - before} 完整掉落生成数量、未命中及独立装备属性断言");
    }
}
