using DfoServer.Game.Characters;
using DfoServer.Game.Quests;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class QuestStateProgressTests
{
    public static void RunCore()
    {
        int checks = Count;
        var character = new Character(1,0,"State",1,new(38,0,450,250),null);
        var level = QuestEligibilityTests.Quest(100) with { Condition = "[level up]", IntData = [20] };
        var grow = level with { Id = 101, Condition = "[first grow]", IntData = [] };
        var awake = level with { Id = 102, Condition = "[awakening]", IntData = [2] };
        var done = level with { Id = 103, Condition = "[clear quest]", IntData = [104] };
        That(QuestStateProgress.Type(level) == 31 && QuestStateProgress.Type(grow) == 39 && QuestStateProgress.Type(awake) == 40 &&
            QuestStateProgress.Type(done) == 12,"本版目标类型31/39/40/12不沿用参考版本编号");
        That(QuestStateProgress.Scalar(character with { Level = 19 },level) == 1 &&
            QuestStateProgress.Scalar(character with { Level = 20 },level) == 0,"达到目标等级使用边界包含，低一级不能完成");
        That(QuestStateProgress.Type(level with { IntData = [200] }) is null &&
            QuestStateProgress.Type(awake with { IntData = [] }) is null,"未使用的失效等级和缺少觉醒参数不截断或猜测修补");
        for (byte job = 0; job < 16; job++)
        {
            That(QuestStateProgress.Scalar(character with { Job = job, GrowType = 0 },grow) == 1 &&
                QuestStateProgress.Scalar(character with { Job = job, GrowType = 1 },grow) == 0,"转职目标按本职业实际grow位核对");
            That(QuestStateProgress.Scalar(character with { Job = job, GrowType = 17 },awake) == 1 &&
                QuestStateProgress.Scalar(character with { Job = job, GrowType = 33 },awake) == 0,"二觉目标不因已一觉而放行");
        }
        That(QuestStateProgress.Scalar(character,done) == 1 && QuestStateProgress.Scalar(character with { CompletedQuests = [104] },done) == 0 &&
            QuestStateProgress.Scalar(character with { AccountCompletedQuests = [104] },done) == 0,"指定任务目标同时消费角色与账号真实完成记录");
        var catalog = new QuestCatalog(new[] { level,grow,awake,done,QuestEligibilityTests.Quest(104) }.ToDictionary(row => row.Id));
        var active = new ActiveMainQuest(0,100,1) { AcceptanceId = Guid.NewGuid() };
        That(QuestStateProgress.Refresh([active],character with { Level = 20 },catalog).Single().ConditionState == 0 &&
            QuestStateProgress.Refresh([active with { AcceptanceId = Guid.Empty }],character with { Level = 20 },catalog).Single().ConditionState == 1,
            "状态重算保持槽和接取身份，不把历史无身份活动槽接管成新任务");
        Console.WriteLine($"PASS {Count-checks} 角色状态任务及本版类型断言");
    }

    public static void Run(TownCatalog towns,PvfArchive pvf,CharacterCatalog characters,ItemResources items)
    {
        int checks = Count;
        var skills = global::DfoServer.Testing.SelfTestCatalogs.SkillLearning(pvf); var growth = global::DfoServer.Testing.SelfTestCatalogs.Growth(pvf, skills);
        var parameters = global::DfoServer.Testing.SelfTestCatalogs.QuestRewardParameters(pvf);
        var trigger = QuestEligibilityTests.Quest(100) with { Npc = 2, CompleteNpc = 2, IntData = [2] };
        var level = trigger with { Id = 101, Condition = "[level up]", IntData = [2], Attributes = ["not give exp quest"] };
        var related = level with { Id = 102, Condition = "[clear quest]", IntData = [100] };
        using var f = new QuestOrdinaryCompletionTests.Fixture(towns,characters,skills,growth,parameters,items,trigger,
            materialCount: 0,active: [new(0,100,0),new(1,101,1),new(2,102,1)],definitions: [trigger,level,related]);
        That(f.Finish(ushort.MaxValue,101).Outcome == QuestFinishOutcome.Rejected,"客户端交付不能替代实际等级事实");
        var before = f.Read();
        var plan = f.Prepare(ushort.MaxValue).Plan!;
        That(plan.TargetLevel >= 2 && plan.ActiveAfter.All(row => row.ConditionState == 0),"领奖经验和完成记录在同一计划更新其它状态任务");
        f.Sql("CREATE TRIGGER state_abort AFTER INSERT ON character_quest_completion_receipts BEGIN SELECT RAISE(ABORT,'state rollback'); END");
        Reject<Microsoft.Data.Sqlite.SqliteException>(() => f.Repository.CommitQuestCompletion(f.Owner,plan),"组合状态目标末段失败");
        That(QuestCompletionRepositoryTests.Same(before,f.Read()),"等级、任务完成位和关联目标一并回滚");
        f.Sql("DROP TRIGGER state_abort");
        That(f.Finish(ushort.MaxValue).Receipt?.Commit.Character.ActiveMainQuests.All(row => row.ConditionState == 0) == true,
            "普通领奖同步安装状态任务进度");
        That(f.Finish(ushort.MaxValue,101).Receipt?.Commit.ConditionType == 31 &&
            f.Finish(ushort.MaxValue,102).Receipt?.Commit.ConditionType == 12,"状态任务按本版分类通过统一零奖励交付事务");
        f.Session.ReturnToRoster(); f.Session.Select(0); f.Session.MarkReady();
        That(f.Read().CompletedQuests.Order().SequenceEqual([100,101,102]) && f.Receipts == 3,"断线式重新选角保持真实状态和唯一回执");
        Console.WriteLine($"PASS {Count-checks} 角色状态任务原子交付、回滚与重登断言");
    }
}
