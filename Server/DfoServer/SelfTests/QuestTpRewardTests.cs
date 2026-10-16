using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Quests;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using PvfLib;
using static DfoServer.Testing.TestCheck;
using static DfoServer.QuestCompletionRepositoryTests;

namespace DfoServer;

internal static class QuestTpRewardTests
{
    public static void RunCore()
    {
        int checks = Count;
        var quest = QuestEligibilityTests.Quest(100) with { RewardType = "[tp]" };
        That(QuestCharacterReward.TryTp(quest,out ushort amount) && amount == 2 &&
            !QuestCharacterReward.TryScalar(quest,out _),"本版空数组TP奖励固定2点，不作为装备槽索引");
        foreach (var invalid in new[]
        {
            quest with { RewardType = "[item]" }, quest with { Rewards = [new(0,2,null)] },
            quest with { Definition = quest.Definition! with { SelectRewards = [new(0,2,null)] } },
            quest with { Definition = new([new("reward int data",[],[new("unknown",[],[])])]) },
            quest with { Definition = new([new("reward selection int data",[new(6,0,"unknown")],[])]) }
        })
            That(!QuestCharacterReward.TryTp(invalid,out ushort invalidAmount) && invalidAmount == 0,
                "带数量、选择或未知嵌套声明不能借用本版空数组TP合同");
        var skills = new LearnedSkill[] { new(0,5,2) { Command = "000302" },new(1,8,3) };
        var state = new CharacterLearningState(1234,
            [new(1,29,11,skills) { Combos = [new(5,[8])] },new(0,7,9,[new(12,9,4)])])
            { BonusSp = 137, BonusTp = 11, ExpertContractExpiresAt = 123456 };
        string before = JsonSerializer.Serialize(state);
        var granted = QuestCharacterReward.AddTp(state,amount);
        That(before == JsonSerializer.Serialize(state),"赠点规划不修改原学习快照");
        That(granted.BonusTp == 13 && granted.Trees.Select(tree => tree.Tp).SequenceEqual(new ushort[] { 13,11 }) &&
            granted.Experience == state.Experience && granted.BonusSp == state.BonusSp &&
            granted.ExpertContractExpiresAt == state.ExpertContractExpiresAt &&
            granted.Trees.Zip(state.Trees).All(pair => pair.First.Context == pair.Second.Context && pair.First.Sp == pair.Second.Sp &&
                pair.First.Skills.SequenceEqual(pair.Second.Skills) && pair.First.SameCombos(pair.Second)),
            "纯TP增加额外来源与每树余额，保持不同已花费量、上下文、经验、SP、技能、指令和连招");
        skills[0] = new(0,5,9);
        That(granted.Trees[0].Skills[0].Level == 2 && before != JsonSerializer.Serialize(state),
            "赠点计划冻结两树绑定，不共享调用方可变技能数组");
        DfoServer.Testing.TestCheck.Reject<NotSupportedException>(() => QuestCharacterReward.AddTp(state with { BonusTp = null },2),"未知额外TP来源不能按零赠点");
        DfoServer.Testing.TestCheck.Reject<NotSupportedException>(() => QuestCharacterReward.AddTp(CharacterLearningState.Uninitialized,2),"无双树不能伪造点数初始化");
        That(QuestCharacterReward.AddTp(state with { BonusSp = null },2).BonusSp is null,
            "已确认TP来源不要求无关SP来源，也不顺带接管旧SP");
        DfoServer.Testing.TestCheck.Reject<OverflowException>(() => QuestCharacterReward.AddTp(state with { BonusTp = 65534 },2),"额外来源溢出整笔拒绝");
        DfoServer.Testing.TestCheck.Reject<OverflowException>(() => QuestCharacterReward.AddTp(state with
            { Trees = state.Trees.Select(tree => tree with { Tp = ushort.MaxValue }).ToArray() },2),"任一树余额溢出整笔拒绝");
        Console.WriteLine($"PASS {Count-checks} 本版TP奖励形状、独立来源、双树保持与冻结断言");
    }

