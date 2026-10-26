using DfoServer.Game.Characters;
using DfoServer.Game.Quests;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Quests;
using DfoServer.Game.Session;
using DfoServer.Persistence;
using DfoServer.Testing;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class QuestStageRepositoryTests
{
    public static void Run(TcpTestResources resources)
    {
        var growth = SelfTestCatalogs.Growth(resources.Archive, resources.Learning);
        var parameters = SelfTestCatalogs.QuestRewardParameters(resources.Archive);
        var a = QuestEligibilityTests.Quest(100) with { SubType = 1, Difficulty = "G", MinimumLevel = 1,
            Attributes = [], Rewards = [], Definition = new([]) { Job = "[all]" } };
        var c = a with { Id = 101, Path = "fixture/101.qst", Definition = a.Definition! with { PrerequisiteGroups = [[100]] } };
        var n = a with { Id = 102, Path = "fixture/102.qst", MinimumLevel = 13,
            Definition = a.Definition! with { PrerequisiteGroups = [[100]] } };
        using var f = new QuestOrdinaryCompletionTests.Fixture(resources.Towns, resources.Characters, resources.Learning,
            growth, parameters, resources.ItemResources, a, materialCount: 0, definitions: [a,c,n], active: [],
            configure: candidate => candidate with { Level = 11, InitialLearning = SkillPointLedger.Synchronize(
                candidate.Job, candidate.GrowType, 11, candidate.InitialLearning with {
                    Experience = growth.GetLevelStartExperience(11) }, resources.Learning) }, stages: QuestStageGraph.Build(new(new[] { a,c,n }.ToDictionary(row => row.Id))));
        That(f.Change(100, QuestLifecycleAction.Accept) is not null, "阶段A真实接取");
        That(f.Finish(ushort.MaxValue).Outcome == QuestFinishOutcome.Committed, "阶段A真实完成");
        That(f.Read().Level == 11, "A仍有同级C工作而延期");
        That(f.Change(101, QuestLifecycleAction.Accept) is not null, "阶段C真实接取");
        var result = f.Finish(ushort.MaxValue,101);
        That(result.Outcome == QuestFinishOutcome.Committed, "阶段C真实完成");
        That(f.Read().Level == 13 && f.Read().Learning.Experience == growth.GetLevelStartExperience(13),
            "C完成沿真实A延期资格补至13级起点");
        RunPersistence(resources);
        RunInvalidation(resources);
    }

    static void RunPersistence(TcpTestResources r)
    {
        var growth = SelfTestCatalogs.Growth(r.Archive,r.Learning);
        var parameters = SelfTestCatalogs.QuestRewardParameters(r.Archive);
        var a = QuestEligibilityTests.Quest(100) with { SubType = 1, Difficulty = "G", MinimumLevel = 1,
            Attributes = [], Rewards = [], Definition = new([]) { Job = "[all]" } };
        var c = a with { Id = 101, Path = "fixture/101.qst", Definition = a.Definition! with { PrerequisiteGroups = [[100]] } };
        var n = a with { Id = 102, Path = "fixture/102.qst", MinimumLevel = 13,
            Definition = a.Definition! with { PrerequisiteGroups = [[100]] } };
        QuestStageGraph Graph(StoryQuest[] definitions) => QuestStageGraph.Build(new(definitions.ToDictionary(row => row.Id)));
        QuestOrdinaryCompletionTests.Fixture Fixture(StoryQuest[] definitions, bool lost = false, ulong? experience = null, byte level = 11) =>
            new(r.Towns,r.Characters,r.Learning,growth,parameters,r.ItemResources,a,materialCount:0,
                definitions:definitions,active:[],loseResult:lost,configure: candidate => candidate with { Level = level,
                    InitialLearning = SkillPointLedger.Synchronize(candidate.Job,candidate.GrowType,level,
                        candidate.InitialLearning with { Experience = experience ?? growth.GetLevelStartExperience(level) },r.Learning) },stages:Graph(definitions));
        QuestStageState State(QuestOrdinaryCompletionTests.Fixture f) => f.Repository.LoadQuestStageState(f.Owner,f.Read());
        void Accept(QuestOrdinaryCompletionTests.Fixture f, ushort id) => That(f.Change(id,QuestLifecycleAction.Accept) is not null,$"阶段{id}真实接取");
        QuestCompletionCommit Finish(QuestOrdinaryCompletionTests.Fixture f, ushort id)
        {
            var result = f.Finish(ushort.MaxValue,id);
            That(result.Outcome == QuestFinishOutcome.Committed,$"阶段{id}真实完成: {result.Reason}");
            return result.Receipt!.Commit;
        }
        using (var f = Fixture([a,c,n]))
        {
            Accept(f,100); var first = Finish(f,100);
            That(first.OrdinaryExperience > 0 && first.StageExperience == 0 && first.GainedExperience == first.OrdinaryExperience &&
                State(f).Witnesses is [{ Status: QuestStageStatus.Deferred, TargetLevel: 13 }],"A普通入账与Deferred同时持久冻结");
            Accept(f,101); var before = f.Read(); string witnesses = Scalar(f,"SELECT group_concat(proof) FROM character_quest_stage_witnesses");
            f.Sql("CREATE TRIGGER abort_stage AFTER INSERT ON character_quest_stage_claims BEGIN SELECT RAISE(ABORT,'stage rollback'); END");
            Reject<SqliteException>(() => f.Finish(ushort.MaxValue,101),"阶段claim晚期失败");
            That(QuestCompletionRepositoryTests.Same(before,f.Read()) && f.Receipts == 1 &&
                Scalar(f,"SELECT count(*) FROM character_quest_stage_claims") == "0" &&
                Scalar(f,"SELECT group_concat(proof) FROM character_quest_stage_witnesses") == witnesses,"阶段失败回滚C普通XP/双树/完成位与witness/claim");
            f.Sql("DROP TRIGGER abort_stage"); var result = Finish(f,101);
            That(result.StageExperience > 0 && result.TotalRequestedExperience == result.OrdinaryExperience + result.ContractExperience + result.StageExperience &&
                result.GainedExperience == result.TotalRequestedExperience &&
                Scalar(f,"SELECT experience_amount FROM character_quest_completion_receipts WHERE quest_id=101") == result.TotalRequestedExperience.ToString(),"普通/契约/阶段/实际总额与receipt一致");
            That(f.Read().Learning.Trees.All(tree => tree.Sp == before.Learning.Trees.Single(old => old.Context == tree.Context).Sp + growth.GetSpDelta(11,13) &&
                tree.Tp == before.Learning.Trees.Single(old => old.Context == tree.Context).Tp + growth.GetTpDelta(11,13)),"最终等级同步双树SP/TP");
            That(State(f).Witnesses is [{ Status: QuestStageStatus.Granted }] && f.Receipts == 2 &&
                Scalar(f,"SELECT count(*) FROM character_quest_stage_claims") == "1","一次成功只落一笔固定来源claim");
        }
        foreach (bool deferred in new[] { true,false })
        using (var f = Fixture(deferred ? [a,c,n] : [a,n],lost:true))
        {
            Accept(f,100); Reject<IOException>(() => f.Finish(ushort.MaxValue),"真实Session完成返回丢失");
            var old = f.Session.Selected!; var durable = f.Read();
            Reject<InvalidOperationException>(() => f.Repository.LoadQuestStageState(f.Owner,old),"旧revision不能重读阶段状态");
            using (var writer = f.Open())
            using (var tx = writer.BeginTransaction(deferred:false))
            {
                var watch = System.Diagnostics.Stopwatch.StartNew(); var replay = Finish(f,100);
                That(replay.Duplicate && f.Lost is { SamePlan:true, LastDuplicate:true, StageLoads:1 } &&
                    watch.Elapsed < TimeSpan.FromSeconds(1),"原pending先同一Plan再只读receipt，不重新Load阻断恢复");
                tx.Rollback();
            }
            That(QuestCompletionRepositoryTests.Same(durable,f.Read()) && f.Receipts == 1 &&
                Scalar(f,"SELECT count(*) FROM character_quest_stage_claims") == (deferred ? "0" : "1"),"丢返回不重复延期或阶段奖励");
        }
        using (var f = Fixture([a,c,n]))
        {
            Accept(f,100); Finish(f,100); f.Session.ReturnToRoster();
            var reopened = new CharacterRepository(f.OpenPath,f.Catalog);
            var session = new GameSession(reopened,r.Towns,f.Owner,stageGraph:Graph([a,c,n]));
            try
            {
                session.Select(0); session.MarkReady();
                That(session.Selected!.Level == 11 && State(f).Witnesses.Single().Status == QuestStageStatus.Deferred,"重连仅恢复延期，选角不发奖");
                That(session.ChangeQuest(101,QuestLifecycleAction.Accept,f.Catalog,r.ItemResources.Items,r.ItemResources.GoldLimits,r.ItemResources.EpicPieces) is not null,"重连后C真实接取");
                var result = session.FinishQuest(101,ushort.MaxValue,f.Catalog,parameters,r.ItemResources.Items,r.ItemResources.GoldLimits,r.ItemResources.EpicPieces,growth);
                That(result.Outcome == QuestFinishOutcome.Committed && f.Read().Level == 13,"重开仓储和新Session沿持久A补差");
            }
            finally { session.End(); }
        }
        var b = a with { Id = 103, Path = "fixture/103.qst" };
        var eitherC = c with { Definition = c.Definition! with { PrerequisiteGroups = [[100],[103]] } };
        var eitherN = n with { Definition = n.Definition! with { PrerequisiteGroups = [[100],[103]] } };
        foreach (bool enough in new[] { false,true })
        using (var f = Fixture([a,b,eitherC,eitherN],lost:enough))
        {
            if (enough) f.Lost!.CompletionLossCall = 3;
            Accept(f,100); Finish(f,100); Accept(f,103); Finish(f,103);
            That(State(f).Witnesses.Count(row => row.Status == QuestStageStatus.Deferred) == 2,"两个真实来源同T延期");
            Accept(f,101);
            if (enough)
            {
                // 临时库模拟延期期间其它成长的已提交结果，完整同步等级与双树增量。
                f.Sql($"UPDATE characters SET level=13,experience={growth.GetLevelStartExperience(13)},revision=revision+1; UPDATE character_skill_points SET sp=sp+{growth.GetSpDelta(11,13)},tp=tp+{growth.GetTpDelta(11,13)}");
                f.Session.ReturnToRoster(); f.Session.Select(0); f.Session.MarkReady();
            }
            QuestCompletionPlan? pending = null;
            if (enough)
            {
                Reject<IOException>(() => f.Finish(ushort.MaxValue,101),"多pending的Satisfied0提交返回丢失");
                pending = f.Lost!.LastPlan;
            }
            var result = Finish(f,101); var state = State(f);
            if (enough) That(result.Duplicate && ReferenceEquals(pending,f.Lost!.LastPlan) && f.Lost.StageLoads == 3 &&
                Scalar(f,"SELECT source_acceptance_id FROM character_quest_stage_claims") == state.Witnesses.OrderBy(row => row.SourceCompletionRevision).First().SourceAcceptanceId.ToString("N"),
                "多来源0额沿原冻结Plan重放，JSON重载与精确pending不重选来源");
            That(state.Witnesses.All(row => row.Status is QuestStageStatus.Granted or QuestStageStatus.Satisfied) &&
                state.Witnesses.Count(row => row.Status == QuestStageStatus.Granted) == (enough ? 0 : 1) &&
                Scalar(f,"SELECT count(*) FROM character_quest_stage_claims") == "1","同T业务闭合所有来源，只写一次claim");
            That((result.StageExperience == 0) == enough && Scalar(f,"SELECT amount FROM character_quest_stage_claims") == result.StageExperience.ToString(),"自然已够落Satisfied0，正额只到账一次");
        }
        using (var f = Fixture([a,n with { MinimumLevel = 12 }],experience:growth.GetLevelStartExperience(12)-1))
        {
            Accept(f,100); var result = Finish(f,100);
            That(result.StageExperience == 0 && State(f).Witnesses is [{ Status: QuestStageStatus.Satisfied }] &&
                Scalar(f,"SELECT amount FROM character_quest_stage_claims") == "0","本次普通经验跨目标也原子消费Satisfied0");
        }
        foreach (string path in new[] { "$.Evidence[0].PrerequisiteGroup[0]","$.BoundaryQuestIds[0]" })
        using (var f = Fixture([a,c,n]))
        {
            Accept(f,100); Finish(f,100); Accept(f,101); var before = f.Read();
            var plan = QuestCompletionPlan.Prepare(before,c,ushort.MaxValue,f.Catalog,parameters,r.ItemResources.Items,r.ItemResources.GoldLimits,
                r.ItemResources.EpicPieces,growth,DateTimeOffset.UtcNow,stageGraph:Graph([a,c,n]),stageState:State(f)).Plan!;
            f.Sql($"UPDATE character_quest_stage_witnesses SET proof=json_set(proof,'{path}',999)");
            Reject<InvalidOperationException>(() => f.Repository.CommitQuestCompletion(f.Owner,plan),"内层proof篡改不等于冻结Expected");
            That(QuestCompletionRepositoryTests.Same(before,f.Read()) && f.Receipts == 1,"proof冲突整笔普通领奖不写");
        }
        using (var f = Fixture([a,c,n]))
        {
            Accept(f,100); Finish(f,100); Accept(f,101); var before = f.Read();
            f.Sql("UPDATE character_quest_stage_witnesses SET proof=json_set(proof,'$.Evidence[0].PrerequisiteGroup[0]',999)");
            Reject<InvalidDataException>(() => f.Finish(ushort.MaxValue,101),"规划前不可信Deferred依据不吞成普通成功");
            That(QuestCompletionRepositoryTests.Same(before,f.Read()) && f.Receipts == 1,"不可信来源复验也与普通奖励同事务回滚");
        }
        using (var f = Fixture([a,c,n]))
        {
            Accept(f,100); Finish(f,100); Accept(f,101);
            // 旧来源余额仍可做零升级普通成长，但阶段跨级时余额溢出必须整笔拒绝。
            f.Sql("UPDATE character_skill_points SET sp=65535; DELETE FROM character_skill_point_sources; UPDATE characters SET revision=revision+1");
            f.Session.ReturnToRoster(); f.Session.Select(0); f.Session.MarkReady();
            var before = f.Read(); var result = f.Finish(ushort.MaxValue,101);
            That(result.Outcome == QuestFinishOutcome.Rejected && result.Reason == "阶段成长被拒绝: PointOverflow" &&
                QuestCompletionRepositoryTests.Same(before,f.Read()) && f.Receipts == 1 &&
                State(f).Witnesses.Single().Status == QuestStageStatus.Deferred && Scalar(f,"SELECT count(*) FROM character_quest_stage_claims") == "0",
                "已冻结阶段的最终成长失败不提交C普通奖励/完成或消费延期资格");
        }
        foreach (bool satisfied in new[] { false,true })
        {
            var next = n with { MinimumLevel = satisfied ? 12 : 13, Definition = n.Definition! with { PrerequisiteGroups = [[100],[103]] } };
            using var f = Fixture([a,b,next],experience:satisfied ? growth.GetLevelStartExperience(12)-1 : null);
            var original = f.Read(); Accept(f,100); Finish(f,100);
            That(State(f).Witnesses.Single().Status == (satisfied ? QuestStageStatus.Satisfied : QuestStageStatus.Granted),"先前目标已消费");
            // 临时库模拟后续等级回退；资格终态必须跨重连仍然防重。
            f.Sql($"UPDATE characters SET level=11,experience={growth.GetLevelStartExperience(11)},revision=revision+1");
            foreach (var tree in original.Learning.Trees) f.Sql($"UPDATE character_skill_points SET sp={tree.Sp},tp={tree.Tp} WHERE context={tree.Context}");
            f.Session.ReturnToRoster(); f.Session.Select(0); f.Session.MarkReady();
            Accept(f,103); var result = Finish(f,103);
            That(result.StagePlan!.Claim is null && result.StageExperience == 0 && State(f).Witnesses.Single(row => row.SourceQuestId == 103).Status == QuestStageStatus.Satisfied &&
                Scalar(f,"SELECT count(*) FROM character_quest_stage_claims") == "1","已有Granted/Satisfied目标的新来源闭合，无第二claim或降级重领");
            string source = Scalar(f,"SELECT source_acceptance_id FROM character_quest_stage_claims");
            string trigger = Scalar(f,"SELECT trigger_acceptance_id FROM character_quest_stage_claims");
            Reject<SqliteException>(() => f.Sql($"INSERT INTO character_quest_stage_claims SELECT character_id,account,source_acceptance_id,'{trigger}',target_level+1,before_experience,after_experience,amount,revision FROM character_quest_stage_claims WHERE source_acceptance_id='{source}'"),"同源不同T也被唯一约束拒绝");
            Reject<SqliteException>(() => f.Sql($"INSERT INTO character_quest_stage_claims SELECT character_id,account,'{result.AcceptanceId:N}',trigger_acceptance_id,target_level,before_experience,after_experience,amount,revision FROM character_quest_stage_claims"),"同T不同源也被唯一约束拒绝");
        }
        var tp = a with { RewardType = "[tp]" };
        var high = n with { MinimumLevel = 51 };
        using (var f = Fixture([tp,high],level:49))
        {
            var before = f.Read(); Accept(f,100); var result = Finish(f,100);
            That(result.Character.Level == 51 && result.TpAmount == 2 && result.Character.Learning.BonusTp == before.Learning.BonusTp + 2 &&
                result.Character.Learning.Trees.All(tree => tree.Sp == before.Learning.Trees.Single(old => old.Context == tree.Context).Sp + growth.GetSpDelta(49,51) &&
                    tree.Tp == before.Learning.Trees.Single(old => old.Context == tree.Context).Tp + growth.GetTpDelta(49,51) + 2),"跨50级阶段成长与TP赠点只同步一次双树");
        }
        using (var f = Fixture([a,c with { Attributes = ["not give exp quest"] },n]))
        {
            Accept(f,100); Finish(f,100); Accept(f,101); var result = Finish(f,101);
            That(result.ExperienceReward.Amount == 0 && result.StageExperience > 0 && result.GainedExperience == result.StageExperience && result.Character.Level == 13,
                "禁经验真实C只续判A来源，普通0与正阶段总额一致");
        }
        using (var f = Fixture([a,n]))
        {
            f.Sql($"INSERT INTO account_wallets(account,revision,premium_contracts) VALUES('ordinary-quest',0,'{{\"84\":{DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()}}}') ON CONFLICT(account) DO UPDATE SET premium_contracts=excluded.premium_contracts");
            f.Session.ReturnToRoster(); f.Session.Select(0); f.Session.MarkReady(); Accept(f,100);
            var before = f.Read();
            That(before.Inventory.AccountWallet.ContractExpiry(84) > DateTimeOffset.UtcNow.ToUnixTimeSeconds(),"契约样例实际载入有效期限");
            var plan = QuestCompletionPlan.Prepare(before,a,ushort.MaxValue,f.Catalog,parameters,r.ItemResources.Items,r.ItemResources.GoldLimits,
                r.ItemResources.EpicPieces,growth,DateTimeOffset.UtcNow,growthContract:new(100,0,[]),stageGraph:Graph([a,n]),stageState:State(f)).Plan!;
            var result = f.Repository.CommitQuestCompletion(f.Owner,plan);
            That(result.OrdinaryExperience > 0 && result.ContractExperience == result.OrdinaryExperience &&
                result.GainedExperience == result.TotalRequestedExperience && result.GainedExperience == growth.GetLevelStartExperience(13)-before.Learning.Experience &&
                result.StageExperience == result.TotalRequestedExperience-result.OrdinaryExperience-result.ContractExperience,"普通与有效契约先入账，阶段补差不再乘契约");
        }
        VerifyLegacySnapshot(r,a,n,growth,parameters);
    }

    static void VerifyLegacySnapshot(TcpTestResources r,StoryQuest a,StoryQuest n,
        DfoServer.GameWorld.Characters.CharacterGrowthCatalog growth,QuestRewardParameterCatalog parameters)
    {
        using var f = new QuestOrdinaryCompletionTests.Fixture(r.Towns,r.Characters,r.Learning,growth,parameters,r.ItemResources,a,
            materialCount:0,definitions:[a,n],active:[]);
        That(f.Change(100,QuestLifecycleAction.Accept) is not null,"旧快照路径真实接取");
        var plan = f.Prepare(ushort.MaxValue).Plan!;
        var expected = plan.Expected;
        var reward = QuestExperiencePolicy.Prepare(expected,a,parameters,out int penalty);
        string legacy = System.Text.Json.JsonSerializer.Serialize(new { plan.QuestId,plan.AcceptanceId,plan.QuestSlot,plan.ConditionType,
            plan.RewardSelector,plan.Multiplier,ExperienceReward=reward,plan.GoldAmount,goldBase=0,goldMultiple=100,penalty,plan.Consumed,
            plan.Rewards,plan.Currencies,FrozenAt=plan.FrozenAt,plan.DailyPeriod,plan.AccountQuest,plan.AuthoritativeClear,plan.Answer,
            Learning=plan.TargetLearning,Level=plan.TargetLevel,GrowType=plan.TargetGrowType,EquipmentSlotFlags=plan.TargetEquipmentSlotFlags });
        That(plan.StagePlan is null && plan.Snapshot == legacy,"无阶段路径按旧匿名对象顺序保持Snapshot逐字节一致");
        f.Repository.CommitQuestCompletion(f.Owner,plan);
        var beforeMigration = f.Read();
        f.Sql("UPDATE character_quest_completion_receipts SET rowid=17,rule_id='legacy-ordinary'; DROP TABLE character_quest_stage_claims; DROP TABLE character_quest_stage_witnesses; ALTER TABLE characters DROP COLUMN daily_booster_usage; ALTER TABLE characters DROP COLUMN selected_skill_tree; PRAGMA user_version=52");
        That(Scalar(f,"PRAGMA user_version") == "52" &&
            Scalar(f,"SELECT count(*) FROM pragma_table_info('characters') WHERE name='daily_booster_usage'") == "0",
            "旧receipt迁移输入为真实v52缺每日礼盒列结构");
        string rows = Scalar(f,"SELECT rowid||':'||rule_id||':'||plan_snapshot FROM character_quest_completion_receipts");
        var reopened = new CharacterRepository(f.OpenPath,f.Catalog);
        That(Scalar(f,"SELECT daily_booster_usage FROM characters") == "[]" &&
            QuestCompletionRepositoryTests.Same(beforeMigration,reopened.LoadCharacter(f.Owner,beforeMigration.Slot)),
            "v52升级默认空每日礼盒账本，保留同owner角色完整资产与revision");
        var replay = reopened.CommitQuestCompletion(f.Owner,plan);
        That(replay.Duplicate && replay.ProjectionCurrent && Scalar(f,"SELECT rowid||':'||rule_id||':'||plan_snapshot FROM character_quest_completion_receipts") == rows &&
            reopened.LoadQuestStageState(f.Owner,f.Read()).Witnesses.IsEmpty,"v52真实旧receipt升级保留rowid/rule_id/Snapshot且不猜补来源");
    }

    static string Scalar(QuestOrdinaryCompletionTests.Fixture fixture,string sql)
    {
        using var db = fixture.Open(); using var command = db.CreateCommand(); command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar(),System.Globalization.CultureInfo.InvariantCulture)!;
    }
}
