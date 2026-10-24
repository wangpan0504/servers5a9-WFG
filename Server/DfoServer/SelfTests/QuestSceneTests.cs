using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Quests;
using DfoServer.Game.Session;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Persistence;
using DfoServer.Testing;
using Microsoft.Data.Sqlite;
using PvfLib;
using static DfoServer.Testing.TestCheck;
using static DfoServer.QuestCompletionRepositoryTests;

namespace DfoServer;

internal static class QuestSceneTests
{
    public static void Run(TownCatalog towns, PvfArchive pvf, CharacterCatalog characters, ItemResources items,
        QuestCatalog? sharedQuests = null, SkillLearningCatalog? sharedSkills = null)
    {
        int checks = Count;
        var quests = sharedQuests ?? global::DfoServer.Testing.SelfTestCatalogs.Quests(pvf); var skills = sharedSkills ?? global::DfoServer.Testing.SelfTestCatalogs.SkillLearning(pvf);
        var growth = global::DfoServer.Testing.SelfTestCatalogs.Growth(pvf, skills); var parameters = global::DfoServer.Testing.SelfTestCatalogs.QuestRewardParameters(pvf);
        var dungeons = global::DfoServer.Testing.SelfTestCatalogs.Dungeons(pvf); var rooms = global::DfoServer.Testing.SelfTestCatalogs.Rooms(pvf); var kills = global::DfoServer.Testing.SelfTestCatalogs.KillExperience(pvf);
        DungeonSpecificCardRewardsTests.RunResources(towns, pvf, characters, items, quests, skills);
        VerifyRepeatedPassiveHunt(towns, characters, items, quests, skills, growth, dungeons, rooms, kills);
        VerifyOuterGentCages(towns, characters, items, quests, skills, growth, dungeons, rooms, kills);
        VerifyBossRoomAntNests(towns, characters, items, quests, skills, growth, dungeons, rooms, kills);
        foreach (var mode in new[] { "normal","lost","completedParent" })
        {
            bool lost = mode == "lost";
            using var f = new Fixture(towns,characters,skills,growth,quests,lost);
            That(f.Session.LeaveRoom(), "正常剧情从赛丽亚房间回城");
            That(Change(3146,QuestLifecycleAction.Accept)?.Commit is not null && Change(3157,QuestLifecycleAction.Accept)?.Commit is not null,
                "真实Lv3父任务及首个隐藏子任务按城镇对话顺序接取");
            That(Change(3054,QuestLifecycleAction.Accept)?.Commit is null, "完成第一子任务前不能提前接取第二子任务");
            That(f.Session.Travel(new(38,2,1085,249,5,38,0,0)), "真实Lv3剧情进入资源中的副本入口");
            var opening = f.Session.EnterFormalDungeon(3,3146,0,dungeons,rooms);
            That(opening is not null && opening.Entry.RouteQuestId == 3146 && opening.Snapshot.CurrentMap.Id == 76131,
                "父任务自己的quest connection选择真实六房路线");
            var loaded = f.Session.CompleteFormalDungeonLoading()!;
            var before = f.Read(); var online = f.Session.Selected!;
            That(Observe(13099,false) is null && Observe(13098,true) is null && Same(before,f.Read()),
                "校验失败或不存在的物件不推进任务");
            if (!lost)
            {
                f.Sql("CREATE TRIGGER scene_abort AFTER INSERT ON character_quest_lifecycle_receipts WHEN NEW.scene_event IS NOT NULL BEGIN SELECT RAISE(ABORT,'scene rollback'); END");
                TestCheck.Reject<SqliteException>(() => Observe(13099,true), "场景事件末段SQL失败");
                That(Same(before,f.Read()) && ReferenceEquals(online,f.Session.Selected), "被动物件进度和场景回执一起回滚");
                f.Sql("DROP TRIGGER scene_abort");
            }
            else TestCheck.Reject<IOException>(() => Observe(13099,true), "场景事件已保存但返回丢失");
            var progress = Observe(13099,true)!;
            That(progress.Commit is { ProjectionCurrent: true } && progress.Commit.Duplicate == lost &&
                f.Read().ActiveMainQuests.Single(row => row.QuestId == 3157).ConditionState == 0 &&
                f.Read().Learning.Experience == before.Learning.Experience && f.Read().Inventory.Gold == before.Inventory.Gold &&
                Observe(13099,true) is null && f.Session.CanPublishQuestLifecycle(progress),
                "真实唯一被动物件只推进一次任务；结果丢失复用冻结计划，无击杀经验和金币");
            var child = Finish(3157);
            That(child.Receipt is { Phase: SessionPhase.Dungeon } && child.Receipt.Commit is { ConditionType: 6, Rewards.Length: 0, ExperienceReward.Amount: 0 } &&
                f.Session.CanPublishQuestCompletion(child.Receipt) && f.Read().CompletedQuests.Contains(3157) && !f.Read().CompletedQuests.Contains(3146),
                "实际被动物件隐藏子任务在同一副本交付，零奖励完成不提前完成父任务");
            That(Change(3054,QuestLifecycleAction.Accept)?.Commit is not null && f.Session.Phase == SessionPhase.Dungeon &&
                f.Session.DungeonSnapshot!.Identity.InstanceId == loaded.Snapshot.Identity.InstanceId,
                "原生顺序入口在原父实例中接下一子任务，不改地图或复用旧接取");
            That(Change(3157,QuestLifecycleAction.Accept)?.Commit is null && Finish(3054).Outcome == QuestFinishOutcome.Rejected,
                "已完成子任务不能重接，清图子任务没有凭据不能领奖");
            FormalDungeonReceipt receipt = loaded;
            var path = new (byte X,byte Y)[] { (0,1),(1,1),(2,1),(3,1),(4,1) };
            foreach (var point in path)
            {
                foreach (var monster in receipt.Monsters.Monsters.Where(row => row.IsBlocking).ToArray())
                    That(f.Session.ObserveFormalDungeonDeath(FormalDungeonSessionTests.Observation(receipt,monster),kills,growth).Outcome == DungeonRunOutcome.Accepted,
                        "任务路线使用真实服务端死亡事实推进房间");
                That(f.Session.MoveFormalDungeon(point.X,point.Y) is not null, "任务路线按实际连接换房");
                receipt = f.Session.CompleteFormalDungeonLoading()!;
            }
            foreach (var monster in receipt.Monsters.Monsters.Where(row => row.IsBlocking).OrderBy(row => row.SpawnType == 3).ToArray())
            {
                var result = f.Session.ObserveFormalDungeonDeath(FormalDungeonSessionTests.Observation(receipt,monster),kills,growth);
                That(result.Outcome == DungeonRunOutcome.Accepted, $"剧情Boss房提交真实死亡与任务清图：{monster.MonsterId}/{monster.ObjectKey}/{result.Outcome}");
            }
            That(f.Session.ReadClearedQuest(3054)?.Clear is { MapId: 76136 } && f.Session.Phase == SessionPhase.Settlement,
                "中途新接子任务获得同实例末次死亡的持久清图凭据");
            // 复现da：清图后没有33/34，断线回城时仍保留本次接取的真实清图凭据。
            That(f.Session.ReturnFromFormalDungeon() is not null,"零奖励清图子任务未交付时可正常回城");
            if (mode == "completedParent")
            {
                f.Sql("INSERT INTO character_completed_quests(character_id,quest_id,daily_period,answer) SELECT id,3146,0,NULL FROM characters; DELETE FROM character_active_main_quests WHERE quest_id=3146");
                f.Session.ReturnToRoster(); f.Session.Select(0); f.Session.MarkReady();
                var orphanBefore = f.Read();
                var recovered = f.Session.FinishReadyQuestChildren(quests,parameters,items.Items,items.GoldLimits,items.EpicPieces,growth);
                var orphanAfter = f.Read();
                That(recovered?.Commit is { ConditionType: 4, Clear: null, ExperienceReward.Amount: 0, GoldAmount: 0, Consumed.Length: 0 } &&
                    orphanAfter.CompletedQuests.Contains(3054) && orphanAfter.CompletedQuests.Contains(3146) &&
                    orphanAfter.ActiveMainQuests.Length == 0 && Same(orphanBefore with { Revision = orphanAfter.Revision,
                        ActiveMainQuests = orphanAfter.ActiveMainQuests, CompletedQuests = orphanAfter.CompletedQuests },orphanAfter),
                    "实际持久清图已存在时仍只归档已完成父任务的终末子项，保留死亡经验与全部资产");
                continue;
            }
            f.Session.ReturnToRoster(); f.Session.Select(0); f.Session.MarkReady();
            var last = f.Session.FinishReadyQuestChildren(quests,parameters,items.Items,items.GoldLimits,items.EpicPieces,growth);
            That(last?.Commit is { Rewards.Length: 0, ExperienceReward.Amount: 0 } &&
                f.Read().ActiveMainQuests is [{ QuestId: 3146, ConditionState: 0 }], "实际两个子任务真实完成后父任务同事务归零");
            var parent = Finish(3146);
            if (parent.Outcome != QuestFinishOutcome.Committed)
            {
                var diagnostic = QuestCompletionPlan.Prepare(f.Session.Selected!,quests.Get(3146),ushort.MaxValue,quests,parameters,
                    items.Items,items.GoldLimits,items.EpicPieces,growth,DateTimeOffset.UtcNow);
                throw new InvalidOperationException($"父任务交付拒绝：{parent.Outcome}，{diagnostic.Reason}");
            }
            That(parent.Receipt?.Commit is { ConditionType: 44 } && f.Read().CompletedQuests.Contains(3146) &&
                f.Read().ActiveMainQuests.Length == 0 && Finish(3146).Outcome == QuestFinishOutcome.AlreadyCompleted,
                "资源场景对话交付父任务仍经C2S34原子发自身奖励，只能领取一次");
            That(f.Session.Phase == SessionPhase.Town, "父任务按真实子完成记录在城镇交付，不额外要求副本结算或常驻剧情NPC");
            var side = quests.Get(3147) with { Id = 29900, Grade = "[side story]" };
            var mixed = new QuestCatalog(quests.All.Append(side).ToDictionary(row => row.Id));
            var continuing = f.Read() with { ActiveMainQuests = [new(0,3147,1) { AcceptanceId = Guid.NewGuid() },
                new(1,29900,1) { AcceptanceId = Guid.NewGuid() }] };
            var successor = DungeonQuestContinuationRules.Resolve(continuing,opening!.Entry,mixed,dungeons,[6],out _);
            That(successor is { Quest.QuestId: 3147, DungeonId: 6 },"相同前置的已接取支线不与真实epic主线争抢转场");
            That(DungeonQuestContinuationRules.Resolve(continuing with { ActiveMainQuests = [continuing.ActiveMainQuests[1]] },
                opening.Entry,mixed,dungeons,[6],out _) is null,"只有已接取支线时不能将其冒充下一主线");
            f.Session.ReturnToRoster(); f.Session.Select(0); f.Session.MarkReady();
            That(Same(f.Session.Selected!,f.Read()) && f.Read().CompletedQuests.Contains(3054), "整条实际Lv3父子链重登完整恢复");

            QuestLifecycleReceipt? Change(ushort id,QuestLifecycleAction action) => f.Session.ChangeQuest(id,action,quests,items.Items,items.GoldLimits,items.EpicPieces);
            QuestLifecycleReceipt? Observe(uint id,bool checksum) => f.Session.ObserveFormalPassiveDeath(id,checksum,new string('B',64),rooms);
            QuestFinishResult Finish(ushort id) => f.Session.FinishQuest(id,ushort.MaxValue,quests,parameters,items.Items,items.GoldLimits,items.EpicPieces,growth);
        }
        VerifyPassiveSettlement(towns, characters, items, quests, skills, growth, dungeons, rooms, kills,
            DungeonClearExperienceCatalog.Load(pvf),DungeonDropRuleCatalog.Load(pvf));
        VerifyRoomProgress(towns,characters,items,quests,skills,growth,dungeons,rooms,kills,parameters);
        VerifyTerminalLayerReentry(towns,characters,items,quests,skills,growth,dungeons,rooms,kills);
        VerifyAganzoSpice(towns,characters,items,quests,skills,growth,dungeons,rooms,kills);
        VerifyMetroCages(towns,characters,items,quests,skills,growth,dungeons,rooms,kills,parameters);
        QuestPassiveClearTests.Run(towns,characters,items,quests,skills,growth,dungeons,rooms,kills,
            global::DfoServer.Testing.SelfTestCatalogs.ClearExperience(pvf),parameters,global::DfoServer.Testing.SelfTestCatalogs.DropRules(pvf),global::DfoServer.Testing.SelfTestCatalogs.CardPrices(pvf));
        Issue153CinematicLayerTests.Run(towns,characters,items,quests,skills,growth,dungeons,rooms,
            global::DfoServer.Testing.SelfTestCatalogs.ClearExperience(pvf),parameters);
        Issue153RemainingTests.Run(towns,characters,items,quests,skills,growth,dungeons,rooms,
            global::DfoServer.Testing.SelfTestCatalogs.ClearExperience(pvf),parameters);
        Issue153MermaidClearTests.Run(towns,characters,items,quests,skills,growth,dungeons,rooms,kills,
            global::DfoServer.Testing.SelfTestCatalogs.ClearExperience(pvf),parameters);
        PreyIsysSinQuestTests.RunSession(towns,characters,items,quests,skills,growth,dungeons,rooms,kills,
            global::DfoServer.Testing.SelfTestCatalogs.ClearExperience(pvf),parameters);
        NonoraDungeonEntryTests.Run(towns,pvf,characters,items,quests,skills,growth,dungeons,rooms);
        Console.WriteLine($"PASS {Count-checks} 实际父子剧情、物件结算、清图交付与恢复断言（非实机）");
    }

