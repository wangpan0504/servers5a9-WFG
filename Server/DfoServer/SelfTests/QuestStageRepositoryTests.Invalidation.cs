using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Auth;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Quests;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Quests;
using DfoServer.Persistence;
using DfoServer.Testing;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class QuestStageRepositoryTests
{
    static void RunInvalidation(TcpTestResources r)
    {
        var growth = SelfTestCatalogs.Growth(r.Archive, r.Learning);
        var parameters = SelfTestCatalogs.QuestRewardParameters(r.Archive);
        var a = QuestEligibilityTests.Quest(100) with { SubType = 1, Difficulty = "G", MinimumLevel = 1,
            Attributes = [], Rewards = [], Definition = new([]) { Job = "[all]" } };
        var c = a with { Id = 101, Path = "fixture/101.qst", Definition = a.Definition! with { PrerequisiteGroups = [[100]] } };
        var n = a with { Id = 102, Path = "fixture/102.qst", MinimumLevel = 13,
            Definition = a.Definition! with { PrerequisiteGroups = [[100]] } };
        QuestOrdinaryCompletionTests.Fixture Fixture(Func<NewCharacter, NewCharacter>? configure = null, StoryQuest[]? definitions = null) =>
            new(r.Towns,r.Characters,r.Learning,growth,parameters,r.ItemResources,a,materialCount:0,
                definitions:definitions ?? [a,c,n],active:[],stages:QuestStageGraph.Build(new((definitions ?? [a,c,n]).ToDictionary(row => row.Id))),
                configure:configure ?? (candidate => candidate with {
                    Level = 11, InitialLearning = SkillPointLedger.Synchronize(candidate.Job,candidate.GrowType,11,
                        candidate.InitialLearning with { Experience = growth.GetLevelStartExperience(11) },r.Learning) }));
        void Reconnect(QuestOrdinaryCompletionTests.Fixture f)
        { f.Session.ReturnToRoster(); f.Session.Select(0); f.Session.MarkReady(); }
        void Source(QuestOrdinaryCompletionTests.Fixture f)
        {
            That(f.Change(100,QuestLifecycleAction.Accept) is not null && f.Finish(ushort.MaxValue).Outcome == QuestFinishOutcome.Committed,
                "失效回归以真实普通A完成建立来源");
            Status(f,QuestStageStatus.Deferred);
        }
        void Status(QuestOrdinaryCompletionTests.Fixture f, QuestStageStatus status) =>
            That(f.Repository.LoadQuestStageState(f.Owner,f.Read()).Witnesses.Single(row => row.SourceQuestId == 100).Status == status,
                "状态列和proof一致加载为" + status);
        CharacterProgressEdit Edit(QuestOrdinaryCompletionTests.Fixture f,CharacterProgressField field,int value) =>
            new(field,f.Owner.Account,f.Read().Name,value,0);

        // 删除任一实际变化门禁、遗漏某类GM写入口或只更新status不更新proof，都会失败。
        foreach (var (field,value,changed) in new[] {
            (CharacterProgressField.Level,12,true),(CharacterProgressField.Level,11,false),
            (CharacterProgressField.Grow,1,true),(CharacterProgressField.Grow,0,false),
            (CharacterProgressField.Story,20,true),(CharacterProgressField.Story,1,false),
            (CharacterProgressField.QuestAdd,999,true),(CharacterProgressField.QuestAdd,100,false),
            (CharacterProgressField.QuestRemove,100,true),(CharacterProgressField.QuestRemove,999,false),
            (CharacterProgressField.QuestClear,0,true),(CharacterProgressField.BonusSp,2,false),
            (CharacterProgressField.BonusTp,2,false),(CharacterProgressField.ExpertContract,100,false) })
        {
            using var f = Fixture(); Source(f);
            if (field == CharacterProgressField.ExpertContract)
                new AuthenticationService(new AuthenticationRepository(f.OpenPath)).Create(f.Owner.Account,"fixture-password");
            int actualValue = field == CharacterProgressField.Story && !changed ? checked((int)f.Read().AcceptedStoryLevel) : value;
            f.Repository.UpdateProgress(f.Owner,Edit(f,field,actualValue));
            Status(f,changed ? QuestStageStatus.Invalidated : QuestStageStatus.Deferred);
            That(Scalar(f,"SELECT count(*) FROM character_quest_stage_claims") == "0","编辑不创建奖励claim");
        }
        using (var f = Fixture())
        {
            Source(f); var skill = f.Read().Skills.First();
            f.Repository.UpdateProgress(f.Owner,new(CharacterProgressField.Skill,f.Owner.Account,f.Read().Name,skill.Id,skill.Level));
            f.Repository.UpdateProgress(f.Owner,Edit(f,CharacterProgressField.QuestAdd,100) with { Quests = [100] });
            Status(f,QuestStageStatus.Deferred);
            // 清空完成位后的第二次空Clear和不存在Remove均不应因为revision变化重复改资格。
            f.Sql("DELETE FROM character_completed_quests");
            f.Repository.UpdateProgress(f.Owner,Edit(f,CharacterProgressField.QuestClear,0));
            Status(f,QuestStageStatus.Deferred);
        }
        using (var f = Fixture())
        {
            Source(f); var before = f.Read();
            string proof = Scalar(f,"SELECT proof FROM character_quest_stage_witnesses");
            f.Sql("CREATE TRIGGER abort_invalidation BEFORE UPDATE OF status ON character_quest_stage_witnesses BEGIN SELECT RAISE(ABORT,'invalidate rollback'); END");
            Reject<SqliteException>(() => f.Repository.UpdateProgress(f.Owner,Edit(f,CharacterProgressField.Story,20)),"失效SQL故障必须回滚GM剧情编辑");
            That(QuestCompletionRepositoryTests.Same(before,f.Read()) && Scalar(f,"SELECT proof FROM character_quest_stage_witnesses") == proof,
                "GM进度和资格同事务回滚");
        }
        var reset = new CharacterProgressReset(r.Characters,r.Learning,growth);
        foreach (string kind in new[] { "same", "experience", "level", "grow" })
        {
            using var f = Fixture(); Source(f); var before = f.Read();
            var request = Edit(f,kind == "grow" ? CharacterProgressField.Grow : CharacterProgressField.Level,
                kind == "grow" ? 1 : kind == "level" ? 12 : before.Level);
            var plan = reset.Prepare(before,request);
            if (kind == "same") plan = plan with { Learning = plan.Learning with { Experience = before.Learning.Experience } };
            That(kind != "experience" || plan.Learning.Experience < before.Learning.Experience,
                "同等级真实重置仍重设XP到等级起点");
            f.Repository.ResetProgress(f.Owner,plan);
            Status(f,kind == "same" ? QuestStageStatus.Deferred : QuestStageStatus.Invalidated);
        }
        using (var f = Fixture())
        {
            Source(f); var before = f.Read(); var plan = reset.Prepare(before,Edit(f,CharacterProgressField.Level,12));
            f.Sql("CREATE TRIGGER abort_invalidation BEFORE UPDATE OF status ON character_quest_stage_witnesses BEGIN SELECT RAISE(ABORT,'reset rollback'); END");
            Reject<SqliteException>(() => f.Repository.ResetProgress(f.Owner,plan),"失效SQL故障回滚ResetProgress");
            That(QuestCompletionRepositoryTests.Same(before,f.Read()),"重置等级经验双树与Deferred同回滚");
            Status(f,QuestStageStatus.Deferred);
        }
        foreach (bool ticket in new[] { false,true })
        {
            var b = a with { Id = 103, Path = "fixture/103.qst", MinimumLevel = 12 };
            var d = c with { Id = 104, Path = "fixture/104.qst", MinimumLevel = 12,
                Definition = c.Definition! with { PrerequisiteGroups = [[103]] } };
            var m = n with { Id = 105, Path = "fixture/105.qst", MinimumLevel = 14,
                Definition = n.Definition! with { PrerequisiteGroups = [[103]] } };
            using var f = Fixture(candidate => {
                var state = SkillPointLedger.Synchronize(candidate.Job,candidate.GrowType,11,
                    candidate.InitialLearning with { Experience = growth.GetLevelStartExperience(11) },r.Learning);
                return candidate with { Level = 11, InitialLearning = state, Inventory = candidate.Inventory with {
                    Items = candidate.Inventory.Items.Add(new(0,new(InventoryListType.Main,65),
                        InventoryRewardGrantService.Create(r.ItemResources.Items.Get(10006007)) with {
                            ExpireTime = checked((int)DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()) })) } };
            },[a,c,n,b,d,m]);
            Source(f); var before = f.Read(); QuestQuickClearPlan? plan; string reason;
            bool prepared = ticket ? QuestQuickClearPlan.TryCreateTicket(before,Guid.NewGuid(),f.Catalog,
                QuestTicketSource.Create(before,new(InventoryListType.Main,65),r.ItemResources.Items.QuestTicket(10006007),
                    DateTimeOffset.UtcNow.ToUnixTimeSeconds()),ImmutableDictionary<int,byte>.Empty,out plan,out reason) :
                QuestQuickClearPlan.TryCreateManual(before,Guid.NewGuid(),f.Catalog,ImmutableDictionary<int,byte>.Empty,out plan,out reason);
            That(prepared && plan!.NewCompletedQuestIds.Contains(101),"构造真实手册/券跳过C: " + reason);
            f.Sql("CREATE TRIGGER abort_invalidation BEFORE UPDATE OF status ON character_quest_stage_witnesses BEGIN SELECT RAISE(ABORT,'skip rollback'); END");
            Reject<SqliteException>(() => f.Repository.CommitQuestQuickClear(f.Owner,plan!),"失效SQL故障回滚手册/券原动作");
            That(QuestCompletionRepositoryTests.Same(before,f.Read()) && Scalar(f,"SELECT count(*) FROM character_quest_skip_receipts") == "0",
                "失败跳过不扣券、不写完成或回执");
            Status(f,QuestStageStatus.Deferred); f.Sql("DROP TRIGGER abort_invalidation");
            f.Repository.CommitQuestQuickClear(f.Owner,plan!); Status(f,QuestStageStatus.Invalidated);
            That(Scalar(f,"SELECT count(*) FROM character_quest_stage_claims") == "0","跳过不发阶段额");
            var skipped = f.Read();
            f.Repository.ResetProgress(f.Owner,reset.Prepare(skipped,Edit(f,CharacterProgressField.Level,12))); Reconnect(f);
            That(f.Change(103,QuestLifecycleAction.Accept) is not null && f.Finish(ushort.MaxValue,103).Outcome == QuestFinishOutcome.Committed,
                "跳过之后真实B建立另一份新延期资格");
            That(f.Repository.CommitQuestQuickClear(f.Owner,plan!).Duplicate,"旧跳过回执精确重放");
            Status(f,QuestStageStatus.Invalidated);
            That(f.Repository.LoadQuestStageState(f.Owner,f.Read()).Witnesses.Single(row => row.SourceQuestId == 103).Status == QuestStageStatus.Deferred,
                "旧跳过receipt不清除后来真实B的新Deferred");
        }
        using (var f = Fixture())
        {
            Source(f); var before = f.Read(); var advanced = f.Repository.AdvanceAcceptedStoryLevel(f.Owner,before);
            That(advanced.AcceptedStoryLevel == before.Level,"普通剧情接受标记实际推进");
            Status(f,QuestStageStatus.Deferred); Reconnect(f);
            That(f.Change(101,QuestLifecycleAction.Accept) is not null && f.Finish(ushort.MaxValue,101).Receipt?.Commit.StageExperience > 0,
                "普通AdvanceAcceptedStoryLevel不当作GM编辑失效");
        }
        using (var f = Fixture())
        {
            var b = a with { Id = 103, Path = "fixture/103.qst" };
            That(QuestQuickClearPlan.TryCreateManual(f.Read(),Guid.NewGuid(),new(new Dictionary<int,StoryQuest> { [103] = b }),
                ImmutableDictionary<int,byte>.Empty,out var plan,out _),"来源之前可合法跳过独立任务B");
            f.Repository.CommitQuestQuickClear(f.Owner,plan!); Reconnect(f); Source(f);
            That(f.Change(101,QuestLifecycleAction.Accept) is not null && f.Finish(ushort.MaxValue,101).Receipt?.Commit.StageExperience > 0,
                "源前skip不阻止后来真实A建立并消费新资格");
        }
        using (var f = Fixture())
        {
            Source(f); f.Repository.UpdateProgress(f.Owner,Edit(f,CharacterProgressField.Story,20)); Reconnect(f);
            That(f.Change(101,QuestLifecycleAction.Accept) is not null && f.Finish(ushort.MaxValue,101).Receipt?.Commit.StageExperience == 0 && f.Read().Level == 11,
                "源后非自然编辑失效，重连后真实C不复活旧A");
            Status(f,QuestStageStatus.Invalidated);
        }
        using (var f = Fixture())
        {
            f.Repository.UpdateProgress(f.Owner,Edit(f,CharacterProgressField.Story,20)); Reconnect(f); Source(f);
            That(f.Change(101,QuestLifecycleAction.Accept) is not null && f.Finish(ushort.MaxValue,101).Receipt?.Commit.StageExperience > 0 && f.Read().Level == 13,
                "源前编辑不阻止后来真实A建立新资格");
        }
        using (var f = Fixture())
        {
            Source(f); f.Sql("DELETE FROM character_quest_stage_witnesses"); var before = f.Read(); Reconnect(f);
            That(f.Read().Learning.Experience == before.Learning.Experience && f.Change(101,QuestLifecycleAction.Accept) is not null &&
                f.Finish(ushort.MaxValue,101).Receipt?.Commit.StageExperience == 0 && f.Read().Level == 11,
                "旧普通receipt没有witness，选角不发奖且C不猜补历史A");
        }
        foreach (bool enough in new[] { false,true })
        {
            using var f = Fixture(); Source(f);
            if (enough)
            {
                var before = f.Read();
                // 临时库模拟延期期间其它自然成长的已提交结果；不调用会失效资格的GM入口。
                f.Sql($"UPDATE characters SET level=13,experience={growth.GetLevelStartExperience(13)},revision=revision+1; " +
                    $"UPDATE character_skill_points SET sp=sp+{growth.GetSpDelta(11,13)},tp=tp+{growth.GetTpDelta(11,13)}"); Reconnect(f);
            }
            That(f.Change(101,QuestLifecycleAction.Accept) is not null && f.Finish(ushort.MaxValue,101).Outcome == QuestFinishOutcome.Committed,"先真实消费目标");
            string proofs = Scalar(f,"SELECT proof FROM character_quest_stage_witnesses");
            string claims = Scalar(f,"SELECT group_concat(source_acceptance_id||':'||amount) FROM character_quest_stage_claims");
            var saved = f.Read(); f.Repository.ResetProgress(f.Owner,new(saved,11,saved.GrowType,SkillPointLedger.Synchronize(saved.Job,saved.GrowType,11,
                saved.Learning with { Experience = growth.GetLevelStartExperience(11) },r.Learning)));
            That(Scalar(f,"SELECT proof FROM character_quest_stage_witnesses") == proofs &&
                Scalar(f,"SELECT group_concat(source_acceptance_id||':'||amount) FROM character_quest_stage_claims") == claims,
                "实际GM降级保留Granted/Satisfied与claim，不重置消费");
        }
        using (var f = Fixture(_ => ClassChangeTests.CharacterFixture(r)))
        {
            // 高等级互转fixture以N更高等级建立同一真实A延期来源。
            var highN = n with { MinimumLevel = 60 };
            var highCatalog = new QuestCatalog(new[] { a,c,highN }.ToDictionary(row => row.Id));
            var highGraph = QuestStageGraph.Build(highCatalog);
            That(f.Change(100,QuestLifecycleAction.Accept) is not null,"互转前真实A接取");
            var beforeA = f.Read();
            var finish = QuestCompletionPlan.Prepare(beforeA,a,ushort.MaxValue,highCatalog,parameters,r.ItemResources.Items,r.ItemResources.GoldLimits,
                r.ItemResources.EpicPieces,growth,DateTimeOffset.UtcNow,stageGraph:highGraph,stageState:f.Repository.LoadQuestStageState(f.Owner,beforeA));
            f.Repository.CommitQuestCompletion(f.Owner,finish.Plan!); Status(f,QuestStageStatus.Deferred);
            var before = f.Read();
            var service = new ClassChangeService(r.ItemResources.Items,r.Characters,r.Learning,r.Quests,r.ItemResources.EpicPieces);
            var plan = service.Prepare(before,new(65,2,false),DateTimeOffset.UtcNow).Plan!;
            That(plan is not null,"已有真实资源互转fixture可规划");
            f.Sql("CREATE TRIGGER abort_invalidation BEFORE UPDATE OF status ON character_quest_stage_witnesses BEGIN SELECT RAISE(ABORT,'class rollback'); END");
            Reject<SqliteException>(() => f.Repository.CommitClassChange(f.Owner,plan!),"失效SQL故障回滚互转原动作");
            That(QuestCompletionRepositoryTests.Same(before,f.Read()),"互转扣券、grow、技能和Deferred整笔回滚");
            Status(f,QuestStageStatus.Deferred); f.Sql("DROP TRIGGER abort_invalidation");
            f.Repository.CommitClassChange(f.Owner,plan!); Status(f,QuestStageStatus.Invalidated);
        }
    }
}
