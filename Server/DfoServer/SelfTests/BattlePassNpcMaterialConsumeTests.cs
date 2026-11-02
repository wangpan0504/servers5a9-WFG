using DfoServer.Game.BattlePass;
using DfoServer.GameWorld.BattlePass;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class BattlePassNpcMaterialConsumeTests
{
    public static void RunCore()
    {
        int before = Count;
        foreach (var (id, npc, material) in new[] { (31, 476, 10158964), (32, 609, 10306130) })
        {
            var definition = new BattlePassMission(id, 1, 1000, 28, "原指定商人材料消费", material);
            That(BattlePassNpcMaterialConsumeSource.Material(npc) == material &&
                BattlePassNpcMaterialConsumeSource.Matches(1, definition, npc, material), "原周任务按商人及材料成对匹配");
            That(BattlePassNpcMaterialConsumeSource.NextCount(1, definition, npc, material, 0, 9) == 1 &&
                BattlePassNpcMaterialConsumeSource.NextCount(1, definition, npc, material, 0, int.MaxValue) == 1 &&
                BattlePassNpcMaterialConsumeSource.NextCount(1, definition, npc, material, 1, 1) == 1,
                "真实消耗数量封顶原1，不按商品产物或重复请求扩增");
            foreach (var other in new[] { definition with { Id = 30 }, definition with { RawTypeCode = 29 },
                definition with { RawParameter = -1 }, definition with { RequiredCount = 2 }, definition with { Experience = 100 } })
                That(!BattlePassNpcMaterialConsumeSource.Matches(1, other, npc, material), "变异定义不借原材料消费来源");
            That(!BattlePassNpcMaterialConsumeSource.Matches(1, definition, npc == 476 ? 609 : 476, material) &&
                !BattlePassNpcMaterialConsumeSource.Matches(1, definition, npc, material + 1), "其他商人或其他材料不能替代原成对资格");
            foreach (byte category in new byte[] { 0, 2, byte.MaxValue })
            {
                That(!BattlePassNpcMaterialConsumeSource.Matches(category, definition, npc, material), "原周任务不得借错误分类消费");
                Reject<InvalidDataException>(() => BattlePassNpcMaterialConsumeSource.NextCount(category, definition, npc, material, 0, 1), "错误任务分类不得累加");
            }
            foreach (int amount in new[] { 0, -1 })
                Reject<InvalidDataException>(() => BattlePassNpcMaterialConsumeSource.NextCount(1, definition, npc, material, 0, amount), "未实际消费不得推进");
            foreach (long current in new[] { -1L, 2L })
                Reject<InvalidDataException>(() => BattlePassNpcMaterialConsumeSource.NextCount(1, definition, npc, material, current, 1), "损坏原进度不得静默修正");
        }
        That(BattlePassNpcMaterialConsumeSource.Material(0) == 0 && BattlePassNpcMaterialConsumeSource.Material(486) == 0,
            "其他NPC不借指定商人材料资格");
        Console.WriteLine($"PASS {Count - before} BattlePassNpcMaterialConsumeTests core");
    }
}