    public static void RunStageBudgets(TcpTestResources r)
    {
        int checks = Count;
        var quests=r.Quests; var growth=SelfTestCatalogs.Growth(r.Archive,r.Learning);
        var parameters=SelfTestCatalogs.QuestRewardParameters(r.Archive);
        var dungeons=SelfTestCatalogs.Dungeons(r.Archive); var rooms=SelfTestCatalogs.Rooms(r.Archive);
        var kills=SelfTestCatalogs.KillExperience(r.Archive); var clears=SelfTestCatalogs.ClearExperience(r.Archive);
        var graph=QuestStageGraph.Build(quests);
        foreach (var scenario in new[] {(Quest:3149,Next:3150,Level:(byte)11,Start:80875UL,Target:164460UL),
            (Quest:3150,Next:3151,Level:(byte)13,Start:164460UL,Target:303835UL)})
        {
            var ordinary=Complete(false); var stage=Complete(true);
            That(ordinary.Character.Learning.Experience < scenario.Target && ordinary.StageExperience == 0,
                $"{scenario.Quest}自然一轮普通奖励仍不足下一门槛");
            That(stage.Character.Learning.Experience == scenario.Target && stage.StageExperience == scenario.Target-ordinary.Character.Learning.Experience &&
                QuestEligibility.Evaluate(stage.Character,quests.Get(scenario.Next),quests).Eligible,
                $"{scenario.Quest}阶段只补精确{scenario.Target}并立即可接{scenario.Next}");

            QuestCompletionCommit Complete(bool enabled)
            {
                int[] done=[3054,3145,3146,3147,3148,3157,4873,4875,..Enumerable.Range(3149,scenario.Quest-3149)];
                var entrance=MiddleQuestEntryTests.Entrance(r.Towns,quests.Get(scenario.Quest),checked((uint)quests.Get(scenario.Quest).DungeonIndex!.Value));
                using var f=new Fixture(r.Towns,r.Characters,r.Learning,growth,quests,false,scenario.Level,done,
                    position:entrance.Position,stages:enabled ? graph : null);
                That(f.Read().Learning.Experience == scenario.Start,"预算从原级0%开始，没有契约或额外刷图");
                That(f.Session.ChangeQuest(checked((ushort)scenario.Quest),QuestLifecycleAction.Accept,quests,r.ItemResources.Items,
                    r.ItemResources.GoldLimits,r.ItemResources.EpicPieces)?.Commit is not null,"预算真实接取");
                var opening=f.Session.EnterFormalDungeon(checked((uint)quests.Get(scenario.Quest).DungeonIndex!.Value),checked((uint)scenario.Quest),0,dungeons,rooms)!;
                var path=QuestStoryRouteTests.Path(opening.Entry.Maze.StaticTopology,opening.Entry.StartMap.Position,opening.Entry.BossMap.Position);
                That(path.Length == 6,"预算经过真实六房路线");
                var receipt=f.Session.CompleteFormalDungeonLoading()!;
                ulong killStart=f.Read().Learning.Experience;
                foreach(var point in path)
                {
                    foreach(var monster in receipt.Monsters.Monsters.Where(row=>row.CanReportDeath).OrderBy(row=>row.IsBoss))
                        That(f.Session.ObserveFormalDungeonDeath(FormalDungeonSessionTests.Observation(receipt,monster),kills,growth).Outcome == DungeonRunOutcome.Accepted,
                            "预算只提交本房真实敌方死亡");
                    if(point == opening.Entry.BossMap.Position) break;
                    var next=path[Array.IndexOf(path,point)+1];
                    That(f.Session.MoveFormalDungeon(checked((byte)next.X),checked((byte)next.Y)) is not null,"预算真实门墙换房");
                    receipt=f.Session.CompleteFormalDungeonLoading()!;
                }
                ulong afterKills=f.Read().Learning.Experience;
                That(f.Session.Phase == SessionPhase.Settlement,"预算真实Boss死亡进入结算");
                That(f.Session.SettleFormalDungeon(f.Read().Id,99,99,[1,2,3,4],dungeons,clears,growth).Outcome == DungeonRunOutcome.Accepted,"预算原46结算经验");
                ulong afterClear=f.Read().Learning.Experience;
                var result=f.Session.FinishQuest(checked((ushort)scenario.Quest),ushort.MaxValue,quests,parameters,r.ItemResources.Items,
                    r.ItemResources.GoldLimits,r.ItemResources.EpicPieces,growth);
                That(result.Outcome == QuestFinishOutcome.Committed,$"预算真实普通34交付：{result.Reason}");
                var commit=result.Receipt!.Commit;
                Console.WriteLine($"BUDGET quest={scenario.Quest} stage={enabled} start={killStart} kill={afterKills-killStart} clear={afterClear-afterKills} ordinary={commit.OrdinaryExperience} contract={commit.ContractExperience} topup={commit.StageExperience} final={commit.Character.Learning.Experience}");
                That(QuestCompletionRepositoryTests.Same(commit.Character,f.Read()) &&
                    f.Query("SELECT count(*) FROM character_quest_completion_receipts") is 1L,"预算完整双树资产及领奖SQL一致");
                return commit;
            }
        }
        Console.WriteLine($"PASS {Count-checks} 两个真实Scene自然一轮预算断言（非实机）");
    }

