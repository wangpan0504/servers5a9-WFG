using System.Collections.Immutable;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Raids;
using DfoServer.GameWorld.Raids;
using DfoServer.Infrastructure;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class RaidEpicRewardRateTests
{
    public static void RunResources(PvfArchive archive, ItemResources items)
    {
        int before = Count;
        // 真实奖励箱同时发材料与一件史诗；按最终产物而不是箱子的Consumable品级识别。
        int[] boxes = [10304486, 10306757, 10309166, 10308714, 10309352];
        var roots = boxes.Select(id => new RaidReward("squad_item", -1, 10, id, 0)).ToImmutableArray();
        var script = new RaidScript("epic-odds-fixture", null, null, null, [],
            [new("phase", [], [], roots)], [], []);
        var policy = new RaidRewardContainers(script, items.Items, items.Boosters);
        foreach (int id in boxes)
        {
            RaidReward[] rows = [new("squad_item", -1, 90, 10093974, 0), new("squad_item", -1, 10, id, 0)];
            foreach (var (factor, expected) in new (decimal, int[])[]
                { (0, [100, 0]), (0.5m, [95, 5]), (1, [90, 10]), (2, [80, 20]), (100, [0, 100]) })
                That(policy.Weights(rows, row => row.ItemId, row => row.RawB, factor).SequenceEqual(expected),
                    "团本史诗容器外层概率只加成一次、保持材料与箱内保底数量：" + id + "/" + factor);
        }
        foreach (decimal factor in new[] { 0m, 0.5m, 1m, 2m, 100m })
        {
            RaidReward[] guaranteed = [new("squad_item", -1, 7, boxes[0], 0), new("squad_item", -1, 13, boxes[1], 0)];
            var widths = policy.Weights(guaranteed, row => row.ItemId, row => row.RawB, factor);
            That(widths.SequenceEqual(new[] { 7, 13 }), "全史诗分队保底保持原总权重及相对概率：" + factor);
            foreach (int roll in new[] { 0, 6, 7, 19 })
            {
                int remaining = roll;
                var selected = guaranteed.Where((_, index) => (remaining -= widths[index]) < 0).First();
                That(selected.ItemId == (roll < 7 ? boxes[0] : boxes[1]), "原total抽样两端和分界均命中全史诗分队奖励");
            }
            RaidReward[] mixed = [new("squad_item", -1, 7, 10093974, 0), new("squad_item", -1, 11, boxes[0], 0), new("squad_item", -1, 13, boxes[1], 0)];
            var adjusted = policy.Weights(mixed, row => row.ItemId, row => row.RawB, factor);
            That(adjusted.Sum() == 31 && adjusted.All(width => width >= 0), "混合分队重分配完整保留原total：" + factor);
            int tail = 30;
            That(mixed.Where((_, index) => (tail -= adjusted[index]) < 0).Any(), "混池原total尾部必须命中");
        }
        var settings = new DungeonDropControlSettings();
        settings.Replace([new(DungeonDropScope.All, [], new(Equipment: 2)),
            new(DungeonDropScope.Raid, [51], new(Equipment: 9))], true);
        var captured = settings.Current.ResolveReward(DungeonDropScope.Raid);
        That(captured?.Equipment == 2, "单地图规则不能扩大为整个团本的奖励倍率");
        settings.Replace([new(DungeonDropScope.Raid, [], new(Equipment: 4))], true);
        That(captured?.Equipment == 2 && settings.Current.ResolveReward(DungeonDropScope.Raid)?.Equipment == 4,
            "开团快照不随GM热改变化，只有下次开团使用新配置");
        settings.Replace([], false);
        That(settings.Current.ResolveReward(DungeonDropScope.Raid) is null, "关闭配置后新奖励沿原概率");
        Console.WriteLine($"PASS {Count - before} 攻坚史诗最终产物概率与冻结断言");
    }
}

