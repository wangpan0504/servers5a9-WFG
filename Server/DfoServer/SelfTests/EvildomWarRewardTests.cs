using System.Collections.Immutable;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Dungeons;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class EvildomWarRewardTests
{
    public static void RunCore()
    {
        int before = Count;
        ImmutableArray<EvildomWarRewardDefinition> normal = [new(0, 1, 101, 4, 5000), new(0, 1, 101, 5, 5000),
            new(1, 0, 105, 15, 10000), new(3, 0, -1, 0, 9372), new(3, 1, 102, 1, 500)];
        ImmutableArray<EvildomWarRewardDefinition> hard = [new(0, 0, 103, 1, 10000)];
        ImmutableArray<EvildomWarRewardDefinition> guide = [new(0, 0, 104, 2, 10000)];
        ImmutableArray<EvildomWarRewardDefinition> Pick(byte mode, params int[] rolls)
        {
            var queue = new Queue<int>(rolls);
            var result = EvildomWarRewardPolicy.Select(mode, normal, hard, guide, maximum =>
            {
                That(maximum == 10000, "配置奖励每组使用固定万分分母");
                return queue.Dequeue();
            });
            That(queue.Count == 0, "每组仅抽取一次，不为显式空项或尾部重抽");
            return result;
        }
        That(Pick(0, 0, 0) is [{ CountRaw: 4 }], "万分首值与显式空项");
        That(Pick(0, 4999, 9371) is [{ CountRaw: 4 }], "权重上界之前仍属于原项");
        That(Pick(0, 5000, 9372) is [{ CountRaw: 5 }, { ItemRaw: 102 }], "权重边界切换到下一项");
        That(Pick(0, 9999, 9871) is [{ CountRaw: 5 }, { ItemRaw: 102 }], "最后有效权重仍可抽出稀有项");
        That(Pick(0, 9999, 9872) is [{ CountRaw: 5 }] && Pick(0, 0, 9999) is [{ CountRaw: 4 }],
            "未满万分尾部保持空，不归一化");
        That(Pick(1, 0, 0, 0) is [{ ItemRaw: 101 }, { ItemRaw: 103 }] && Pick(2, 0) is [{ ItemRaw: 104 }],
            "本服困难基础加追加，引导使用独立表");
        foreach (var invalid in new[] { new EvildomWarRewardDefinition(4, 0, 1, 1, 1), new(0, 2, 1, 1, 1),
            new(0, 0, -2, 0, 1), new(0, 0, -1, 1, 1), new(0, 0, 0, 1, 1), new(0, 0, 1, 0, 1),
            new(0, 0, 1, 129, 1), new(0, 0, 1, 1, -1), new(0, 0, 1, 1, 10001) })
            Reject<InvalidDataException>(() => EvildomWarRewardPolicy.Select(0, [invalid], hard, guide, _ => 0), "拒绝无效奖励配置");
        Reject<InvalidDataException>(() => EvildomWarRewardPolicy.Select(0, [hard[0], hard[0]], hard, guide, _ => 0), "拒绝同组权重溢出");
        Reject<InvalidDataException>(() => EvildomWarRewardPolicy.Select(0, [], hard, guide, _ => 0), "拒绝空表");
        foreach (int invalid in new[] { -1, 10000 })
            Reject<InvalidDataException>(() => EvildomWarRewardPolicy.Select(0, normal, hard, guide, _ => invalid), "拒绝随机源越界");
        Reject<ArgumentOutOfRangeException>(() => EvildomWarRewardPolicy.Select(3, normal, hard, guide, _ => 0), "拒绝未知模式");
        Console.WriteLine($"PASS evildom war reward rules {Count - before}");
    }
}