    // 三个真实笼子分别由所在地图唯一寻址；合成33不证明客户端自然触发，也不借dummy死亡完成任务。
    static void VerifyMetroCages(TownCatalog towns,CharacterCatalog characters,ItemResources items,QuestCatalog quests,
        SkillLearningCatalog skills,CharacterGrowthCatalog growth,DungeonCatalog dungeons,DungeonRoomCatalog rooms,
        DungeonKillExperienceCatalog kills,QuestRewardParameterCatalog parameters)
    {
        var quest = quests.Get(3959);
        That(quest.MinimumLevel == 88 && quest.Prerequisites.SequenceEqual(new[] {3958}) &&
            quest.Condition == "[hunt enemy]" && quest.IntData.SequenceEqual(new[] {310,-1,72325,3,3}),
            "3959保留真实三笼目标，不简化任务或改为清图条件");
        var entrance = MiddleQuestEntryTests.Entrance(towns,quest,310);
        using var f = new Fixture(towns,characters,skills,growth,quests,false,88,entrance.Completed,position:entrance.Position);
        That(!f.Read().CompletedQuests.Contains(3959) &&
            f.Session.ChangeQuest(3959,QuestLifecycleAction.Accept,quests,items.Items,items.GoldLimits,items.EpicPieces)?.Commit is not null,
            "3959只准备入口前置，真实31建立接取");
        var accepted = f.Read().ActiveMainQuests.Single(row => row.QuestId == 3959);
        That(accepted.ConditionState == 3 && accepted.AcceptanceId != Guid.Empty,"三笼从本次接取的真实剩余数3开始");
        ushort selector = QuestFixedRewardDefinition.Parse(quest with { Rewards=quest.Definition!.SelectRewards })
            .ForJob(f.Read().Job,f.Read().GrowType).Length == 0 ? ushort.MaxValue : (ushort)0;
        var opening = f.Session.EnterFormalDungeon(310,3959,0,dungeons,rooms)!;
        That(opening is not null && opening.Entry.Maze.SourceOrdinal == 9 && opening.Entry.BossMap.Id == 76768 &&
            opening.Entry.QuestAcceptances.Any(row => row.AcceptanceId == accepted.AcceptanceId),"三笼路线冻结原接取身份");
        var receipt = f.Session.CompleteFormalDungeonLoading()!;
        var before = f.Read();
        That(Observe(new(0x10,0,0)) is null && Same(before,f.Read()),"起点没有72325，不能按目标模板提前减量");
        var killed = new HashSet<ushort>();
        foreach (var target in new[] { (Map:76763,Point:new DungeonPoint(2,0),Ordinal:24),
            (Map:76764,Point:new DungeonPoint(2,2),Ordinal:24),(Map:76766,Point:new DungeonPoint(3,2),Ordinal:23) })
        {
            var path = QuestStoryRouteTests.Path(opening!.Entry.Maze.StaticTopology,receipt.Snapshot.CurrentMap.Position,target.Point);
            foreach (var point in path.Skip(1))
            {
                foreach (var monster in receipt.Monsters.Monsters.Where(row => row.IsBlocking && !killed.Contains(row.ObjectKey)))
                {
                    That(!monster.IsScriptedBoss && f.Session.ObserveFormalDungeonDeath(
                        FormalDungeonSessionTests.Observation(receipt,monster),kills,growth).Outcome == DungeonRunOutcome.Accepted,
                        "三笼之间只提交真实前房敌人，不造终点dummy死亡");
                    killed.Add(monster.ObjectKey);
                }
                That(f.Session.MoveFormalDungeon(checked((byte)point.X),checked((byte)point.Y)) is not null,"沿真实门墙抵达下一笼子");
                receipt = f.Session.CompleteFormalDungeonLoading()!;
            }
            That(receipt.Snapshot.CurrentMap.Id == target.Map && receipt.Monsters.Room.PassiveObjects.Count(row => row.ObjectId == 72325) == 1 &&
                receipt.Monsters.Room.PassiveObjects[target.Ordinal].ObjectId == 72325 &&
                !rooms.ReportsQuestDeath(receipt.Monsters.Room.PassiveObjects[target.Ordinal]),"每笼保留真实地图、原ordinal及普通自动销毁声明");
            before = f.Read();
            var identity = f.Session.DungeonSnapshot!.Identity;
            That(Observe(new(0x20,0,0)) is null && Observe(new(0x10,1,0)) is null && Observe(new(0x10,0,1)) is null &&
                f.Session.ObserveFormalPassiveDeath(72325,true,new string('C',64),rooms) is null && Same(before,f.Read()),
                "错通道、非终末字段及无must-be-destroyed的39不能冒充三笼33");
            var progress = Observe(new(0x10,0,0));
            var after = f.Read();
            That(progress?.Commit?.Plan.SceneEvent is { TemplateId:72325 } source && source.MapId == target.Map &&
                source.SourceOrdinal == target.Ordinal && source.Identity == identity &&
                source.Identity.InstanceId == opening.Snapshot.Identity.InstanceId && progress.DungeonIdentity == identity &&
                progress.Commit.Plan.AcceptanceId == accepted.AcceptanceId &&
                after.ActiveMainQuests.Single(row => row.QuestId == 3959).ConditionState ==
                    before.ActiveMainQuests.Single(row => row.QuestId == 3959).ConditionState-1 &&
                Same(before with { Revision=after.Revision,ActiveMainQuests=after.ActiveMainQuests },after),
                "本图唯一笼子只减原接取一次，不产生击杀成长、金币或物品");
            That(Observe(new(0x10,0,0)) is null && Same(after,f.Read()),"同图重复33不能再次解救帝国军");
            if (after.ActiveMainQuests.Single(row => row.QuestId == 3959).ConditionState != 0)
                That(Finish().Outcome == QuestFinishOutcome.Rejected && Same(after,f.Read()),"三笼未齐不能提前34");
        }
        var finished = Finish();
        That(finished.Receipt?.Commit is { ConditionType:6,Clear:null } complete && complete.AcceptanceId == accepted.AcceptanceId &&
            f.Read().CompletedQuests.Contains(3959) && f.Session.Phase == SessionPhase.Dungeon &&
            f.Session.EnableFormalDungeonSettlement() is null && f.Repository.LoadQuestClear(f.Owner,f.Read(),3959) is null &&
            f.Query("SELECT count(*) FROM character_quest_lifecycle_receipts WHERE quest_id=3959 AND scene_event IS NOT NULL") is 3L,
            "三笼在76766正常34，仅三条独立场景来源，不要求清图或杀75100");
        before = f.Read();
        That(Finish().Outcome == QuestFinishOutcome.AlreadyCompleted && Same(before,f.Read()),"重复三笼34不二次发奖");
        That(f.Session.ReturnFromFormalDungeon() is not null,"未打dummy也可完成目标后回城");
        f.Session.ReturnToRoster(); f.Session.Select(0); f.Session.MarkReady();
        That(f.Read().CompletedQuests.Contains(3959) && f.Read().ActiveMainQuests.All(row => row.QuestId != 3959) &&
            f.Query("SELECT count(*) FROM character_quest_completion_receipts WHERE quest_id=3959") is 1L,"重选保留三笼交付且只有一条领奖回执");

        QuestLifecycleReceipt? Observe(QuestClientCondition condition) =>
            f.Session.ObserveFormalPassiveTrigger(3959,condition,new string('C',64),rooms);
        QuestFinishResult Finish() => f.Session.FinishQuest(3959,selector,quests,parameters,items.Items,items.GoldLimits,items.EpicPieces,growth);
    }

    private static void VerifyPassiveSettlement(TownCatalog towns, CharacterCatalog characters, ItemResources items,
        QuestCatalog quests, SkillLearningCatalog skills, CharacterGrowthCatalog growth, DungeonCatalog dungeons,
        DungeonRoomCatalog rooms, DungeonKillExperienceCatalog kills, DungeonClearExperienceCatalog clear,
        DungeonDropRuleCatalog drops)
    {
        var area = towns.Areas.Values.First(row => row.Level <= 28 && row.DungeonWorldMap is { } world &&
            towns.DungeonMaps[world].Options.Any(option => option.DungeonId == 25));
        var done = new HashSet<int>(area.Quests) { 3204 };
        if (area.AnyQuests.Length > 0) done.Add(area.AnyQuests[0]);
        var bounds = area.Map(done).Bounds.First(row => row.Conditions.Length == 0 && row.Quests.All(done.Contains)).Bounds;
        var position = new Location(area.Town, area.Area, checked((ushort)(bounds.X + bounds.Width / 2)), checked((ushort)(bounds.Y + bounds.Height / 2)));
        foreach (string mode in new[] { "rollback", "lost", "stale", "independent", "lostSettlement" })
        {
            using var f = new Fixture(towns, characters, skills, growth, quests, mode is "lost" or "stale",
                28, done.ToArray(), position: position,loseSettlement: mode == "lostSettlement");
            That(f.Session.ChangeQuest(3205, QuestLifecycleAction.Accept, quests, items.Items, items.GoldLimits, items.EpicPieces)?.Commit is not null,
                "隔离真实入口正常接取3205");
            if (mode == "independent")
            {
                // 活动任务已有进度时，新的实例仍须提交本次迷宫物件事实，不能借scalar0提前结算。
                f.Sql("UPDATE character_active_main_quests SET condition_state=0 WHERE quest_id=3205");
                f.Session.ReturnToRoster(); f.Session.Select(0, resumeTown: true); f.Session.MarkReady();
            }
            var opening = f.Session.EnterFormalDungeon(25, 3205, 0, dungeons, rooms);
            That(opening is not null, "3205场景事务fixture已恢复实际入口区域并正常准入：" + mode);
            That(opening!.Entry is { RouteQuestId: 3205, IsOrdinary: false, BossMap.Id: 53493 },
                "3205即使原活动条件为0也保留本实例被动物件路线：" + mode);
            var path = QuestStoryRouteTests.Path(opening!.Entry.Maze.StaticTopology, opening.Entry.StartMap.Position, opening.Entry.BossMap.Position);
            FormalDungeonReceipt receipt = opening!;
            foreach (var point in path)
            {
                receipt = f.Session.CompleteFormalDungeonLoading()!;
                foreach (var monster in receipt.Monsters.Monsters.Where(monster => monster.IsBlocking))
                    That(f.Session.ObserveFormalDungeonDeath(FormalDungeonSessionTests.Observation(receipt, monster), kills, growth).Outcome == DungeonRunOutcome.Accepted,
                        "实际3205路线由合成权威死亡通过原提交链清房");
                if (point == opening.Entry.BossMap.Position) break;
                var next = path[Array.IndexOf(path, point) + 1];
                That(f.Session.MoveFormalDungeon(checked((byte)next.X), checked((byte)next.Y)) is not null, "按实际门墙路线移动至3205终点");
            }
            var before = f.Read(); var online = f.Session.Selected!; var identity = f.Session.DungeonSnapshot!.Identity;
            That(receipt.Snapshot.CurrentMap.Id == 53493 && f.Session.Phase == SessionPhase.Dungeon &&
                f.Session.EnableFormalDungeonSettlement() is null, "物件提交前终点仍在Dungeon且无结算许可");
            if (mode == "rollback")
            {
                f.Sql("CREATE TRIGGER terminal_scene_abort AFTER INSERT ON character_quest_lifecycle_receipts WHEN NEW.scene_event IS NOT NULL BEGIN SELECT RAISE(ABORT,'terminal scene rollback'); END");
                TestCheck.Reject<SqliteException>(() => Observe(), "终点物件SQL末段回滚");
                f.Sql("DROP TRIGGER terminal_scene_abort");
                That(Same(before, f.Read()), "回滚保留完整角色资产和任务状态");
            }
            if (mode is "lost" or "stale") TestCheck.Reject<IOException>(() => Observe(), "终点物件提交返回丢失");
            if (mode != "independent")
                That(ReferenceEquals(f.Session.Selected, online) && f.Session.DungeonSnapshot!.Identity == identity &&
                    f.Session.Phase == SessionPhase.Dungeon && f.Session.EnableFormalDungeonSettlement() is null,
                    "未取得明确提交结果时不改变Run或提前许可结算");
            if (mode == "stale") f.Sql("UPDATE characters SET revision=revision+1");
            var progress = Observe()!;
            if (mode == "stale")
            {
                That(progress.Commit is { Duplicate: true, ProjectionCurrent: false } && !f.Session.CanPublishQuestLifecycle(progress) &&
                    ReferenceEquals(f.Session.Selected, online) && f.Session.Phase == SessionPhase.Dungeon && f.Session.DungeonSnapshot!.Identity == identity,
                    "已过期的持久回执不推进Run、不覆盖在线角色或发布成功");
                continue;
            }
            var enable = f.Session.EnableFormalDungeonSettlement();
            That(progress.Commit is { ProjectionCurrent: true } && progress.Commit.Duplicate == (mode == "lost") &&
                f.Session.CanPublishQuestLifecycle(progress) && progress.Phase == SessionPhase.Settlement &&
                progress.DungeonIdentity == f.Session.DungeonSnapshot!.Identity && progress.DungeonIdentity!.Epoch == identity.Epoch + 1 &&
                enable is { BossKey: null } && enable.Value.Receipt.Snapshot.CurrentMap.Id == 53493,
                "原操作恢复后回执绑定实际结算phase与epoch，空终点没有伪造Boss key");
            var committed = f.Read();
            That(Observe() is null && Same(committed, f.Read()) &&
                f.Session.ObserveFormalPassiveDeath(80458, true, new string('D', 64), rooms) is null,
                "同物件33重放或39镜像不再次推进revision或结算");
            f.Session.ConfirmFormalDungeonSettlementEnabled(enable!.Value.Receipt);
            That(f.Session.EnableFormalDungeonSettlement() is null, "成功发布后本实例不重复许可结算");
            if (mode == "lostSettlement")
            {
                var expected = clear.Calculate(dungeons.Get(25),0,99);
                var previous = f.Session.Selected!;
                int draws = 0;
                var cards = new DungeonCardRewardPolicy(drops,items.DungeonDropItems,items.Items,items.GoldLimits,items.EpicPieces,
                    _ => { draws++; return 0; });
                TestCheck.Reject<IOException>(() => Settle(),"第一脊椎真实物件ClearFact结算COMMIT后返回丢失");
                var saved = f.Read(); int frozenDraws = draws;
                That(ReferenceEquals(previous,f.Session.Selected) && saved.Learning.Experience == previous.Learning.Experience+expected.TotalExperience &&
                    f.Query("SELECT count(*) FROM character_dungeon_settlement_receipts") is 1L &&
                    f.Query("SELECT claimed FROM character_dungeon_card_rewards") is 0L &&
                    saved.Inventory.Gold == previous.Inventory.Gold && f.Session.ReturnFromFormalDungeon() is null,
                    "未知结果保留原在线快照且拒绝离场，持久经验与未领牌仅一次提交");
                var restored = Settle();
                That(restored.Outcome == DungeonRunOutcome.Accepted && restored.Card is not null && restored.Inventory is null &&
                    draws == frozenDraws && Same(saved,f.Session.Selected!) && Same(saved,f.Read()),
                    "第一脊椎原46恢复安装持久结果，不重复抽牌、经验或资产");
                That(Settle().Outcome == DungeonRunOutcome.Duplicate && Same(saved,f.Read()) &&
                    f.Query("SELECT count(*) FROM character_dungeon_card_rewards") is 1L,
                    "原物件结算恢复后的重复请求保持唯一牌计划");
                That(f.Session.ReturnFromFormalDungeon() is not null,"已恢复物件结算允许正常回城，不遗留未决事务互锁");
                f.Session.ReturnToRoster(); f.Session.Select(0,resumeTown:true); f.Session.MarkReady();
                That(f.Session.Selected!.Learning.Experience == saved.Learning.Experience &&
                    System.Text.Json.JsonSerializer.Serialize(f.Session.Selected.Inventory) == System.Text.Json.JsonSerializer.Serialize(saved.Inventory) &&
                    f.Read().ActiveMainQuests.Single(row => row.QuestId == 3205).ConditionState == 0 &&
                    f.Query("SELECT count(*) FROM character_dungeon_settlement_receipts") is 1L,
                    "重选回放已保存经验和已达成物件条件，不恢复旧Run或重发通关奖励");
                FormalDungeonSettlementResult Settle() => f.Session.SettleFormalDungeon(previous.Id,99,99,[11,3,25],dungeons,clear,growth,cards);
            }
            QuestLifecycleReceipt? Observe() => f.Session.ObserveFormalPassiveTrigger(3205, new(0x10, 0, 0), new string('C', 64), rooms);
        }
    }

