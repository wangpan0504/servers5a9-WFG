using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Characters;
using DfoServer.Game.Quests;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Testing;
using Microsoft.Data.Sqlite;
using PvfLib;
using static DfoServer.Testing.TestCheck;
using static DfoServer.QuestCompletionRepositoryTests;

namespace DfoServer;

internal static class QuestCascadeCompletionTests
{
    public static void Run(TownCatalog towns,PvfArchive pvf,CharacterCatalog characters,ItemResources items)
    {
        int checks = Count;
        var actual = QuestCatalog.Load(pvf); var learning = SkillLearningCatalog.Load(pvf, DfoServer.GameWorld.Items.PremiumContractCatalog.Load(pvf).ExpertSkillLevels);
        var growth = CharacterGrowthCatalog.Load(pvf,learning); var parameters = QuestRewardParameterCatalog.Load(pvf);
        foreach (var (terminal,markerId) in new[] { (6211,6218),(6308,6315),(6352,6380) })
        {
            That(QuestRelationProgress.CascadeMarker(actual.Get(terminal),actual) == markerId &&
                QuestRelationProgress.IsCascadeMarker(actual.Get(markerId)),"匹配PVF显式casecade终点引用独立无奖励角色标记: " + terminal);
            That(QuestCoverage.HasOrdinaryDeclarations(actual.Get(terminal)),"真实终点的级联及NPC显示声明不再阻断普通交付: " + terminal);
        }
        That(QuestRelationProgress.CascadeMarker(actual.Get(6219),actual)==6211 && QuestCoverage.HasRequiredReferences(actual.Get(6219),actual),
            "真实简短旅程6219可登记长旅程6211终点，不被无奖励marker限制拦截");
        var shortTerminal=actual.Get(6219) with {Id=100,Prerequisites=[],
            Definition=actual.Get(6219).Definition! with {PrerequisiteGroups=[]}};
        using(var f=new QuestOrdinaryCompletionTests.Fixture(towns,characters,learning,growth,parameters,items,shortTerminal,
            materialCount:0,definitions:[shortTerminal,actual.Get(6211),actual.Get(6218)],active:[new(0,100,0)],
            configure:candidate=>candidate with {Level=95,CompletedQuests=[6218],
                InitialLearning=SkillPointLedger.Synchronize(candidate.Job,candidate.GrowType,95,
                    candidate.InitialLearning with {Experience=growth.GetLevelStartExperience(95)},learning)}))
        {
            var prepared=f.Prepare(ushort.MaxValue);
            That(prepared.Plan is {CascadeMarkerQuestId:6211} && prepared.Plan.NewCompletedQuestIds.SequenceEqual([100,6211]),
                "简短旅程只冻结本任务与直接终点，不递归完成或重复领取长旅程奖励");
            var committed=f.Repository.CommitQuestCompletion(f.Owner,prepared.Plan!);
            That(committed.Character.CompletedQuests.Contains(6211) && committed.Rewards.Sum(row=>row.GrantedCount)==2 && f.Receipts==1,
                "简短终点发自身两件奖励并登记6211，长终点不生成第二份奖励或回执");
        }
        // 事务测试合成寻物条件，复用真实marker定义；不把条件替换称为真实主线推进。
        var marker = actual.Get(6218) with { Id = 101 };
        var terminalQuest = QuestEligibilityTests.Quest(100) with
        {
            Condition = "[seeking]", IntData = [10165055,3], Npc = 2, CompleteNpc = 2, Rewards = Rows(0,77,2058,3),
            Definition = new([new("casecade clear",[new(0,101,null)],[])])
                { Job = "[all]", SelectRewards = Rows(10403,1,2058,2).ToImmutableArray() }
        };
        using (var f = Make())
        {
            var before = f.Read(); var plan = f.Prepare(1).Plan!;
            That(plan.CascadeMarkerQuestId == 101 && plan.NewCompletedQuestIds.SequenceEqual([100,101]) &&
                plan.CompletedAfter.SequenceEqual([100,101]) && plan.AccountCompletedAfter.SequenceEqual([101]) &&
                plan.Snapshot!.Contains("\"CascadeMarkerQuestId\":101",StringComparison.Ordinal),
                "普通领奖冻结主任务与唯一显式marker完成集合，不借用账号领奖或跳过事务");
            var commit = f.Repository.CommitQuestCompletion(f.Owner,plan);
            That(commit is { Duplicate:false, ProjectionCurrent:true, Rewards.Length:1 } &&
                commit.Rewards[0].Item.Core.ItemId == 2058 && commit.Rewards[0].GrantedCount == 5 && commit.Rewards[0].Item.Uid > 0 &&
                commit.Character.Inventory.Gold == before.Inventory.Gold+77 &&
                commit.Character.Learning.Experience == before.Learning.Experience+plan.ExperienceReward.Amount &&
                JsonSerializer.Serialize(commit.Character.Learning) == JsonSerializer.Serialize(plan.TargetLearning) &&
                commit.Character.Revision == before.Revision+1 && commit.Character.ActiveMainQuests.Length == 0,
                "marker与原寻物扣除、固定/选择奖励、金币、经验和双树一次提交，marker不增发奖励或revision");
            That(commit.Character.CompletedQuests.SequenceEqual([100,101]) && commit.Character.AccountCompletedQuests.SequenceEqual([101]) &&
                Scalar(f,"SELECT count(*) FROM character_completed_quests WHERE quest_id=101 AND daily_period=0 AND answer IS NULL") == 1 &&
                Scalar(f,"SELECT count(*) FROM account_completed_quests WHERE quest_id=101 AND answer IS NULL AND daily_period=0") == 1 && f.Receipts == 1 && Same(commit.Character,f.Read()),
                "显式同步marker同事务登记角色与账号位，空答案、零周期且不新增领奖回执");
            That(commit.Character.Inventory.Items.Single(row => row.Core.ItemId == 10165055).Core.Count == 2 &&
                before.Inventory.Items.Where(row => !plan.Inventory.ChangedSlots.Contains(row.Position)).All(row =>
                    commit.Character.Inventory.Find(row.Position) == row),"全部无关UID/Core实例与原物品状态保留，材料按原计划实际扣三件");
            var alternate=f.Repository.Create(f.Owner,new("CascadeAlt",towns.Room,towns.InitialReturn)
                {ActiveMainQuests=[new(0,6217,QuestQuestionProgress.Unanswered)]});
            var alternateBody=DfoServer.Network.Builders.TownPackets.CompletedQuests(alternate);
            That(alternate.AccountCompletedQuests.SequenceEqual([101]) && alternate.CompletedQuests.Length==0 &&
                alternateBody[4+101]==1 && alternateBody[4+6217]==0 && alternate.Revision==0,
                "正常完成立即向同账号后续角色同步标记，保留未回答旅程及角色revision");
            var replay = f.Repository.CommitQuestCompletion(f.Owner,plan);
            That(replay is { Duplicate:true, ProjectionCurrent:true } && replay.Rewards.SequenceEqual(commit.Rewards) &&
                Same(replay.Character,commit.Character) && f.Receipts == 1,"原完整领奖计划精确重放保留marker与真实奖励UID，不再次扣发");
            f.Session.ReturnToRoster(); f.Session.Select(0); f.Session.MarkReady();
            That(Same(commit.Character,f.Session.Selected!) && f.Finish(1).Outcome == QuestFinishOutcome.AlreadyCompleted,
                "正常选角重登从完成表恢复marker，普通重复交付不重算奖励");
        }
        using (var f = Make(configure: candidate => candidate with { CompletedQuests = [101] }))
        {
            int day = QuestCycle.Day(DateTimeOffset.UtcNow);
            f.Sql($"UPDATE characters SET quest_cycle_day={day}; UPDATE character_completed_quests SET answer=2,daily_period={day} WHERE quest_id=101");
            f.Session.ReturnToRoster(); f.Session.Select(0); f.Session.MarkReady();
            var plan = f.Prepare(1).Plan!;
            That(plan.NewCompletedQuestIds.SequenceEqual([100]) && plan.CompletedAfter.SequenceEqual([100,101]),
                "既有marker不列入新完成集合");
            f.Sql("CREATE TRIGGER marker_no_rewrite BEFORE INSERT ON character_completed_quests WHEN NEW.quest_id=101 BEGIN SELECT RAISE(ABORT,'existing marker rewritten'); END");
            var result = f.Finish(1);
            That(result.Receipt?.Commit is { Duplicate:false, ProjectionCurrent:true } &&
                f.Read().QuestAnswers[101] == 2 && f.Read().QuestDailyClaims[101] == day && f.Receipts == 1,
                "旧marker的原回答及每日周期原样保留，不插入、不覆盖、不发奖励");
        }
        foreach (int failure in new[] { 0,1,2 })
        using (var f = Make())
        {
            var before = f.Read(); var online = f.Session.Selected;
            f.Sql(failure==2 ? "CREATE TRIGGER cascade_abort AFTER INSERT ON account_completed_quests BEGIN SELECT RAISE(ABORT,'cascade account rollback'); END" : failure==1
                ? "CREATE TRIGGER cascade_abort AFTER INSERT ON character_quest_completion_receipts BEGIN SELECT RAISE(ABORT,'cascade receipt rollback'); END"
                : "CREATE TRIGGER cascade_abort AFTER INSERT ON character_completed_quests WHEN NEW.quest_id=101 BEGIN SELECT RAISE(ABORT,'cascade marker rollback'); END");
            TestCheck.Reject<SqliteException>(() => f.Finish(1),"marker中途或领奖回执末段SQL失败");
            That(Same(before,f.Read()) && ReferenceEquals(online,f.Session.Selected) && f.Receipts == 0 &&
                Scalar(f,"SELECT count(*) FROM character_completed_quests WHERE quest_id IN(100,101)") == 0 &&
                Scalar(f,"SELECT count(*) FROM account_completed_quests")==0,
                "marker失败回滚主任务清槽、完成、材料、奖励UID、金币、经验、双树与revision");
            f.Sql("DROP TRIGGER cascade_abort");
            That(f.Finish(1).Receipt?.Commit is { Duplicate:false, ProjectionCurrent:true } && f.Read().CompletedQuests.SequenceEqual([100,101]),
                "明确回滚后原Session冻结计划可正常完成主任务与marker");
        }
        using(var f=new QuestOrdinaryCompletionTests.Fixture(towns,characters,learning,growth,parameters,items,terminalQuest,
            materialCount:5,definitions:[terminalQuest,marker with {Definition=new([new("synchronization account quest",[new(0,0,null)],[])])}]))
        {
            That(f.Finish(1).Receipt!.Commit.Character.CompletedQuests.SequenceEqual([100,101]) && f.Read().AccountCompletedQuests.Length==0,
                "资源sync=0的正常级联标记仍只归当前角色");
        }
        using (var f = Make(loseResult:true))
        {
            var before = f.Read(); var online = f.Session.Selected;
            TestCheck.Reject<IOException>(() => f.Finish(1),"普通领奖与marker已提交但返回丢失");
            var saved = f.Read();
            That(saved.CompletedQuests.SequenceEqual([100,101]) && saved.Revision == before.Revision+1 &&
                ReferenceEquals(online,f.Session.Selected),"返回丢失时数据库已有完整marker和奖励，在线状态保持旧快照待恢复");
            var recovery = f.Finish(1);
            That(recovery.Receipt?.Commit is { Duplicate:true, ProjectionCurrent:true } && f.Lost!.SamePlan &&
                Same(saved,f.Session.Selected!) && f.Receipts == 1,"Session使用原冻结普通领奖计划恢复marker、实际奖励UID和双树，无额外提交");
            f.Session.ReturnToRoster(); f.Session.Select(0); f.Session.MarkReady();
            That(Same(saved,f.Session.Selected!) && f.Finish(1).Outcome == QuestFinishOutcome.AlreadyCompleted && f.Receipts == 1,
                "返回丢失恢复后重登和重复交付都保留唯一普通领奖结果与marker");
        }
        var child = terminalQuest with { MainQuest = 200, Condition = "[meet npc]", IntData = [2], Npc = 2, CompleteNpc = 2,
            RewardType = null, Rewards = [], Attributes = ["not give exp quest"],
            Definition = terminalQuest.Definition! with { SelectRewards = [] } };
        var parent = QuestEligibilityTests.Quest(200) with { Condition = "[quest clear]", SubType = 2, IntData = [100], Npc = 2, CompleteNpc = 2 };
        using (var f = new QuestOrdinaryCompletionTests.Fixture(towns,characters,learning,growth,parameters,items,child,
            materialCount:0,definitions:[parent,child,marker],active:[new(0,200,1),new(1,100,0)]))
        {
            var before = f.Read();
            var result = f.Finish(ushort.MaxValue);
            That(result.Receipt?.Commit is { Rewards.Length:0, GoldAmount:0, ExperienceReward.Amount:0 } &&
                f.Read().CompletedQuests.SequenceEqual([100,101]) && f.Read().ActiveMainQuests is [{ QuestId:200, ConditionState:0 }] &&
                Same(before with { ActiveMainQuests=f.Read().ActiveMainQuests,CompletedQuests=[100,101],AccountCompletedQuests=[101],Revision=before.Revision+1 },f.Read()) && f.Receipts == 1,
                "零奖励会面子任务同事务完成显式marker和父条件，保留全部资产，不提前领取父奖励");
        }
        Console.WriteLine($"PASS {Count-checks} 普通完成显式casecade资源引用、SQLite原子奖励/标记、回滚与重登断言（条件合成，非实机）");

        QuestOrdinaryCompletionTests.Fixture Make(bool loseResult = false,Func<NewCharacter,NewCharacter>? configure = null) =>
            new(towns,characters,learning,growth,parameters,items,terminalQuest,materialCount:5,loseResult:loseResult,
                definitions:[terminalQuest,marker],configure:configure);
    }

    static QuestRewardToken[] Rows(params int[] values) => values.Select(value => new QuestRewardToken(0,value,null)).ToArray();
    static long Scalar(QuestOrdinaryCompletionTests.Fixture fixture,string sql)
    {
        using var db = fixture.Open(); using var command = db.CreateCommand(); command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }
}