    public static void Run(TownCatalog towns,PvfArchive pvf,CharacterCatalog characters,ItemResources items,
        QuestCatalog? sharedQuests = null,SkillLearningCatalog? sharedSkills = null)
    {
        int checks = Count;
        var actual = sharedQuests ?? global::DfoServer.Testing.SelfTestCatalogs.Quests(pvf); var learning = sharedSkills ?? global::DfoServer.Testing.SelfTestCatalogs.SkillLearning(pvf);
        var growth = global::DfoServer.Testing.SelfTestCatalogs.Growth(pvf, learning); var parameters = global::DfoServer.Testing.SelfTestCatalogs.QuestRewardParameters(pvf);
        var actualTp = actual.All.Where(quest => quest.RewardType == "[tp]").ToArray();
        That(actualTp is [{ Id:2674, Condition:"[custom multi condition]", Difficulty:"W", DeclaredGoldMultiple:50,
            DeclaredIgnoreLevelForExperience:1 }] && QuestCharacterReward.TryTp(actualTp[0],out ushort actualAmount) && actualAmount == 2,
            "匹配PVF唯一TP任务保留真实决斗条件、经验difficulty和金币声明，奖励数组确为空");
        // 资源2674的决斗目标尚未实现；以下只合成普通寻物条件验证奖励领域与事务，不称为真实任务完成。
        var quest = QuestEligibilityTests.Quest(100) with
        {
            Condition = "[seeking]", IntData = [10165055,3], Npc = 2, CompleteNpc = 2,
            RewardType = "[tp]", Difficulty = "W", DeclaredGoldMultiple = 50, DeclaredIgnoreLevelForExperience = 1,
            Attributes = ["not give exp quest"], Definition = new([]) { Job = "[all]" }
        };
        var tp = PvfScript.List(pvf.ReadText("skill/swordmanskill.lst")).Keys.Select(id => learning.Get(0,checked((ushort)id)))
            .First(skill => skill.SupportsTpLearning && skill.GetMaximum(0) >= 2 && skill.Prerequisites!.Count == 0);
        int tpCost = tp.GetPointCost(0,0)+tp.GetPointCost(1,0);

        using (var f = Make())
        {
            var before = f.Read(); var prepared = f.Prepare(ushort.MaxValue);
            That(prepared is { Outcome:QuestCompletionOutcome.Accepted, Plan:{ TpAmount:2, GrowthPlan:null, UpdatesLearning:true } },
                "零经验TP领奖独立规划真实学习更新，保留普通物品消耗通道");
            var plan = prepared.Plan!;
            That(plan.ExperienceReward.Amount == 0 && plan.GoldAmount == (uint)((long)parameters.Gold[95]*50/100) &&
                plan.Rewards.Length == 0 && plan.Currencies.Length == 0 && plan.Consumed is [{ ItemId:10165055,Count:3 }] &&
                plan.Snapshot!.Contains("\"TpAmount\":2",StringComparison.Ordinal) && SameTpGrant(before.Learning,plan.TargetLearning),
                "TP固定2点与原金币表计算、真实扣材共同冻结，不构造物品奖励或清除金币");
            for (byte job = 0; job < 16; job++)
                That(QuestCompletionPlan.Prepare(before with { Job = job },quest,ushort.MaxValue,f.Catalog,parameters,items.Items,
                    items.GoldLimits,items.EpicPieces,growth,DateTimeOffset.UtcNow).Plan is { TpAmount:2 },"奖励规划不增加职业特判 " + job);
            var unsupported = before with { Level = 50, CompletedQuests = [2673],
                ActiveMainQuests = [new(0,2674,0) { AcceptanceId = Guid.NewGuid() }] };
            That(QuestCompletionPlan.Prepare(unsupported,actualTp[0],ushort.MaxValue,actual,parameters,items.Items,
                items.GoldLimits,items.EpicPieces,growth,DateTimeOffset.UtcNow).Plan is null,
                "空客户端进度不能替代真实决斗事实，2674不因奖励能力就开放交付");
            var ordinary = QuestCompletionPlan.Prepare(before,quest with { RewardType = "[item]" },ushort.MaxValue,f.Catalog,parameters,
                items.Items,items.GoldLimits,items.EpicPieces,growth,DateTimeOffset.UtcNow).Plan!;
            That(ordinary.TpAmount == 0 && !ordinary.Snapshot!.Contains("\"TpAmount\"",StringComparison.Ordinal) &&
                JsonSerializer.Serialize(ordinary.TargetLearning) == JsonSerializer.Serialize(before.Learning),
                "普通历史奖励不追加TP快照字段，也不修改两树或额外来源");
            var commit = f.Repository.CommitQuestCompletion(f.Owner,plan);
            That(commit is { Duplicate:false, ProjectionCurrent:true, TpAmount:2 } && commit.Character.Revision == before.Revision+1 &&
                SameTpGrant(before.Learning,commit.Character.Learning) &&
                commit.Character.Inventory.Gold == before.Inventory.Gold+plan.GoldAmount &&
                commit.Character.Inventory.Items.Single(row => row.Core.ItemId == 10165055).Core.Count == 2 &&
                commit.Character.CompletedQuests.Contains(100) && commit.Character.ActiveMainQuests.Length == 0 &&
                Same(commit.Character,f.Read()) && f.Receipts == 1 &&
                Scalar(f,"SELECT bonus_tp FROM character_tp_sources") == 13 &&
                Scalar(f,"SELECT sum(tp) FROM character_skill_points") == 2*(learning.TotalTp(95)+13)-tpCost,
                "扣材、Gold、双树TP与bonus来源、完成记录、唯一revision和回执一次落库，已购买成本保持");
            var replay = f.Repository.CommitQuestCompletion(f.Owner,plan);
            That(replay is { Duplicate:true, ProjectionCurrent:true, TpAmount:2 } && Same(commit.Character,replay.Character) && f.Receipts == 1,
                "同接取完整回执重放不再授TP或消费材料");
            f.Session.ReturnToRoster(); f.Session.Select(0); f.Session.MarkReady();
            using (var db = f.Open())
            {
                var reopened = new CharacterRepository(db.DataSource,f.Catalog);
                That(Same(commit.Character,f.Session.Selected!) && Same(commit.Character,reopened.LoadCharacter(f.Owner,0)) &&
                    f.Finish(ushort.MaxValue).Outcome == QuestFinishOutcome.AlreadyCompleted && f.Receipts == 1,
                    "重选和新仓储实例从原TP来源、双树与领奖回执恢复，不重复发奖");
            }
        }
        foreach (bool failSource in new[] { true,false })
        using (var f = Make())
        {
            var before = f.Read(); var online = f.Session.Selected;
            f.Sql(failSource
                ? "CREATE TRIGGER tp_abort AFTER UPDATE ON character_tp_sources BEGIN SELECT RAISE(ABORT,'tp source rollback'); END"
                : "CREATE TRIGGER tp_abort AFTER INSERT ON character_quest_completion_receipts BEGIN SELECT RAISE(ABORT,'tp receipt rollback'); END");
            DfoServer.Testing.TestCheck.Reject<SqliteException>(() => f.Finish(ushort.MaxValue),"TP来源写入中途或回执末段SQL失败");
            That(Same(before,f.Read()) && f.Receipts == 0 && ReferenceEquals(online,f.Session.Selected),
                "失败回滚扣材、金币、两树余额、bonus来源、完成、revision与回执");
            f.Sql("DROP TRIGGER tp_abort");
            That(f.Finish(ushort.MaxValue).Receipt?.Commit is { Duplicate:false, ProjectionCurrent:true } &&
                SameTpGrant(before.Learning,f.Read().Learning) && f.Receipts == 1,"明确回滚后原冻结TP计划可提交一次");
        }
        using (var f = Make(loseResult:true))
        {
            var before = f.Session.Selected!;
            DfoServer.Testing.TestCheck.Reject<IOException>(() => f.Finish(ushort.MaxValue),"TP领奖已提交但返回丢失");
            var saved = f.Read();
            That(SameTpGrant(before.Learning,saved.Learning) && ReferenceEquals(before,f.Session.Selected) && f.Receipts == 1,
                "不明结果保留原在线快照，持久库已有整笔TP与资产");
            var recovered = f.Finish(ushort.MaxValue);
            That(recovered.Receipt?.Commit is { Duplicate:true, ProjectionCurrent:true } && f.Lost!.SamePlan &&
                Same(saved,f.Session.Selected!) && Same(saved,f.Read()) && f.Receipts == 1,
                "Session按原冻结计划恢复实际TP、Gold与材料结果，无第二次赠点");
        }
        using (var f = Make())
        {
            var before = f.Read(); var plan = f.Prepare(ushort.MaxValue).Plan!;
            using var foreign = new AccountOwners().Acquire("other-quest-owner",() => { });
            DfoServer.Testing.TestCheck.Reject<InvalidDataException>(() => f.Repository.CommitQuestCompletion(foreign,plan),"其他owner不能提交TP计划");
            f.Sql("UPDATE character_tp_sources SET bonus_tp=bonus_tp+1");
            var changed = f.Read();
            DfoServer.Testing.TestCheck.Reject<InvalidOperationException>(() => f.Repository.CommitQuestCompletion(f.Owner,plan),"同revision额外TP来源变化不能覆盖");
            That(Same(changed,f.Read()) && f.Receipts == 0 && changed.Revision == before.Revision,
                "全学习source复验失败不扣材、不发Gold、不清任务");
            f.Sql("UPDATE character_tp_sources SET bonus_tp=bonus_tp-1; UPDATE characters SET revision=revision+1");
            DfoServer.Testing.TestCheck.Reject<InvalidOperationException>(() => f.Repository.CommitQuestCompletion(f.Owner,plan),"旧revision不能提交TP奖励");
            That(f.Receipts == 0 && Same(before with { Revision = before.Revision+1 },f.Read()),"过期规划不留下资产或回执");
        }
        using (var f = Make())
        {
            var before = f.Read();
            foreach (var state in new[] { before.Learning with { BonusTp = null },CharacterLearningState.Uninitialized,
                before.Learning with { BonusTp = 65534 },before.Learning with
                    { Trees = before.Learning.Trees.Select(tree => tree with { Tp = ushort.MaxValue }).ToArray() } })
                That(QuestCompletionPlan.Prepare(before with { Learning = state },quest,ushort.MaxValue,f.Catalog,parameters,items.Items,
                    items.GoldLimits,items.EpicPieces,growth,DateTimeOffset.UtcNow).Plan is null && Same(before,f.Read()) && f.Receipts == 0,
                    "零经验TP缺真实来源、双树或空间时整笔拒绝，不能先扣材或发金币");
        }
        var growing = quest with { Attributes = [], MinimumLevel = 49 };
        using (var f = Make(growing,configure: candidate =>
        {
            var state = SkillPointLedger.Synchronize(0,0,49,candidate.InitialLearning with
                { Experience = growth.GetCumulativeExperienceBoundary(49)-1, BonusSp = 137, BonusTp = 11 },learning);
            return candidate with { Level = 49, InitialLearning = state,
                Skills = state.Trees[0].Skills.Select(skill => new CharacterSkill(skill.Id,skill.Level)).OrderBy(skill => skill.Id).ToArray() };
        }))
        {
            var before = f.Read(); var prepared = f.Prepare(ushort.MaxValue);
            That(prepared.Plan is { TpAmount:2, GrowthPlan:{ TargetLevel: >= 50 }, ExperienceReward.Amount: > 0, GoldAmount: > 0 },
                "普通任务经验可以跨50级并同步等级TP，特殊奖励分支保留经验和金币");
            var plan = prepared.Plan!;
            That(SameTpGrant(plan.Growth.Learning,plan.TargetLearning) && plan.TargetLearning.BonusTp == 13 &&
                plan.TargetLearning.Experience == before.Learning.Experience+plan.ExperienceReward.Amount,
                "先按正常成长增加等级来源，再额外赠2TP，不重复累计已花费或等级贡献");
            var result = f.Finish(ushort.MaxValue);
            That(result.Receipt?.Commit is { Duplicate:false, ProjectionCurrent:true } &&
                SameLearning(plan.TargetLearning,result.Receipt.Commit.Character.Learning) &&
                result.Receipt.Commit.Character.Level == plan.TargetLevel && f.Receipts == 1,
                "跨级经验、金币、等级TP与额外TP同事务提交");
        }
        Console.WriteLine($"PASS {Count-checks} TP奖励真实资源形状、合成领域原子交付、回滚、来源复验与重登断言（非决斗实机）");

        QuestOrdinaryCompletionTests.Fixture Make(StoryQuest? definition = null,bool loseResult = false,
            Func<NewCharacter,NewCharacter>? configure = null) => new(towns,characters,learning,growth,parameters,items,definition ?? quest,
                loseResult:loseResult,configure:configure ?? AtTpLevel);
        NewCharacter AtTpLevel(NewCharacter candidate)
        {
            var state = SkillPointLedger.Synchronize(0,0,95,candidate.InitialLearning with
                { Experience = growth.GetLevelStartExperience(95), BonusSp = 137, BonusTp = 11 },learning);
            var bought = SkillLearning.Plan(0,0,95,state,new(0,0,[new(tp.SkillId,false,2)]),learning);
            That(bought.Success,"专项fixture采用真实TP技能逐级购买，两树保留不同余额");
            state = bought.State! with { Trees = bought.State!.Trees.Select(tree => tree with
                { Skills = tree.Skills.Select(skill => skill.Id == tp.SkillId ? skill with { Command = "000302" } : skill).ToArray() }).ToArray() };
            return candidate with { Level = 95, InitialLearning = state,
                Skills = state.Trees[0].Skills.Select(skill => new CharacterSkill(skill.Id,skill.Level)).OrderBy(skill => skill.Id).ToArray() };
        }
    }

    static bool SameTpGrant(CharacterLearningState before,CharacterLearningState after) =>
        before.BonusTp is { } bonus && after.BonusTp == bonus+2 && SameLearning(before with
            { BonusTp = bonus+2, Trees = before.Trees.Select(tree => tree with { Tp = checked((ushort)(tree.Tp+2)) }).ToArray() },after);
    static bool SameLearning(CharacterLearningState before,CharacterLearningState after) => JsonSerializer.Serialize(before) == JsonSerializer.Serialize(after);
    static long Scalar(QuestOrdinaryCompletionTests.Fixture fixture,string sql)
    {
        using var db = fixture.Open(); using var command = db.CreateCommand(); command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }
}