    // 使用真实父3525/子3560与92247资源；死亡合成，不能替代客户端演出验收。
    private static void VerifyRoomProgress(TownCatalog towns,CharacterCatalog characters,ItemResources items,
        QuestCatalog quests,SkillLearningCatalog skills,CharacterGrowthCatalog growth,DungeonCatalog dungeons,
        DungeonRoomCatalog rooms,DungeonKillExperienceCatalog kills,QuestRewardParameterCatalog parameters)
    {
        // 独立合成支线仅用于核对同一真实目标房的多接取原子性，不冒充PVF中另一个任务。
        var siblingSource = quests.Get(3560);
        var sibling = siblingSource with { Id = 29900,MainQuest = null,Grade = "[side]",
            Definition = siblingSource.Definition! with { ExposedByNpc = true } };
        quests = new(quests.All.Append(sibling).ToDictionary(row => row.Id));
        var area = towns.Areas.Values.First(row => row.Level <= 65 && row.DungeonWorldMap is { } world &&
            towns.DungeonMaps[world].Options.Any(option => option.DungeonId == 82));
        var done = new HashSet<int>(area.Quests) { 3145,4873,3524,3558,3559 };
        if (area.AnyQuests.Length > 0) done.Add(area.AnyQuests[0]);
        var bounds = area.Map(done).Bounds.First(row => row.Conditions.Length == 0 && row.Quests.All(done.Contains)).Bounds;
        var position = new Location(area.Town,area.Area,checked((ushort)(bounds.X+bounds.Width/2)),checked((ushort)(bounds.Y+bounds.Height/2)));
        foreach (bool lost in new[] { false,true })
        {
            var clock = lost ? new ConsumableUseSessionTests.FakeClock() : null;
            long? contractExpiry = clock?.GetUtcNow().AddSeconds(1).ToUnixTimeSeconds();
            using var f = new Fixture(towns,characters,skills,growth,quests,false,65,done.ToArray(),loseGrowth: lost,
                position: position,growthLossCondition: prepared => prepared.Result.RoomClearFact is not null,
                contracts: lost ? items.Contracts : null,timeProvider: clock,growthContractExpiresAt: contractExpiry);
            int expiredKills = 0;
            That(Change(3525)?.Commit is not null && f.Read().ActiveMainQuests.Single().ConditionState == 1,
                "真实父任务按已完成两个会面子任务进入最后清图阶段");
            var opening = f.Session.EnterFormalDungeon(82,3525,0,dungeons,rooms)!;
            That(opening is not null && opening.Entry.QuestClearMap is { Id: 92247,Position.X: 2,Position.Y: 1 } &&
                opening.Entry.BossMap.Id == 92251 && opening.Entry.BaseBossMap == opening.Entry.BossMap &&
                !f.Read().ActiveMainQuests.Any(row => row.QuestId == 3560),"父请求借真实子布局，不造接取或替换原Boss");
            var receipt = f.Session.CompleteFormalDungeonLoading()!;
            That(Change(3560)?.Commit is not null,"加载后真实31接取清图子任务并绑定本Run");
            That(Change(29900)?.Commit is not null,"同图合成支线以独立接取身份绑定原Run");
            var acceptance = f.Read().ActiveMainQuests.Single(row => row.QuestId == 3560).AcceptanceId;
            var entry = opening!.Entry;
            var target = entry.QuestClearMap!;
            var path = QuestStoryRouteTests.Path(entry.Maze.StaticTopology,entry.StartMap.Position,target.Position);
            foreach (var point in path.Skip(1))
            {
                KillRoom();
                That(f.Session.MoveFormalDungeon(checked((byte)point.X),checked((byte)point.Y)) is not null,"真实门墙连接通往中途任务房");
                receipt = f.Session.CompleteFormalDungeonLoading()!;
            }
            var enemies = receipt.Monsters.Monsters.Where(row => row.IsBlocking).ToArray();
            That(enemies.Length == 6 && enemies.All(row => row.MonsterId == 61801) &&
                receipt.Monsters.Monsters.Any(row => row.MonsterId == 10448 && row.IsFriendly && !row.IsBlocking),
                "任务房保留真实六敌与友方APC");
            foreach (var monster in enemies[..^1]) Kill(monster);
            That(f.Session.ReadClearedQuest(3560) is null && f.Session.EnableFormalDungeonSettlement() is null,
                "少一敌人不能达成清图或提前许可结算");
            var observation = FormalDungeonSessionTests.Observation(receipt,enemies[^1]);
            var scripted = new NativeDungeonScriptedDeathObservation(enemies[^1].ObjectKey,0,true,new string('D',64));
            var before = f.Read(); var online = f.Session.Selected; var originalIdentity = f.Session.DungeonSnapshot!.Identity;
            uint basis = ExpectedExperience(enemies[^1]);
            uint bonus = lost ? checked((uint)((ulong)basis * (uint)items.Contracts.Growth!.ExperiencePercent / 100)) : 0;
            Character? savedBeforeRecovery = null;
            QuestClearReceipt? savedClear = null;
            if (lost)
            {
                TestCheck.Reject<IOException>(() => SubmitLast(),"合成脚本39的目标房事务提交后返回丢失");
                That(f.Read().ActiveMainQuests.Single(row => row.QuestId == 3560).ConditionState == 0 &&
                    ReferenceEquals(f.Session.Selected,online) && f.Session.DungeonSnapshot.Identity == originalIdentity &&
                    f.Session.ReadClearedQuest(3560) is null,"数据库已达成但未确认Run，不提前读取或安装Selected");
                savedBeforeRecovery = f.Read();
                savedClear = f.Repository.LoadQuestClear(f.Owner,savedBeforeRecovery,3560)!;
                That(bonus > 0 && savedBeforeRecovery.Learning.Experience == before.Learning.Experience + basis + bonus &&
                    savedBeforeRecovery.Revision == before.Revision + 1 &&
                    f.GrowthReceipt(savedClear.EventId) == (1L,(long)basis + bonus),
                    "返回丢失前已按有效账户契约提交原死亡经验与唯一清图事件");
                // 可控时钟推进到精确到期；同一死亡恢复必须保留到期前的原奖励。
                clock!.Advance(TimeSpan.FromSeconds(1));
                That(clock.GetUtcNow().ToUnixTimeSeconds() == contractExpiry &&
                    before.Inventory.AccountWallet.ContractExpiry(84) == contractExpiry,
                    "原角色账户真实期限不变，仅推进测试时钟至契约到期");
            }
            else
            {
                f.Sql("CREATE TRIGGER room_clear_abort AFTER INSERT ON character_quest_clear_receipts BEGIN SELECT RAISE(ABORT,'room clear rollback'); END");
                TestCheck.Reject<SqliteException>(() => SubmitLast(),"中途清图末段SQL失败");
                That(Same(before,f.Read()) && ReferenceEquals(f.Session.Selected,online) &&
                    f.Session.DungeonSnapshot.Identity == originalIdentity,"回滚保留全部资产、任务与原epoch候选");
                f.Sql("DROP TRIGGER room_clear_abort");
            }
            var result = SubmitLast();
            var committed = f.Read(); var clear = f.Session.ReadClearedQuest(3560)!;
            That(result.Outcome == DungeonRunOutcome.Accepted && result.ClearFact is null && f.Session.Phase == SessionPhase.Dungeon &&
                f.Session.DungeonSnapshot!.Identity.Epoch == originalIdentity.Epoch+1 && committed.Revision == before.Revision+1 &&
                clear.Clear is { MapId: 92247,SourceType: QuestClearSourceType.MonsterDeath } && clear.Clear.AcceptanceId == acceptance &&
                f.Session.CanPublishQuestCondition(clear) && f.Session.EnableFormalDungeonSettlement() is null &&
                result.Receipt!.Snapshot.LivingMonsters.Contains(receipt.Monsters.Monsters.Single(row => row.MonsterId == 10448).ObjectKey),
                "原成长事务精确恢复并确认RoomClear；保留友方且不生成终点奖励或结算许可");
            if (lost)
                That(result.Reward is { } reward && reward.Amount == basis + bonus && reward.GrowthContractExperience == bonus &&
                    result.GainedExperience == basis + bonus && Same(savedBeforeRecovery!,committed) &&
                    clear.Clear.EventId == savedClear!.EventId && f.GrowthReceipt(clear.Clear.EventId) == (1L,(long)basis + bonus),
                    "到期后精确恢复沿用原死亡额度和契约份额，不重写revision、清图身份或再发经验");
            That(f.Session.ReadClearedQuest(29900) is { } siblingClear && siblingClear.Clear.EventId == clear.Clear.EventId &&
                siblingClear.Clear.AcceptanceId != clear.Clear.AcceptanceId &&
                committed.ActiveMainQuests.Single(row => row.QuestId == 29900).ConditionState == 0,
                "同次实际清房原子推进全部合法匹配接取，凭据保持各自AcceptanceId");
            That(SubmitLast().Outcome == DungeonRunOutcome.Duplicate && Same(committed,f.Read()),
                "确认后的旧epoch死亡仅重复忽略，不二次授予经验或进度");
            if (lost) That(f.GrowthReceipt(clear.Clear.EventId) == (1L,(long)basis + bonus),
                "确认后的重复死亡不增加原事件收据或改写冻结额度");
            var toBoss = QuestStoryRouteTests.Path(entry.Maze.StaticTopology,target.Position,entry.BossMap.Position);
            foreach (var point in toBoss.Skip(1))
            {
                KillRoom();
                That(f.Session.MoveFormalDungeon(checked((byte)point.X),checked((byte)point.Y)) is not null,"清图后继续原Boss路线");
                That(!f.Session.CanPublishQuestCondition(clear),"过房使旧发布快照失效");
                receipt = f.Session.CompleteFormalDungeonLoading()!;
                That(f.Session.ReadClearedQuest(3560)?.Clear == clear.Clear,"后续房间仍读取本Run具体已提交清图事实");
            }
            KillRoom();
            if (lost) That(expiredKills > 0,"恢复后确有新死亡经过到期契约分支，不仅检查旧事件重放");
            That(f.Session.Phase == SessionPhase.Settlement && f.Session.ReadClearedQuest(3560)?.Clear == clear.Clear &&
                f.Session.EnableFormalDungeonSettlement() is not null,"真正Boss才产生结算，中途任务凭据不丢失");
            That(f.Session.ReturnFromFormalDungeon() is not null,"已达成子任务可以保留凭据回城");
            f.Session.ReturnToRoster(); f.Session.Select(0); f.Session.MarkReady();
            That(f.Session.ReadClearedQuest(3560)?.Clear == clear.Clear,"重登从原growth外键恢复中途清图资格");
            var finished = f.Session.FinishQuest(3560,ushort.MaxValue,quests,parameters,items.Items,items.GoldLimits,items.EpicPieces,growth);
            That(finished.Outcome == QuestFinishOutcome.Committed && f.Read().CompletedQuests.Contains(3560) &&
                f.Read().ActiveMainQuests.Single(row => row.QuestId == 3525).ConditionState == 0,"子任务真实交付后按原顺序推进父任务");

            QuestLifecycleReceipt? Change(ushort id) => f.Session.ChangeQuest(id,QuestLifecycleAction.Accept,quests,items.Items,items.GoldLimits,items.EpicPieces);
            FormalDungeonDeathResult SubmitLast() => lost ? f.Session.ObserveFormalDungeonScriptedDeath(scripted,kills,growth) :
                f.Session.ObserveFormalDungeonDeath(observation,kills,growth);
            uint ExpectedExperience(DungeonPlannedMonster monster) => DungeonKillExperiencePolicy.Calculate(
                kills.GetExperience(monster.CandidateLevel),entry.ExperienceWeight,kills.GetDifficultyRate(entry.Difficulty),
                kills.GetMonsterRate(monster.RewardType,monster.AiCharacter is null && entry.NamedMonsters.Contains(monster.MonsterId)),
                f.Session.Selected!.Level,monster.CandidateLevel);
            void Kill(DungeonPlannedMonster monster)
            {
                uint expected = ExpectedExperience(monster);
                bool expired = clock is not null && clock.GetUtcNow().ToUnixTimeSeconds() >= contractExpiry!.Value;
                var death = f.Session.ObserveFormalDungeonDeath(FormalDungeonSessionTests.Observation(receipt,monster),kills,growth);
                That(death.Outcome == DungeonRunOutcome.Accepted,"合成已登记敌对死亡沿原成长事务提交");
                if (expired)
                {
                    That(death.Reward is { GrowthContractExperience: 0 } reward && reward.Amount == expected &&
                        death.GainedExperience == expected,"原奖励恢复后新的合法死亡按已到期账户资格发基础经验，不沿用旧契约加成");
                    expiredKills++;
                }
            }
            void KillRoom()
            {
                foreach (var monster in receipt.Monsters.Monsters.Where(row => row.IsBlocking).OrderBy(row => row.IsBoss).ToArray())
                    if (f.Session.DungeonSnapshot!.LivingMonsters.Contains(monster.ObjectKey)) Kill(monster);
            }
        }
    }

