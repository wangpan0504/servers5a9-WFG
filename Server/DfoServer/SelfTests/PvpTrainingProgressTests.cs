using DfoServer.Game.Pvp;
using DfoServer.GameWorld.Quests;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class PvpTrainingProgressTests
{
    public static void Run()
    {
        int before = Count;
        foreach (int id in new[] { 2672, 2673, 2674, 2676, 2677, 2678, 2679 })
        {
            int mode = id is 2673 or 2677 or 2679 ? 2 : id == 2674 ? 1 : 0;
            var quest = QuestEligibilityTests.Quest(id) with { Condition = "[custom multi condition]", Npc = 323, CompleteNpc = -1,
                IntData = mode == 2 ? [1, 0, 0, 2] : [1, 1, 1, mode] };
            Check(quest);
            That(!PvpTrainingProgress.Matches(quest with { Id = 2680 }) &&
                !PvpTrainingProgress.Matches(quest with { IntData = [1, 1, 1, 3] }) &&
                !PvpTrainingProgress.Matches(quest with { Npc = 324 }) &&
                !PvpTrainingProgress.Matches(quest with { SubType = 1 }), "未知任务、模式或其它塔条件不能借用训练规则");
        }
        Console.WriteLine($"PASS {Count-before} 决斗训练三通道及难度纯规则断言（不代表正式训练实例已接入）");
    }

    public static void RunResources(QuestCatalog quests)
    {
        int before = Count;
        foreach (int id in new[] { 2672, 2673, 2674, 2676, 2677, 2678, 2679 }) Check(quests.Get(id));
        Console.WriteLine($"PASS {Count-before} 真实PVF训练任务计数、模式及难度断言");
    }

    static void Check(StoryQuest quest)
    {
        bool soloCounter = quest.Id is 2673 or 2677 or 2679;
        uint initial = soloCounter ? 1u : 262657u; // 原声明1+1*512+1*262144；独立常量避免同错自证。
        That(PvpTrainingProgress.Initial(quest) == initial && PvpTrainingProgress.Valid(quest, initial), "任务保留原三组9位计数");
        That(!PvpTrainingProgress.Valid(quest, 0x08000000) && !PvpTrainingProgress.Valid(quest, 2), "高位和超过原计数的存档拒绝");
        if (soloCounter)
        {
            That(!PvpTrainingProgress.TryApply(quest, initial, PvpTrainingFact.SkillsCommitted, 2, out _) &&
                !PvpTrainingProgress.TryApply(quest, initial, PvpTrainingFact.ComboCompleted, 2, out _), "单目标只认末轮对练事实");
            if (quest.Id == 2679)
                That(!PvpTrainingProgress.TryApply(quest, initial, PvpTrainingFact.OpponentsDefeated, 1, out _), "风卷流云不能以普通难度完成");
            That(PvpTrainingProgress.TryApply(quest, initial, PvpTrainingFact.OpponentsDefeated, 2, out uint done) && done == 0,
                "单目标末轮胜利更新通道0");
        }
        else
        {
            That(PvpTrainingProgress.TryApply(quest, initial, PvpTrainingFact.SkillsCommitted, 0, out uint skills) && skills == 262656,
                "自动加点事实仅清通道0");
            That(PvpTrainingProgress.TryApply(quest, skills, PvpTrainingFact.ComboCompleted, 0, out uint combo) && combo == 262144,
                "连续技事实仅清通道1");
            That(PvpTrainingProgress.TryApply(quest, combo, PvpTrainingFact.OpponentsDefeated, 0, out uint done) && done == 0,
                "末轮胜利事实仅清通道2");
        }
        That(!PvpTrainingProgress.TryApply(quest, 0, PvpTrainingFact.OpponentsDefeated, 2, out _) &&
            !PvpTrainingProgress.TryApply(quest, initial, (PvpTrainingFact)255, 2, out _), "重复或未知事实不生成新进度");
    }
}
