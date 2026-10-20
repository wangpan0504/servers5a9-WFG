using System.Buffers.Binary;
using System.Net;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Quests;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Network.Parsers;
using DfoServer.Network;
using DfoServer.Network.Handlers;
using DfoServer.Network.Protocol;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class QuestCinematicSettlementTests
{
    // 真实3599整条叠层路线；敌对死亡和终末33明确合成，不代表客户端播放或实发顺序。
    public static void Run(TownCatalog towns, PvfArchive pvf, CharacterCatalog characters, ItemResources items,
        QuestCatalog quests, SkillLearningCatalog skills)
    {
        int checks = Count;
        var growth = global::DfoServer.Testing.SelfTestCatalogs.Growth(pvf, skills);
        var kills = global::DfoServer.Testing.SelfTestCatalogs.KillExperience(pvf);
        var clears = global::DfoServer.Testing.SelfTestCatalogs.ClearExperience(pvf);
        var parameters = global::DfoServer.Testing.SelfTestCatalogs.QuestRewardParameters(pvf);
        var connections = global::DfoServer.Testing.SelfTestCatalogs.QuestConnections(pvf);
        var dungeons = global::DfoServer.Testing.SelfTestCatalogs.Dungeons(pvf); var rooms = global::DfoServer.Testing.SelfTestCatalogs.Rooms(pvf);
        var area = towns.Areas.Values.First(row => row.Level <= 74 && row.DungeonWorldMap is { } world &&
            towns.DungeonMaps[world].Options.Any(option => option.DungeonId == 96 &&
                (option.QuestId == -1 || option.InProgress && option.QuestId == 3599 || !option.InProgress && option.QuestId != 3599)));
        var done = new HashSet<int>(area.Quests) { 3598 };
        if (area.AnyQuests.Length > 0) done.Add(area.AnyQuests[0]);
        var option = towns.DungeonMaps[area.DungeonWorldMap!.Value].Options.First(row => row.DungeonId == 96 &&
            (row.QuestId == -1 || row.InProgress && row.QuestId == 3599 || !row.InProgress && row.QuestId != 3599));
        if (!option.InProgress && option.QuestId > 0) done.Add(option.QuestId);
        done.UnionWith(area.Map(done).DungeonGates.First(gate => gate.Conditions.Length == 0 && !gate.Quests.Contains(3599)).Quests);
        var bounds = area.Map(done).Bounds.First(row => row.Conditions.Length == 0 && row.Quests.All(done.Contains)).Bounds;
        var position = new Location(area.Town,area.Area,checked((ushort)(bounds.X+bounds.Width/2)),checked((ushort)(bounds.Y+bounds.Height/2)));
        foreach (var scenario in new[] { (Request:3599u,Finish:false,Replay:false),(Request:0u,Finish:false,Replay:false),
            (Request:3599u,Finish:true,Replay:false),(Request:3599u,Finish:false,Replay:true) })
        {
            using var f = new QuestSceneTests.Fixture(towns,characters,skills,growth,quests,false,74,
                scenario.Replay ? done.Append(3599).ToArray() : done.ToArray(),position:position);
            FormalDungeonReceipt opening;
            if (scenario.Replay)
            {
                That(connections.TryGetReplayDungeon(3599,out uint route) && route == 96 &&
                    f.Session.PrepareQuestReplay(3599,connections,dungeons,rooms) is not null &&
                    f.Session.BeginQuestReplayDungeonSelection(96).Receipt is not null,"真实关联目录允许已完成3599回放");
                opening = f.Session.EnterQuestReplay(96,3599,0)!;
                That(opening.Entry.IsQuestReplay && opening.Entry.RouteQuestId == 3599 && opening.Entry.QuestId == 0 &&
                    f.Read().ActiveMainQuests.All(row => row.QuestId != 3599),"回放保留原身份，不伪造活动接取");
            }
            else
            {
                That(f.Session.ChangeQuest(3599,QuestLifecycleAction.Accept,quests,items.Items,items.GoldLimits,items.EpicPieces)?.Commit is not null,
                    "完整演出结算沿真实前置接取3599");
                opening = f.Session.EnterFormalDungeon(96,scenario.Request,0,dungeons,rooms)!;
            }
            var loaded = f.Session.CompleteFormalDungeonLoading()!;
            That(loaded.Snapshot.CurrentMap.Id == 76112 && loaded.Monsters.Monsters.All(row => !row.IsBlocking),
                "真实76112只有非战斗演员，不虚构前序死亡");
            That(f.Session.MoveFormalDungeon(1,0) is not null,"按真实基础门墙进入76114");
            loaded = f.Session.CompleteFormalDungeonLoading()!;
            That(loaded.Snapshot.CurrentMap.Id == 76114 && f.Session.Phase == SessionPhase.Dungeon &&
                f.Session.EnableFormalDungeonSettlement() is null,"零死亡基础路线不能仅凭友方终点许可结算");
            That(f.Session.MoveFormalDungeon(1,0,1) is not null,"沿实际叠层身份进入76113");
            loaded = f.Session.CompleteFormalDungeonLoading()!;
            var enemies = loaded.Monsters.Monsters.Where(row => row.IsBlocking).ToArray();
            That(loaded.Snapshot.CurrentMap.Id == 76113 && enemies.Length == 9 &&
                f.Session.EnableFormalDungeonSettlement() is null,"76113保留九个真实敌对演员，层内未清不能结算");
            foreach (var enemy in enemies)
                That(f.Session.ObserveFormalDungeonScriptedDeath(new(enemy.ObjectKey,0x22,true,new string('A',64)),kills,growth).Outcome == DungeonRunOutcome.Accepted,
                    "合成脚本39只对真实层内敌人提交原死亡事务");
            That(f.Session.Phase == SessionPhase.Dungeon && f.Session.MoveFormalDungeon(1,0,1) is not null,
                "清完76113后返回原基础76114而非替换地图");
            That(f.Session.EnableFormalDungeonSettlement() is null,"返回加载中仍不能发布结算");
            loaded = f.Session.CompleteFormalDungeonLoading()!;
            That(loaded.Snapshot.CurrentMap.Id == 76114 && f.Session.Phase == SessionPhase.Settlement &&
                loaded.Monsters.Monsters.All(row => !row.CanReportDeath),
                "真实叠层死亡后加载全非战斗终点产生合法ClearFact，不把62959当死亡Boss");
            var before = f.Read();
            That(f.Session.EnableFormalDungeonSettlement() is null,"本次接取演出scalar1时不能提前发31");
            byte[] report = DungeonSettlementProtocolTests.Report(before.Id,99);
            That(DungeonSettlementRequests.TryParsePlayResult(report,out var parsed),"完整46正文沿原Parser");
            if (scenario.Replay)
            {
                That(f.Session.SettleFormalDungeon(parsed!.ActorId,parsed.RankPoint,parsed.MaximumRankPoint,report,dungeons,clears,growth).Outcome == DungeonRunOutcome.Accepted &&
                    f.Session.FormalDungeonResultFocus() is not null && f.Read().ActiveMainQuests.All(row => row.QuestId != 3599),
                    "已完成演出回放无接取scalar，不被新许可门永久拒绝46");
                That(f.Session.ReturnFromFormalDungeon() is not null,"3599回放结算保留正式返回城镇");
                continue;
            }
            That(f.Session.SettleFormalDungeon(parsed!.ActorId,parsed.RankPoint,parsed.MaximumRankPoint,report,dungeons,clears,growth).Outcome == DungeonRunOutcome.Rejected &&
                QuestCompletionRepositoryTests.Same(before,f.Read()),"已有合法ClearFact仍须等演出许可，提前46不提交结算");
            bool publication = scenario.Request == 3599 && !scenario.Finish;
            if (publication) Publish(f,quests,items,rooms,parameters,growth,fail:true).GetAwaiter().GetResult();
            var progress = f.Session.ChangeQuest(3599,QuestLifecycleAction.Condition,quests,items.Items,items.GoldLimits,items.EpicPieces,new(null,0,0));
            That(progress?.Outcome is QuestLifecycleOutcome.Committed or QuestLifecycleOutcome.Unchanged &&
                f.Read().ActiveMainQuests.Single(row => row.QuestId == 3599).ConditionState == 0,
                "原接取终末33只保存真实演出达成条件");
            var enabled = f.Session.EnableFormalDungeonSettlement();
            That(enabled is { BossKey: null } && enabled.Value.Receipt.Snapshot.Identity == f.Session.DungeonSnapshot!.Identity,
                "原ClearFact及演出达成只许可空31，不编造115死亡key");
            if (publication) Publish(f,quests,items,rooms,parameters,growth,fail:false).GetAwaiter().GetResult();
            f.Session.ConfirmFormalDungeonSettlementEnabled(enabled!.Value.Receipt);
            That(f.Session.EnableFormalDungeonSettlement() is null,"成功发布确认后不重复许可31");
            if (scenario.Finish)
                That(f.Session.FinishQuest(3599,ushort.MaxValue,quests,parameters,items.Items,items.GoldLimits,items.EpicPieces,growth).Receipt is not null,
                    "允许33和31后先沿正常34清任务槽，再完成46");
            var settled = f.Session.SettleFormalDungeon(parsed.ActorId,parsed.RankPoint,parsed.MaximumRankPoint,report,dungeons,clears,growth);
            That(settled.Outcome == DungeonRunOutcome.Accepted && settled.Receipt is not null && f.Session.FormalDungeonResultFocus() is not null,
                "演出许可后沿原结算事务完成真实奖励与结果页");
            var committed = f.Read();
            That(f.Session.SettleFormalDungeon(parsed.ActorId,parsed.RankPoint,parsed.MaximumRankPoint,report,dungeons,clears,growth).Outcome == DungeonRunOutcome.Duplicate &&
                QuestCompletionRepositoryTests.Same(committed,f.Read()),"同一46精确重试不重复奖励");
            That(f.Session.ReturnFromFormalDungeon() is not null && !f.Session.CanPublishDungeonSettlement(enabled.Value.Receipt),
                "正式返回释放原结算实例，旧许可回执不可发布");
            That(opening.Entry.Maze.LayeredMaps.Single().Id == 76113,"完整路线保留真实叠层资源");
        }
        VerifyMetroCinematics(towns,characters,items,quests,skills,growth,dungeons,rooms,kills,clears,parameters);
        Console.WriteLine($"PASS {Count-checks} 真实演出路线、物件终末、交付与结算回归（非实机）");
    }

    // 74652的die cinematic声明2843/down，另外两条MAP ACT在开图时声明2845/2863；这里只合成39/33，不推断客户端45顺序。
    static void VerifyMetroCinematics(TownCatalog towns,CharacterCatalog characters,ItemResources items,QuestCatalog quests,
        SkillLearningCatalog skills,CharacterGrowthCatalog growth,DungeonCatalog dungeons,DungeonRoomCatalog rooms,
        DungeonKillExperienceCatalog kills,DungeonClearExperienceCatalog clears,QuestRewardParameterCatalog parameters)
    {
        foreach (var scenario in new[] { (Quest:3984,Dungeon:312u,Request:3984u,Ordinal:8,Map:76979,Movie:2843),
            (Quest:3987,Dungeon:312u,Request:0u,Ordinal:10,Map:76997,Movie:2845),
            (Quest:3991,Dungeon:313u,Request:0u,Ordinal:4,Map:77037,Movie:2863) })
        {
            ushort id = checked((ushort)scenario.Quest);
            var quest = quests.Get(id);
            That(quest.MinimumLevel == 89 && quest.Prerequisites.SequenceEqual(new[] {scenario.Quest-1}) &&
                QuestClientSceneProgress.DungeonCinematic(quest) && quest.IntData.SequenceEqual(new[] {scenario.Movie}) &&
                quest.CinematicMapId == scenario.Map,"地轨演出保持真实前置、CMT及基础地图："+id);
            var entrance = MiddleQuestEntryTests.Entrance(towns,quest,scenario.Dungeon);
            using var f = new QuestSceneTests.Fixture(towns,characters,skills,growth,quests,false,89,
                entrance.Completed,position:entrance.Position);
            That(!f.Read().CompletedQuests.Contains(id) && f.Session.ChangeQuest(id,QuestLifecycleAction.Accept,
                quests,items.Items,items.GoldLimits,items.EpicPieces)?.Commit is not null,"演出沿真实31建立原接取："+id);
            var accepted = f.Read().ActiveMainQuests.Single(row => row.QuestId == id);
            ushort selector = QuestFixedRewardDefinition.Parse(quest with { Rewards=quest.Definition!.SelectRewards })
                .ForJob(f.Read().Job,f.Read().GrowType).Length == 0 ? ushort.MaxValue : (ushort)0;
            var opening = f.Session.EnterFormalDungeon(scenario.Dungeon,scenario.Request,0,dungeons,rooms)!;
            That(opening is not null && opening.Entry.RouteQuestId == id && opening.Entry.QuestId == scenario.Request &&
                opening.Entry.Maze.SourceOrdinal == scenario.Ordinal && opening.Entry.BossMap.Id == scenario.Map &&
                QuestHuntProgress.Bound(accepted,opening.Entry) && (scenario.Request != 0 ||
                    opening.Entry.QuestAcceptanceId == Guid.Empty && opening.Entry.ActiveQuestSlot == byte.MaxValue),
                "演出显式或普通0保留原路线与接取，0不冒充任务实例号："+id);
            var before = f.Read();
            That(Condition() is null && QuestCompletionRepositoryTests.Same(before,f.Read()),
                "加载中33不能提前完成演出："+id);
            var receipt = f.Session.CompleteFormalDungeonLoading()!;
            That(Condition()?.Outcome == QuestLifecycleOutcome.Rejected && Finish().Outcome == QuestFinishOutcome.Rejected &&
                QuestCompletionRepositoryTests.Same(before,f.Read()),"错误起点地图33与提前34均拒绝："+id);
            int deaths = 0;
            var path = QuestStoryRouteTests.Path(opening!.Entry.Maze.StaticTopology,opening.Entry.StartMap.Position,opening.Entry.BossMap.Position);
            foreach (var point in path.Skip(1))
            {
                foreach (var monster in receipt.Monsters.Monsters.Where(row => row.IsBlocking))
                {
                    That(!monster.IsScriptedBoss && f.Session.ObserveFormalDungeonDeath(
                        FormalDungeonSessionTests.Observation(receipt,monster),kills,growth).Outcome == DungeonRunOutcome.Accepted,
                        "演出前房只提交真实生成敌人的合成死亡："+id);
                    deaths++;
                }
                That(f.Session.MoveFormalDungeon(checked((byte)point.X),checked((byte)point.Y)) is not null,"按真实门墙抵达演出基础图："+id);
                receipt = f.Session.CompleteFormalDungeonLoading()!;
            }
            That(deaths > 0 && receipt.Snapshot.CurrentMap.Id == scenario.Map &&
                f.Read().ActiveMainQuests.Single(row => row.QuestId == id).ConditionState == 1,"完整前房战斗不代替type65终末33："+id);
            byte[] report = DungeonSettlementProtocolTests.Report(f.Read().Id,99);
            That(DungeonSettlementRequests.TryParsePlayResult(report,out var parsed),"地轨演出46使用原Parser："+id);
            ushort? expectedBoss = null;
            if (id == 3984)
            {
                var boss = receipt.Monsters.Monsters.Single(row => row.MonsterId == 74652);
                That(boss.IsBoss && boss.CanReportDeath && boss.IsBlocking && !boss.IsFriendly &&
                    receipt.Monsters.Monsters.Single(row => row.MonsterId == 74653) is { IsFriendly:true,CanReportDeath:false } &&
                    opening.Entry.Maze.LayeredMaps.Single().Id == 76989,"3984普通敌对Boss、友方及同坐标叠层均保留");
                var death = f.Session.ObserveFormalDungeonDeath(FormalDungeonSessionTests.Observation(receipt,boss),kills,growth);
                That(death is { Outcome:DungeonRunOutcome.Accepted,ClearFact.ConfirmedDeathEvents.Count:>0 } &&
                    death.ClearFact.ConfirmedDeathEvents.Count == deaths+1,"74652合法死亡形成普通通关事实，不把友方算入死亡集合");
                expectedBoss = boss.ObjectKey;
                That(f.Session.EnableFormalDungeonSettlement()?.BossKey == expectedBoss && Settle().Outcome == DungeonRunOutcome.Accepted &&
                    f.Read().ActiveMainQuests.Single(row => row.QuestId == id).ConditionState == 1,
                    "普通Boss可在终末33之前46结算，不套用全非战斗演出的窄门禁");
            }
            else if (id == 3987)
            {
                var boss = receipt.Monsters.Monsters.Single(row => row.MonsterId == 75099);
                That(boss is { IsScriptedBoss:true,CanReportDeath:true,IsBlocking:true,IsScriptedRetirement:false } &&
                    receipt.Monsters.Monsters.Single(row => row.MonsterId == 66022).CanReportDeath &&
                    receipt.Monsters.Monsters.Single(row => row.AiCharacter?.Id == 17002) is { IsFriendly:true,CanReportDeath:false } &&
                    opening.Entry.Maze.ClearConditions is [{ Type:"destroy object",TargetId:85194,Count:1 }] &&
                    receipt.Monsters.Room.PassiveObjects.Count(row => row.ObjectId == 85194) == 1 &&
                    receipt.Monsters.Room.PassiveObjects[15].ObjectId == 85194,"3987保留可报告dummy Boss、友方及唯一物件终末合同");
                before = f.Read();
                That(Passive() is null && Settle().Outcome == DungeonRunOutcome.Rejected && QuestCompletionRepositoryTests.Same(before,f.Read()),
                    "85194不能越过尚存75099先签发场景清图或46奖励");
                // CMT声明reward1不等于实发39；此处明确提交真实计划允许报告的75099对象key。
                var death = f.Session.ObserveFormalDungeonScriptedDeath(new(boss.ObjectKey,0x22,true,new string('D',64)),kills,growth);
                That(death is { Outcome:DungeonRunOutcome.Accepted,ClearFact:null } && f.Session.Phase == SessionPhase.Dungeon &&
                    f.Session.EnableFormalDungeonSettlement() is null,"合法Boss39仍须等待独立85194条件，不提前清图");
                before = f.Read();
                var passive = Passive();
                That(passive?.Commit?.Plan.SceneClear?.Result.ClearFact is { ClearMap.Id:76997,
                        PassiveSource.TemplateId:85194,PassiveSource.SourceOrdinal:15 } clear &&
                    clear.ConfirmedDeathEvents.Count == deaths+1 && clear.Identity.QuestId == 0 &&
                    passive.Commit.Plan.AcceptanceId == accepted.AcceptanceId &&
                    f.Read().ActiveMainQuests.Single(row => row.QuestId == id).ConditionState == 1 &&
                    f.Read().Learning.Experience == before.Learning.Experience && f.Session.Phase == SessionPhase.Settlement,
                    "85194原事务独立保存PassiveSource，物件与友方不加入死亡或type65进度");
                before = f.Read();
                That(Passive() is null && QuestCompletionRepositoryTests.Same(before,f.Read()),"重复终末物件39不重复清图或发奖");
                expectedBoss = boss.ObjectKey;
            }
            else
            {
                That(receipt.Monsters.Monsters is [{ MonsterId:74657,IsFriendly:true,CanReportDeath:false }] &&
                    opening.Entry.Maze.LayeredMaps.Single().Id == 77038 && f.Session.Phase == SessionPhase.Settlement,
                    "3991前房战斗后加载全友方77037形成合法终点，保留77038层且不造其dummy死亡");
                before = f.Read();
                That(f.Session.EnableFormalDungeonSettlement() is null && Settle().Outcome == DungeonRunOutcome.Rejected &&
                    QuestCompletionRepositoryTests.Same(before,f.Read()),"3991本次演出scalar1时不许空31或提前46");
            }
            before = f.Read();
            That(Finish().Outcome == QuestFinishOutcome.Rejected && QuestCompletionRepositoryTests.Same(before,f.Read()),"已通关也不能跳过演出终末34："+id);
            var identity = f.Session.DungeonSnapshot!.Identity;
            var progress = Condition();
            var after = f.Read();
            That(progress?.Outcome == QuestLifecycleOutcome.Committed && progress.DungeonIdentity == identity &&
                progress.Commit?.Plan.AcceptanceId == accepted.AcceptanceId &&
                after.ActiveMainQuests.Single(row => row.QuestId == id).ConditionState == 0 &&
                QuestCompletionRepositoryTests.Same(before with { Revision=after.Revision,ActiveMainQuests=after.ActiveMainQuests },after),
                "匹配基础MAP的终末33只保存原接取scalar，不另造清图或资产："+id);
            That(Condition()?.Outcome == QuestLifecycleOutcome.Unchanged && QuestCompletionRepositoryTests.Same(after,f.Read()),"重复演出33只返回已存进度："+id);
            var enabled = f.Session.EnableFormalDungeonSettlement();
            That(enabled is not null && enabled.Value.BossKey == expectedBoss,"结算许可只引用实际死亡Boss，3991保持空key："+id);
            f.Session.ConfirmFormalDungeonSettlementEnabled(enabled!.Value.Receipt);
            That(f.Session.EnableFormalDungeonSettlement() is null,"同实例结算许可确认后不重复："+id);
            if (id != 3984) That(Settle().Outcome == DungeonRunOutcome.Accepted,"终末许可后沿原46结算："+id);
            before = f.Read();
            That(Settle().Outcome == DungeonRunOutcome.Duplicate && QuestCompletionRepositoryTests.Same(before,f.Read()),"重复46不重复资产："+id);
            var finished = Finish();
            That(finished.Receipt?.Commit is { ConditionType:65,Clear:null } complete && complete.AcceptanceId == accepted.AcceptanceId &&
                f.Read().CompletedQuests.Contains(id),"真实type65按原接取34交付，不借死亡或物件凭据替代演出："+id);
            before = f.Read();
            That(Finish().Outcome == QuestFinishOutcome.AlreadyCompleted && QuestCompletionRepositoryTests.Same(before,f.Read()),"重复演出34不二次领奖："+id);
            That(f.Session.ReturnFromFormalDungeon() is not null && !f.Session.CanPublishDungeonSettlement(enabled.Value.Receipt),"回城撤销旧实例发布资格："+id);
            f.Session.ReturnToRoster(); f.Session.Select(0); f.Session.MarkReady();
            That(f.Read().CompletedQuests.Contains(id) && f.Read().ActiveMainQuests.All(row => row.QuestId != id) &&
                f.Query("SELECT count(*) FROM character_quest_completion_receipts WHERE quest_id="+id) is 1L,
                "重选保留实际演出完成位及唯一领奖回执："+id);

            QuestLifecycleReceipt? Condition() => f.Session.ChangeQuest(id,QuestLifecycleAction.Condition,quests,items.Items,items.GoldLimits,items.EpicPieces,new(null,0,0));
            QuestLifecycleReceipt? Passive() => f.Session.ObserveFormalPassiveDeath(85194,true,new string('E',64),rooms);
            QuestFinishResult Finish() => f.Session.FinishQuest(id,selector,quests,parameters,items.Items,items.GoldLimits,items.EpicPieces,growth);
            FormalDungeonSettlementResult Settle() => f.Session.SettleFormalDungeon(parsed!.ActorId,parsed.RankPoint,parsed.MaximumRankPoint,report,dungeons,clears,growth);
        }
        Verify3984CinematicLayer(towns,characters,items,quests,skills,growth,dungeons,rooms,kills,parameters);
    }

    // 2843 末段 CHANGE MAP 在 Boss 死亡后才发 kind1。结算阶段仍要进入同坐标 76989，终末 33 在该层归零。
    static void Verify3984CinematicLayer(TownCatalog towns,CharacterCatalog characters,ItemResources items,QuestCatalog quests,
        SkillLearningCatalog skills,CharacterGrowthCatalog growth,DungeonCatalog dungeons,DungeonRoomCatalog rooms,
        DungeonKillExperienceCatalog kills,QuestRewardParameterCatalog parameters)
    {
        const ushort id = 3984;
        var quest = quests.Get(id);
        var entrance = MiddleQuestEntryTests.Entrance(towns,quest,312);
        using var f = new QuestSceneTests.Fixture(towns,characters,skills,growth,quests,false,89,entrance.Completed,position:entrance.Position);
        That(f.Session.ChangeQuest(id,QuestLifecycleAction.Accept,quests,items.Items,items.GoldLimits,items.EpicPieces)?.Commit is not null,
            "3984换层路线沿真实前置接取");
        var opening = f.Session.EnterFormalDungeon(312,id,0,dungeons,rooms)!;
        var receipt = f.Session.CompleteFormalDungeonLoading()!;
        var path = QuestStoryRouteTests.Path(opening.Entry.Maze.StaticTopology,opening.Entry.StartMap.Position,opening.Entry.BossMap.Position);
        foreach (var point in path.Skip(1))
        {
            foreach (var monster in receipt.Monsters.Monsters.Where(row => row.IsBlocking))
                That(f.Session.ObserveFormalDungeonDeath(FormalDungeonSessionTests.Observation(receipt,monster),kills,growth).Outcome == DungeonRunOutcome.Accepted,
                    "3984换层前只提交真实阻挡敌人");
            That(f.Session.MoveFormalDungeon(checked((byte)point.X),checked((byte)point.Y)) is not null,"3984沿真实门墙走到演出图");
            receipt = f.Session.CompleteFormalDungeonLoading()!;
        }
        var boss = receipt.Monsters.Monsters.Single(row => row.MonsterId == 74652);
        That(receipt.Snapshot.CurrentMap.Id == 76979 &&
            f.Session.ObserveFormalDungeonDeath(FormalDungeonSessionTests.Observation(receipt,boss),kills,growth).ClearFact is not null,
            "74652死亡后仍在基础演出图，并形成通关事实");
        var position = receipt.Snapshot.CurrentMap.Position;
        That(f.Session.MoveFormalDungeon(checked((byte)position.X),checked((byte)position.Y),1) is not null,
            "结算阶段接受演出末段的同坐标kind1");
        var layer = f.Session.CompleteFormalDungeonLoading()!;
        That(layer.Snapshot.CurrentMap.Id == 76989 && layer.Snapshot.CurrentMap.Kind == "layered","kind1进入真实叠层76989");
        var progress = f.Session.ChangeQuest(id,QuestLifecycleAction.Condition,quests,items.Items,items.GoldLimits,items.EpicPieces,new(null,0,0));
        That(progress?.Outcome == QuestLifecycleOutcome.Committed &&
            f.Read().ActiveMainQuests.Single(row => row.QuestId == id).ConditionState == 0,
            "叠层上的终末33仍保存原接取演出条件");
        That(f.Session.FinishQuest(id,ushort.MaxValue,quests,parameters,items.Items,items.GoldLimits,items.EpicPieces,growth).Receipt?.Commit.Character.CompletedQuests.Contains(id) == true,
            "演出条件归零后按原34交付3984");
    }

    // 合成发送故障后保留同Session检查发布边界；不是TCP断线重连恢复声明。
    static async Task Publish(QuestSceneTests.Fixture f,QuestCatalog quests,ItemResources items,DungeonRoomCatalog rooms,
        QuestRewardParameterCatalog parameters,CharacterGrowthCatalog growth,bool fail)
    {
        string directory = Path.Combine(Path.GetTempPath(),"S5A9-movie-publication-"+Guid.NewGuid().ToString("N"));
        var before = f.Read();
        using (var log = new ServerLog(directory))
        using (var stop = new CancellationTokenSource())
        using (var output = new MovieOutput(fail))
        {
            await using var sender = new GameConnectionSender(f.Owner,output,stop,0,(_,_,_,_,_) => { });
            var context = new GameConnectionContext(new(176,IPAddress.Loopback,output,stop),f.Owner,f.Session,sender);
            var commands = new GameCommandRegistry();
            new QuestConditionHandler(log,quests,items.Items,items.GoldLimits,items.EpicPieces,rooms,parameters,growth).Register(commands);
            commands.Seal();
            var packet = new Packet(1,33,[33,0,15,14,0,0]);
            if (fail)
            {
                bool failed = false;
                try { await commands.DispatchAsync(context,packet); }
                catch (IOException) { failed = true; }
                That(failed && stop.IsCancellationRequested && sender.PendingBytes == 0 && output.Frames.Count == 1 &&
                    output.Frames[0].AsSpan(16).SequenceEqual(new byte[] {1,15,14,0,0,0,0}) &&
                    f.Read().Revision == before.Revision+1 && f.Read().ActiveMainQuests.Single(row => row.QuestId == 3599).ConditionState == 0 &&
                    f.Session.EnableFormalDungeonSettlement() is not null,
                    "31发送失败传播取消且释放预算，已提交演出条件不回滚、许可尚未确认");
            }
            else
            {
                That(await commands.DispatchAsync(context,packet) == CommandResult.Handled && !stop.IsCancellationRequested &&
                    output.Frames.Select(frame => BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(1))).SequenceEqual(new ushort[] {33,31}) &&
                    output.Frames[1].Length == 16 && QuestCompletionRepositoryTests.Same(before,f.Read()) &&
                    f.Session.EnableFormalDungeonSettlement() is null,
                    "同Session健康发布者的Unchanged33补发空31，不重复提交演出进度");
                That(await commands.DispatchAsync(context,packet) == CommandResult.Handled && output.Frames.Count == 3 &&
                    BinaryPrimitives.ReadUInt16LittleEndian(output.Frames[2].AsSpan(1)) == 33 &&
                    QuestCompletionRepositoryTests.Same(before,f.Read()),"成功确认31后重复33只回复ACK");
            }
        }
        Directory.Delete(directory,true);
    }

    sealed class MovieOutput(bool fail) : MemoryStream
    {
        public List<byte[]> Frames { get; } = [];
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,CancellationToken cancellationToken = default)
        {
            if (fail && BinaryPrimitives.ReadUInt16LittleEndian(buffer.Span[1..]) == 31)
                throw new IOException("合成演出31发送故障");
            Frames.Add(buffer.ToArray());
            return ValueTask.CompletedTask;
        }
    }
}