    // 3213 寻找阿甘左：香料是副本投掷，阿甘左是非阻挡敌对 APC。死亡后才有结算许可。
    private static void VerifyAganzoSpice(TownCatalog towns, CharacterCatalog characters, ItemResources items,
        QuestCatalog quests, SkillLearningCatalog skills, CharacterGrowthCatalog growth, DungeonCatalog dungeons,
        DungeonRoomCatalog rooms, DungeonKillExperienceCatalog kills)
    {
        var area = towns.Areas.Values.First(row => row.Level <= 30 && row.DungeonWorldMap is { } world &&
            towns.DungeonMaps[world].Options.Any(option => option.DungeonId == 26));
        var done = new HashSet<int>(area.Quests) { 3212 };
        if (area.AnyQuests.Length > 0) done.Add(area.AnyQuests[0]);
        var bounds = area.Map(done).Bounds.First(row => row.Conditions.Length == 0 && row.Quests.All(done.Contains)).Bounds;
        var position = new Location(area.Town, area.Area, checked((ushort)(bounds.X + bounds.Width / 2)), checked((ushort)(bounds.Y + bounds.Height / 2)));
        using var f = new Fixture(towns, characters, skills, growth, quests, false, 30, done.ToArray(), position: position);
        var accept = f.Session.ChangeQuest(3213, QuestLifecycleAction.Accept, quests, items.Items, items.GoldLimits, items.EpicPieces);
        That(accept?.Commit is not null, "完成3212后可以接取寻找阿甘左");
        var granted = f.Session.PlanAndCommitInventory(character => InventoryRewardGrantService.TryGrantBatch(character,
            [new InventoryReward(10162711, 1)], items.Items, items.GoldLimits, items.EpicPieces, DateTimeOffset.UnixEpoch));
        That(granted.Accepted, "艾丽丝的香料能进主背包");
        var opening = f.Session.EnterFormalDungeon(26, 3213, 0, dungeons, rooms);
        That(opening is not null && opening.Entry.BossMap.Id == 53529, "3213进入第二脊椎，终点是阿甘左所在的53529：" + f.Session.LastFormalDungeonRejection);
        var path = QuestStoryRouteTests.Path(opening!.Entry.Maze.StaticTopology, opening.Entry.StartMap.Position, opening.Entry.BossMap.Position);
        FormalDungeonReceipt receipt = opening;
        foreach (var point in path)
        {
            receipt = f.Session.CompleteFormalDungeonLoading()!;
            if (point == opening.Entry.BossMap.Position) break;
            foreach (var monster in receipt.Monsters.Monsters.Where(row => row.IsBlocking).ToArray())
                That(f.Session.ObserveFormalDungeonDeath(FormalDungeonSessionTests.Observation(receipt, monster), kills, growth).Outcome == DungeonRunOutcome.Accepted,
                    "3213沿路清掉阻挡怪");
            var next = path[Array.IndexOf(path, point) + 1];
            That(f.Session.MoveFormalDungeon(checked((byte)next.X), checked((byte)next.Y)) is not null, "3213按门墙走到阿甘左");
        }
        var spice = f.Session.Selected!.Inventory.Items.Single(row => row.Core.ItemId == 10162711);
        var used = f.Session.UseConsumable(new(spice.Position, 0, 10162711, 0), new InventoryConsumableUseService(items.RecoveryConsumables));
        That(used.Outcome == ConsumableUseOutcome.Committed && f.Session.Selected.Inventory.Find(spice.Position) is null,
            "副本里使用香料成功并扣掉这一件：" + used.Reason);
        var aganzo = receipt.Monsters.Monsters.Single(row => row.MonsterId == 2004);
        That(aganzo is { SpawnType: 8, IsBoss: true, CanReportDeath: true, IsBlocking: false }, "53529的阿甘左是可上报死亡的敌对APC");
        var death = f.Session.ObserveFormalDungeonDeath(FormalDungeonSessionTests.Observation(receipt, aganzo), kills, growth);
        That(death.Outcome == DungeonRunOutcome.Accepted && death.ClearFact is not null && f.Session.Phase == SessionPhase.Settlement &&
            f.Session.EnableFormalDungeonSettlement() is { } permit && permit.Receipt.Snapshot.CurrentMap.Id == 53529,
            "香料打出的阿甘左死亡给出53529结算许可");
        That(f.Repository.LoadQuestClear(f.Owner, f.Session.Selected, 3213)?.MapId == 53529, "3213清图凭据记在53529，可以回城交付");
    }

    private static void VerifyTerminalLayerReentry(TownCatalog towns, CharacterCatalog characters, ItemResources items,
        QuestCatalog quests, SkillLearningCatalog skills, CharacterGrowthCatalog growth, DungeonCatalog dungeons,
        DungeonRoomCatalog rooms, DungeonKillExperienceCatalog kills)
    {
        var area = towns.Areas.Values.First(row => row.Level <= 92 && row.DungeonWorldMap is { } world &&
            towns.DungeonMaps[world].Options.Any(option => option.DungeonId == 291100319));
        var done = new HashSet<int>(area.Quests) { 6031 };
        if (area.AnyQuests.Length > 0) done.Add(area.AnyQuests[0]);
        foreach (int id in area.Map(done).DungeonGates.First(gate => gate.Conditions.Length == 0 && !gate.Quests.Contains(6032)).Quests)
            done.Add(id);
        var bounds = area.Map(done).Bounds.First(row => row.Conditions.Length == 0 && row.Quests.All(done.Contains)).Bounds;
        var position = new Location(area.Town,area.Area,checked((ushort)(bounds.X+bounds.Width/2)),checked((ushort)(bounds.Y+bounds.Height/2)));
        using var f = new Fixture(towns,characters,skills,growth,quests,false,92,done.ToArray(),position:position);
        That(f.Session.ChangeQuest(6032,QuestLifecycleAction.Accept,quests,items.Items,items.GoldLimits,items.EpicPieces)?.Commit is not null,
            "真实搏击俱乐部入口按前置接取末层清图任务");
        var original = f.Read().ActiveMainQuests.Single(row => row.QuestId == 6032);
        // 裸scalar0没有任何死亡/场景持久凭据，不能通过普通0入口冒充已批准重入。
        f.Sql("UPDATE character_active_main_quests SET condition_state=0 WHERE quest_id=6032");
        Reselect();
        That(f.Session.EnterFormalDungeon(291100319,0,0,dungeons,rooms) is null,
            "真实Session普通0拒绝无持久清图凭据的裸state0");
        f.Sql("UPDATE character_active_main_quests SET condition_state=1 WHERE quest_id=6032");
        Reselect();
        var opening = f.Session.EnterFormalDungeon(291100319,0,0,dungeons,rooms);
        That(opening is { Entry.TerminalLayerMap: null, Entry.BossMap.Position.X: 3, Entry.BossMap.Id: 59914 } &&
            opening.Entry.QuestAcceptances.Any(row => row.QuestId == original.QuestId && row.Slot == original.Slot &&
                row.AcceptanceId == original.AcceptanceId),
            "普通0保留原接取三元组，6032演出返回声明恢复资源Boss清图目标");
        var receipt = opening ?? throw new InvalidOperationException("末层任务实例未建立");
        var target = receipt.Entry.BossMap;
        var scene = receipt.Entry.Maze.LayeredMaps.Single(map => map.Id == target.Id);
        ClearTerminal(receipt);
        var first = f.Session.ReadClearedQuest(6032)?.Clear;
        That(first is not null && first.AcceptanceId == original.AcceptanceId && f.Session.Phase == SessionPhase.Settlement,
            "合成可信死亡经真实Session和SQLite形成原接取持久凭据（非客户端验收）");
        That(f.Session.ReturnFromFormalDungeon() is not null,"未交付任务可沿原退出回城");
        Reselect();
        var repeated = f.Session.EnterFormalDungeon(291100319,0,0,dungeons,rooms);
        That(repeated is not null && repeated.Entry.BossMap == target && repeated.Entry.TerminalLayerMap is null &&
            repeated.Entry.QuestAcceptances.Any(row => row.AcceptanceId == original.AcceptanceId) &&
            f.Repository.LoadQuestClear(f.Owner,f.Session.Selected!,6032) == first &&
            f.Read().ActiveMainQuests.Single(row => row.QuestId == 6032).ConditionState == 0,
            "同接取已达成未34后普通0凭原持久账本重入，保留末层及首清图凭据");
        ClearTerminal(repeated!);
        That(f.Session.Phase == SessionPhase.Settlement && f.Repository.LoadQuestClear(f.Owner,f.Session.Selected!,6032) == first &&
            !f.Read().CompletedQuests.Contains(6032), "再次真实末层死亡不覆盖首清图凭据或代替34领奖");

        void ClearTerminal(FormalDungeonReceipt entered)
        {
            var path = QuestStoryRouteTests.Path(entered.Entry.Maze.StaticTopology,entered.Entry.StartMap.Position,scene.Position);
            for (int index = 0; index < path.Length; index++)
            {
                var loaded = f.Session.CompleteFormalDungeonLoading()!;
                Clear(loaded);
                if (index == path.Length-1) break;
                That(f.Session.MoveFormalDungeon(checked((byte)path[index+1].X),checked((byte)path[index+1].Y)) is not null,
                    "真实路线按原连接到达末层入口");
            }
            That(f.Session.Phase == SessionPhase.Dungeon &&
                f.Session.MoveFormalDungeon(checked((byte)scene.Position.X),checked((byte)scene.Position.Y),1) is not null,
                "中途清房不结算，按原kind1进入真实末层");
            That(f.Session.CompleteFormalDungeonLoading() is not null &&
                f.Session.MoveFormalDungeon(checked((byte)scene.Position.X),checked((byte)scene.Position.Y),0) is not null &&
                f.Session.CompleteFormalDungeonLoading() is not null &&
                f.Session.MoveFormalDungeon(checked((byte)target.Position.X),checked((byte)target.Position.Y)) is not null,
                "6032演出层无需合成死亡即可返回并继续资源Boss房");
            Clear(f.Session.CompleteFormalDungeonLoading()!);
        }

        void Clear(FormalDungeonReceipt loaded)
        {
            foreach (var monster in loaded.Monsters.Monsters.Where(row => row.CanReportDeath).OrderBy(row => row.IsBoss).ToArray())
                That(f.Session.ObserveFormalDungeonDeath(FormalDungeonSessionTests.Observation(loaded,monster),kills,growth).Outcome == DungeonRunOutcome.Accepted,
                    "末层重入回归复用合成死亡与原成长提交入口");
        }
        void Reselect() { f.Session.ReturnToRoster(); f.Session.Select(0,resumeTown:true); f.Session.MarkReady(); }
    }

    // #496：夺回西部线的摩托车/GT-9600 是同图多份被动物件。客户端 33 不带实例，按本图放置行逐次计入。
    static void VerifyRepeatedPassiveHunt(TownCatalog towns, CharacterCatalog characters, ItemResources items,
        QuestCatalog quests, SkillLearningCatalog skills, CharacterGrowthCatalog growth, DungeonCatalog dungeons,
        DungeonRoomCatalog rooms, DungeonKillExperienceCatalog kills)
    {
        const int questId = 3577;
        var quest = quests.Get(questId);
        That(QuestHuntProgress.TryTargets(quest, out var targets) &&
            targets.Length == 3 && targets.All(row => row.EnemyType == 3 && row.DungeonId == 87 && row.Count > 1),
            "Q3577 是地下城 87 的三个被动物件猎杀通道");
        var area = towns.Areas.Values.First(row => row.DungeonWorldMap is { } world &&
            towns.DungeonMaps.TryGetValue(world, out var selection) && selection.Error is null &&
            selection.Options.Any(option => option.DungeonId == 87));
        var done = new HashSet<int>(quest.Prerequisites);
        var group = quest.Definition?.PrerequisiteGroups.FirstOrDefault(row => row.Length > 0);
        if (group is not null) done.UnionWith(group);
        done.UnionWith(area.Quests);
        if (area.AnyQuests.Length > 0) done.Add(area.AnyQuests[0]);
        if (towns.DungeonMaps.TryGetValue(area.DungeonWorldMap!.Value, out var worldMap))
            foreach (var option in worldMap.Options.Where(option => option.DungeonId == 87 && option.QuestId > 0 && !option.InProgress))
                done.Add(option.QuestId);
        var townMap = area.Map(done);
        foreach (var gate in townMap.DungeonGates.Where(gate => gate.Conditions.Length == 0))
            done.UnionWith(gate.Quests);
        townMap = area.Map(done);
        var bounds = townMap.Bounds.First(row => row.Conditions.Length == 0 && row.Quests.All(done.Contains)).Bounds;
        var position = new Location(area.Town, area.Area, checked((ushort)(bounds.X + bounds.Width / 2)),
            checked((ushort)(bounds.Y + bounds.Height / 2)));
        byte job = 0;
        if (quest.Definition?.Job is { } required && required != "[all]")
        {
            string[] names = ["[swordman]", "[fighter]", "[gunner]", "[mage]", "[priest]", "[at gunner]", "[thief]", "[at fighter]",
                "[at mage]", "[demonic swordman]", "[creator mage]", "[at swordman]", "[knight]", "[demonic lancer]", "[at priest]", "[gun blader]"];
            job = checked((byte)Array.IndexOf(names, required));
        }
        using var f = new Fixture(towns, characters, skills, growth, quests, false, checked((byte)quest.MinimumLevel),
            done.Order().ToArray(), job: job, position: position);
        var accepted = f.Session.ChangeQuest((ushort)questId, QuestLifecycleAction.Accept, quests, items.Items, items.GoldLimits, items.EpicPieces);
        That(accepted?.Commit is not null, "Q3577 按真实前置接取：" + accepted?.Reason);
        var opening = f.Session.EnterFormalDungeon(87, (uint)questId, 0, dungeons, rooms);
        That(opening is not null && opening.Entry.DungeonId == 87, "Q3577 进入夺回西部线：" + f.Session.LastFormalDungeonRejection);
        var maps = opening!.Entry.Maze.MapSpecification ?? [];
        var placed = maps.SelectMany(map => targets.Select((target, index) => (
            Map: map, Index: index, Target: target,
            Copies: rooms.Get(map).PassiveObjects.Count(row => row.ObjectId == target.MonsterId))))
            .Where(row => row.Copies >= 2).OrderByDescending(row => row.Copies).First();
        That(placed.Copies >= 2, "任务路线上至少有一个房间放置了两份以上的猎杀物件");
        var receipt = f.Session.CompleteFormalDungeonLoading()!;
        if (receipt.Snapshot.CurrentMap.Position != placed.Map.Position)
        {
            var path = QuestStoryRouteTests.Path(opening.Entry.Maze.StaticTopology, receipt.Snapshot.CurrentMap.Position, placed.Map.Position);
            for (int step = 0; step < path.Length - 1; step++)
            {
                foreach (var monster in receipt.Monsters.Monsters.Where(row => row.IsBlocking).ToArray())
                    That(f.Session.ObserveFormalDungeonDeath(FormalDungeonSessionTests.Observation(receipt, monster), kills, growth).Outcome ==
                        DungeonRunOutcome.Accepted, "前往多份物件房间时清除阻挡");
                That(f.Session.MoveFormalDungeon(checked((byte)path[step + 1].X), checked((byte)path[step + 1].Y)) is not null,
                    "按迷宫连接进入下一房间");
                receipt = f.Session.CompleteFormalDungeonLoading()!;
            }
        }
        That(receipt.Snapshot.CurrentMap.Id == placed.Map.Id &&
            receipt.Snapshot.CurrentMap.Position == placed.Map.Position, "当前房间就是多份猎杀物件所在地图");
        var before = f.Read();
        var active = before.ActiveMainQuests.Single(row => row.QuestId == questId);
        uint beforeChannel = (active.ConditionState >> (placed.Index * 9)) & 511;
        That(beforeChannel >= (uint)placed.Copies, "接取后的剩余数量不少于本图放置份数");
        string fingerprint = new string('B', 64);
        byte channel = placed.Index == 0 ? (byte)0x10 : placed.Index == 1 ? (byte)0x20 : (byte)0x40;
        That(f.Session.ObserveFormalPassiveDeath(checked((uint)placed.Target.MonsterId), true, fingerprint, rooms) is null &&
            f.Read().ActiveMainQuests.Single(row => row.QuestId == questId).ConditionState == active.ConditionState,
            "同模板多份时 39 不计数，也不消耗放置行");
        for (int copy = 0; copy < placed.Copies; copy++)
        {
            var progress = f.Session.ObserveFormalPassiveTrigger((ushort)questId, new(channel, 0, 0), fingerprint, rooms);
            That(progress?.Commit is { ProjectionCurrent: true }, "相同 33 第 " + (copy + 1) + " 次计入本图的一份物件");
        }
        var after = f.Read().ActiveMainQuests.Single(row => row.QuestId == questId);
        That(((after.ConditionState >> (placed.Index * 9)) & 511) == beforeChannel - (uint)placed.Copies &&
            f.Read().Learning.Experience == before.Learning.Experience && f.Read().Inventory.Gold == before.Inventory.Gold,
            "本图放置份数全部计入对应通道，不发经验和金币");
        That(f.Session.ObserveFormalPassiveTrigger((ushort)questId, new(channel, 0, 0), fingerprint, rooms) is null &&
            f.Read().ActiveMainQuests.Single(row => row.QuestId == questId).ConditionState == after.ConditionState,
            "超过本图放置份数的相同 33 不再减少");
    }

    // #436：根特外围 3519「冒险家被抓」的牢笼 8824 在 92191/92195 各放两份。客户端猎杀 33 不带实例号，
    // 实机旧构建在这两房都拒绝。双笼房按放置份数各计一次、第三次拒绝；单笼房只计一次；重新入场是新实例，放置行重新可用。
    // 房间资源与正式入场同样不强制每个物件定义可解析，避免无关房间的缺失物件中断取数。
    static void VerifyOuterGentCages(TownCatalog towns, CharacterCatalog characters, ItemResources items,
        QuestCatalog quests, SkillLearningCatalog skills, CharacterGrowthCatalog growth, DungeonCatalog dungeons,
        DungeonRoomCatalog rooms, DungeonKillExperienceCatalog kills)
    {
        const int questId = 3519, cage = 8824;
        var quest = quests.Get(questId);
        That(QuestHuntProgress.TryTargets(quest, out var targets) && targets.Length == 1 &&
            targets[0] is { EnemyType: 3, MonsterId: cage, DungeonId: 80, Count: 15 }, "3519 是地下城 80 牢笼 8824 的 15 次被动物件猎杀");
        var entrance = MiddleQuestEntryTests.Entrance(towns, quest, 80);
        using var f = new Fixture(towns, characters, skills, growth, quests, false, checked((byte)quest.MinimumLevel),
            entrance.Completed, position: entrance.Position);
        That(f.Session.ChangeQuest(questId, QuestLifecycleAction.Accept, quests, items.Items, items.GoldLimits, items.EpicPieces)?.Commit is not null,
            "3519 按真实前置接取");
        That(Remaining() == 15, "接取后剩余 15 个，scalar 是剩余数");
        var receipt = Enter();
        var rows = receipt.Entry.Maze.MapSpecification!.Select(map => (Map: map,
            Copies: rooms.Get(map, requirePassiveDefinitions: false).PassiveObjects.Count(row => row.ObjectId == cage))).ToArray();
        That(rows.Count(row => row.Map.Id is 92191 or 92195 && row.Copies == 2) == 2, "实机被拒的 92191/92195 各放两份牢笼");
        var single = rows.First(row => row.Copies == 1).Map;
        var twin = rows.First(row => row.Map.Id == 92191).Map;
        var killed = new HashSet<ushort>();
        Go(single.Position);
        That(Observe() is { Commit.ProjectionCurrent: true } && Remaining() == 14, "单笼房第一次 33 计一次");
        That(Observe() is null && Remaining() == 14, "单笼房重复 33 不再计数");
        Go(twin.Position);
        That(Observe() is { Commit.ProjectionCurrent: true } && Remaining() == 13, "92191 第一份牢笼计入");
        That(Observe() is { Commit.ProjectionCurrent: true } && Remaining() == 12, "92191 第二份牢笼也计入");
        That(Observe() is null && Remaining() == 12, "92191 两份用完后第三次 33 拒绝");
        That(f.Session.ReturnFromFormalDungeon() is not null, "未完成的 3519 可回城");
        f.Session.ReturnToRoster(); f.Session.Select(0, resumeTown: true); f.Session.MarkReady();
        That(Remaining() == 12, "回城与重选保留已计进度");
        receipt = Enter();
        killed.Clear();
        Go(twin.Position);
        That(Observe() is { Commit.ProjectionCurrent: true } && Observe() is { Commit.ProjectionCurrent: true } &&
            Observe() is null && Remaining() == 10, "重新入场后 92191 两份放置行重新可用，仍以两份为上限");

        FormalDungeonReceipt Enter()
        {
            var opening = f.Session.EnterFormalDungeon(80, questId, 0, dungeons, rooms);
            That(opening is not null && opening.Entry.DungeonId == 80, "3519 进入根特外围：" + f.Session.LastFormalDungeonRejection);
            return f.Session.CompleteFormalDungeonLoading()!;
        }
        void Go(DungeonPoint target)
        {
            var path = QuestStoryRouteTests.Path(receipt.Entry.Maze.StaticTopology, receipt.Snapshot.CurrentMap.Position, target);
            foreach (var point in path.Skip(1))
            {
                foreach (var monster in receipt.Monsters.Monsters.Where(row => row.IsBlocking && killed.Add(row.ObjectKey)).ToArray())
                    That(f.Session.ObserveFormalDungeonDeath(FormalDungeonSessionTests.Observation(receipt, monster), kills, growth).Outcome ==
                        DungeonRunOutcome.Accepted, "前往牢笼房间时清除阻挡");
                That(f.Session.MoveFormalDungeon(checked((byte)point.X), checked((byte)point.Y)) is not null, "按迷宫连接进入下一房间");
                receipt = f.Session.CompleteFormalDungeonLoading()!;
            }
            That(receipt.Snapshot.CurrentMap.Position == target, "已到达目标牢笼房间");
        }
        QuestLifecycleReceipt? Observe() =>
            f.Session.ObserveFormalPassiveTrigger(questId, new(0x10, 0, 0), new string('D', 64), rooms);
        uint Remaining() => f.Read().ActiveMainQuests.Single(row => row.QuestId == questId).ConditionState & 511;
    }

    // #441：3455 的目标是物件 18568。boss 房 70229 放了两份。死亡日志里的 65641 是同图蚂蚁，不是蚁穴。
    // 39 仍不计数；两次猎杀 33 各消耗一份放置行。
    static void VerifyBossRoomAntNests(TownCatalog towns, CharacterCatalog characters, ItemResources items,
        QuestCatalog quests, SkillLearningCatalog skills, CharacterGrowthCatalog growth, DungeonCatalog dungeons,
        DungeonRoomCatalog rooms, DungeonKillExperienceCatalog kills)
    {
        const int questId = 3455;
        const int mapId = 70229;
        const int nest = 18568;
        var quest = quests.Get(questId);
        That(QuestHuntProgress.TryTargets(quest, out var targets) &&
            targets.Select((target, index) => (target, index)).Any(row => row.target.EnemyType == 3 && row.target.MonsterId == nest && row.target.Count == 5),
            "3455 是副本 157 的被动物件 18568，数量 5");
        int channelIndex = targets.Select((target, index) => (target, index)).First(row => row.target.MonsterId == nest).index;
        var area = towns.Areas.Values.First(row => row.DungeonWorldMap is { } world &&
            towns.DungeonMaps.TryGetValue(world, out var selection) && selection.Error is null &&
            selection.Options.Any(option => option.DungeonId == quest.DungeonIndex));
        var done = new HashSet<int>(quest.Prerequisites);
        var group = quest.Definition?.PrerequisiteGroups.FirstOrDefault(row => row.Length > 0);
        if (group is not null) done.UnionWith(group);
        done.UnionWith(area.Quests);
        if (area.AnyQuests.Length > 0) done.Add(area.AnyQuests[0]);
        if (towns.DungeonMaps.TryGetValue(area.DungeonWorldMap!.Value, out var worldMap))
            foreach (var option in worldMap.Options.Where(option => option.DungeonId == quest.DungeonIndex && option.QuestId > 0 && !option.InProgress))
                done.Add(option.QuestId);
        var townMap = area.Map(done);
        foreach (var gate in townMap.DungeonGates.Where(gate => gate.Conditions.Length == 0))
            done.UnionWith(gate.Quests);
        townMap = area.Map(done);
        var bounds = townMap.Bounds.First(row => row.Conditions.Length == 0 && row.Quests.All(done.Contains)).Bounds;
        var position = new Location(area.Town, area.Area, checked((ushort)(bounds.X + bounds.Width / 2)),
            checked((ushort)(bounds.Y + bounds.Height / 2)));
        using var f = new Fixture(towns, characters, skills, growth, quests, false, checked((byte)quest.MinimumLevel),
            done.Order().ToArray(), position: position);
        That(f.Session.ChangeQuest((ushort)questId, QuestLifecycleAction.Accept, quests, items.Items, items.GoldLimits, items.EpicPieces)?.Commit is not null,
            "3455 按真实前置接取");
        var opening = f.Session.EnterFormalDungeon(checked((uint)quest.DungeonIndex!.Value), (uint)questId, 0, dungeons, rooms);
        That(opening is not null, "3455 进入副本：" + f.Session.LastFormalDungeonRejection);
        var boss = opening!.Entry.Maze.MapSpecification!.Single(map => map.Id == mapId);
        That(rooms.Get(boss).PassiveObjects.Count(row => row.ObjectId == nest) == 2, "70229 正好放置两份蚁穴 18568");
        var receipt = f.Session.CompleteFormalDungeonLoading()!;
        if (receipt.Snapshot.CurrentMap.Position != boss.Position)
        {
            var path = QuestStoryRouteTests.Path(opening.Entry.Maze.StaticTopology, receipt.Snapshot.CurrentMap.Position, boss.Position);
            for (int step = 0; step < path.Length - 1; step++)
            {
                foreach (var monster in receipt.Monsters.Monsters.Where(row => row.IsBlocking).ToArray())
                    That(f.Session.ObserveFormalDungeonDeath(FormalDungeonSessionTests.Observation(receipt, monster), kills, growth).Outcome ==
                        DungeonRunOutcome.Accepted, "前往 70229 时清除阻挡");
                That(f.Session.MoveFormalDungeon(checked((byte)path[step + 1].X), checked((byte)path[step + 1].Y)) is not null, "进入下一房间");
                receipt = f.Session.CompleteFormalDungeonLoading()!;
            }
        }
        That(receipt.Snapshot.CurrentMap.Id == mapId, "当前房间是 boss 房 70229");
        var before = f.Read().ActiveMainQuests.Single(row => row.QuestId == questId).ConditionState;
        uint beforeChannel = (before >> (channelIndex * 9)) & 511;
        string fingerprint = new string('N', 64);
        byte channel = channelIndex == 0 ? (byte)0x10 : channelIndex == 1 ? (byte)0x20 : (byte)0x40;
        That(f.Session.ObserveFormalPassiveDeath((uint)nest, true, fingerprint, rooms) is null &&
            f.Read().ActiveMainQuests.Single(row => row.QuestId == questId).ConditionState == before,
            "70229 两份 18568 的 39 不计数");
        That(f.Session.ObserveFormalPassiveTrigger((ushort)questId, new(channel, 0, 0), fingerprint, rooms)?.Commit is { ProjectionCurrent: true } &&
            f.Session.ObserveFormalPassiveTrigger((ushort)questId, new(channel, 0, 0), fingerprint, rooms)?.Commit is { ProjectionCurrent: true },
            "70229 两次猎杀 33 各计入一份蚁穴");
        uint afterChannel = (f.Read().ActiveMainQuests.Single(row => row.QuestId == questId).ConditionState >> (channelIndex * 9)) & 511;
        That(beforeChannel >= 2 && afterChannel == beforeChannel - 2, "两份蚁穴都从 3455 的对应通道扣掉");
        That(f.Session.ObserveFormalPassiveTrigger((ushort)questId, new(channel, 0, 0), fingerprint, rooms) is null, "第三份 33 不再消耗 70229 的放置行");
    }

    internal sealed class Fixture : IDisposable
    {
        readonly string root = Path.Combine(Path.GetTempPath(),"S5A9-quest-scene-"+Guid.NewGuid().ToString("N"));
        readonly string path;
        readonly AccountOwners owners = new();
        public AccountOwners.Lease Owner { get; }
        public CharacterRepository Repository { get; }
        public GameSession Session { get; }
        public Fixture(TownCatalog towns,CharacterCatalog characters,SkillLearningCatalog skills,CharacterGrowthCatalog growth,QuestCatalog quests,bool lost,
            byte level = 3, int[]? completed = null, bool loseGrowth = false, byte job = 0, Location? position = null,
            Func<PreparedDungeonDeath,bool>? growthLossCondition = null,PremiumContractCatalog? contracts = null,
            TimeProvider? timeProvider = null,long? growthContractExpiresAt = null,bool loseSettlement = false,(int Id,int Count)[]? items = null,QuestStageGraph? stages = null,ushort? fatigueRemaining = null,byte growType = 0)
        {
            path = Path.Combine(root,"game.db"); Repository = new(path,quests); Owner = owners.Acquire("scene-quest",() => { });
            var candidate = CharacterCreation.Prepare("SceneQuest",characters.Get(job),position ?? towns.Room,position ?? towns.InitialReturn,skills.Get);
            if (growType != 0)
                candidate = candidate with { GrowType = growType, Skills = skills.FreeSkills(job,growType).ToArray(),
                    InitialLearning = SkillLearning.Initialize(job,skills.FreeSkills(job,growType),skills).State! };
            var learning = SkillPointLedger.Synchronize(job,growType,level,candidate.InitialLearning with { Experience = growth.GetLevelStartExperience(level) },skills);
            var created = Repository.Create(Owner,candidate with { Level = level, CompletedQuests = completed ?? [3145,4873], InitialLearning = learning,
                AcceptedStoryLevel = position is null ? candidate.AcceptedStoryLevel : level });
            // 实验性：给新建角色在主背包预置物品（深渊入场票测试用）。
            if (items is { Length: > 0 })
            {
                var seeded = created.Inventory.Items;
                long uid = seeded.Select(row => row.Uid).DefaultIfEmpty(0).Max() + 1;
                foreach (var (itemId, count) in items)
                {
                    short slot = 121; while (seeded.Any(row => row.Position.List == DfoServer.Game.Inventory.Models.InventoryListType.Main && row.Position.Index == slot)) slot++;
                    seeded = seeded.Add(new(0, new(DfoServer.Game.Inventory.Models.InventoryListType.Main, slot), new(itemId, ItemKind.Material, count)));
                }
                created = Repository.CommitInventory(Owner, created, new(created.Inventory, created.Inventory with { Items = seeded }, "test_seed_items"));
            }
            if (growthContractExpiresAt is { } expiry)
                Repository.CommitInventory(Owner,created,new(created.Inventory,created.Inventory with
                    { AccountWallet = created.Inventory.AccountWallet.SetContractExpiry(84,expiry) },"test_growth_contract"));
            // 选角前改写库内疲劳余额（入场一次性疲劳不足测试用）。
            if (fatigueRemaining is { } remaining) Sql("UPDATE character_fatigue SET remaining=$p0", (int)remaining);
            ICharacterRepository repository = lost || loseGrowth ? new LostResultRepository(Repository) { LoseSceneResult = lost, LoseCompletionResult = false,
                LoseGrowthResult = loseGrowth,GrowthLossCondition = growthLossCondition } : Repository;
            if (loseSettlement)
                repository = new FormalDungeonPublicationTests.LostResultRepository(repository,loseGrowth:false,loseSettlement:true);
            Session = new(repository,towns,Owner,questCatalog: quests,
                contracts: contracts,timeProvider: timeProvider,stageGraph: stages);
            Session.Select(0, resumeTown: position is not null); Session.MarkReady();
        }
        public Character Read() => Repository.LoadCharacter(Owner,0);
        public (long Count,long Amount) GrowthReceipt(Guid eventId)
        {
            using var db = new SqliteConnection("Data Source="+path+";Pooling=False"); db.Open();
            using var command = db.CreateCommand();
            command.CommandText = "SELECT count(*),coalesce(max(amount),0) FROM character_dungeon_growth_receipts WHERE character_id=$id AND event_id=$event";
            command.Parameters.AddWithValue("$id",Read().Id); command.Parameters.AddWithValue("$event",eventId.ToString("N"));
            using var reader = command.ExecuteReader(); reader.Read();
            return (reader.GetInt64(0),reader.GetInt64(1));
        }
        public Dictionary<(byte,byte),(byte,byte)> TravelPaths(TownCatalog towns, bool includeTransport = false)
        {
            if (towns.IsRoom(Session.Selected!.Position)) That(Session.LeaveRoom(),"选角后从真实房间返回城镇");
            var start = (Session.Selected!.Position.Town,Session.Selected.Position.Area);
            var pending = new Queue<(byte,byte)>(); pending.Enqueue(start);
            var previous = new Dictionary<(byte,byte),(byte,byte)> { [start] = start };
            var done = QuestEligibility.Completed(Session.Selected);
            while (pending.TryDequeue(out var next))
            {
                if (includeTransport && towns.IsRoom(new(next.Item1,next.Item2,0,0),done))
                    foreach(var portal in towns.RoomPortals)
                    {
                        var destination=(portal.Town,portal.Area);
                        if (!previous.ContainsKey(destination) && towns.Admission(new(next.Item1,next.Item2,0,0),
                            new(portal.Town,portal.Area,0,0),Session.Selected.Level,done,roomPortal:true)=="ready")
                        { previous.Add(destination,next); pending.Enqueue(destination); }
                    }
                // 跨大陆任务须走当前阶段真实码头，不能把仅步行不可达误判为任务断路。
                foreach (var stop in includeTransport ? towns.Dock.Active(done) : [])
                {
                    var destination=(stop.Point.Town,stop.Point.Area);
                    if (!previous.ContainsKey(destination) && towns.Dock.Admission(new(next.Item1,next.Item2,0,0),stop.Point,Session.Selected.Level,done)=="ready" &&
                        towns.Admission(new(next.Item1,next.Item2,0,0),stop.Point,Session.Selected.Level,done)=="ready")
                    { previous.Add(destination,next); pending.Enqueue(destination); }
                }
                foreach (var link in towns.Areas[next].Map(done).Links.Where(link => link.Gate.Door && link.Town != 255))
                {
                    var destination = (link.Town,link.Area);
                    if (!previous.ContainsKey(destination) && towns.Admission(new(next.Item1,next.Item2,0,0),
                        new(link.Town,link.Area,0,0),Session.Selected.Level,done) == "ready")
                    { previous.Add(destination,next); pending.Enqueue(destination); }
                }
            }
            return previous;
        }
        public void TravelTo(TownCatalog towns,Location position, bool includeTransport = false)
        {
            var previous = TravelPaths(towns,includeTransport);
            var start = (Session.Selected!.Position.Town,Session.Selected.Position.Area); var target = (position.Town,position.Area);
            var done = QuestEligibility.Completed(Session.Selected);
            That(previous.ContainsKey(target),"实际PVF城镇连接可达任务场景");
            var steps = new List<(byte,byte)>();
            for (var next = target; next != start; next = previous[next]) steps.Add(next);
            steps.Reverse();
            foreach (var next in steps)
            {
                var bounds = towns.Areas[next].Map(done).Bounds.First(row => row.Conditions.Length == 0 && row.Quests.All(done.Contains)).Bounds;
                var source = Session.Selected.Position;
                That(Session.Travel(new(next.Item1,next.Item2,checked((ushort)(bounds.X + bounds.Width / 2)),
                    checked((ushort)(bounds.Y + bounds.Height / 2)),0,source.Town,source.Area,
                    towns.IsRoom(source,done) && towns.RoomPortals.Any(p=>p.Town==next.Item1 && p.Area==next.Item2)?(byte)5:(byte)0)),"按真实连接到达下一城镇区域");
            }
            That(Session.Move(new(position.X,position.Y,0,0,0,0)),"任务测试落点符合城镇可行走资源");
        }
        public void Sql(string sql,params object[] values)
        {
            using var db = new SqliteConnection("Data Source="+path+";Pooling=False"); db.Open();
            using var command = db.CreateCommand(); command.CommandText = sql;
            for (int index=0;index<values.Length;index++) command.Parameters.AddWithValue("$p"+index,values[index]);
            command.ExecuteNonQuery();
        }
        public object? Query(string sql)
        {
            using var db = new SqliteConnection("Data Source="+path+";Pooling=False"); db.Open();
            using var command = db.CreateCommand(); command.CommandText = sql;
            return command.ExecuteScalar();
        }
        public void Dispose() { Session.End(); Owner.Dispose(); Directory.Delete(root,true); }
    }
}
