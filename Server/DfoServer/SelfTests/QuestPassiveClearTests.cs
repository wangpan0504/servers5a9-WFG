using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Quests;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Network.Parsers;
using DfoServer.Testing;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class QuestPassiveClearTests
{
    // 复用本轮已装配目录；资源和合成报告断言不代替Windows客户端验收。
    internal static void Run(TownCatalog towns, CharacterCatalog characters, ItemResources items, QuestCatalog quests,
        SkillLearningCatalog skills, CharacterGrowthCatalog growth, DungeonCatalog dungeons, DungeonRoomCatalog rooms,
        DungeonKillExperienceCatalog kills, DungeonClearExperienceCatalog clears, QuestRewardParameterCatalog parameters,
        DungeonDropRuleCatalog cardRules, DungeonCardPriceCatalog cardPrices)
    {
        int checks = Count;
        var ids = Enumerable.Range(29000,999).Where(id => !quests.TryGet(id,out _)).Take(2).ToArray();
        var counted = QuestEligibilityTests.Quest(ids[0]) with { Condition="[condition under clear]",SubType=6,IntData=[191,-1],
            CompleteNpc=-1,Attributes=["not give exp quest"],Rewards=[] };
        var single = counted with { Id=ids[1],Condition="[clear map]",SubType=-1,IntData=[70407],DungeonIndex=191 };
        var withClearTargets = new QuestCatalog(quests.All.Concat([counted,single]).ToDictionary(row=>row.Id));
        var area = towns.Areas.Values.First(row => row.Level <= 85 && row.DungeonWorldMap is { } world &&
            towns.DungeonMaps[world].Options.Any(option => option.DungeonId == 191));
        var done = new HashSet<int>(area.Quests) { 3841 };
        if (area.AnyQuests.Length > 0) done.Add(area.AnyQuests[0]);
        var bounds = area.Map(done).Bounds.First(row => row.Conditions.Length == 0 && row.Quests.All(done.Contains)).Bounds;
        var position = new Location(area.Town,area.Area,checked((ushort)(bounds.X+bounds.Width/2)),checked((ushort)(bounds.Y+bounds.Height/2)));
        foreach (bool ordinary in new[] { false, true })
        foreach (string mode in new[] { "normal", "rollback", "lost", "stale" })
        {
            var catalog = mode == "normal" ? withClearTargets : quests;
            using var f = new QuestSceneTests.Fixture(towns,characters,skills,growth,catalog,mode is "lost" or "stale",85,done.ToArray(),position:position);
            That(f.Session.ChangeQuest(3842,QuestLifecycleAction.Accept,quests,items.Items,items.GoldLimits,items.EpicPieces)?.Commit is not null,
                "实际3842按Lv85和前置3841正常接取");
            if (mode == "normal")
                foreach (var target in new[] { counted,single })
                    That(f.Session.ChangeQuest(checked((ushort)target.Id),QuestLifecycleAction.Accept,catalog,items.Items,items.GoldLimits,items.EpicPieces)?.Commit is not null,
                        "并行合成sub6及单地图目标沿普通接取绑定到同一实际实例");
            var opening = f.Session.EnterFormalDungeon(191,ordinary ? 0u : 3842u,0,dungeons,rooms)!;
            That(opening is not null && opening.Entry.BossMap.Id == 70407 &&
                opening.Entry.Maze.ClearConditions is [{ Type: "destroy object", TargetId: 69291, Count: 1 }],
                "实际D191选择3842的物件终末路线");
            var path = QuestStoryRouteTests.Path(opening!.Entry.Maze.StaticTopology,opening.Entry.StartMap.Position,opening.Entry.BossMap.Position);
            FormalDungeonReceipt receipt = opening;
            var killed = new Dictionary<int,int>();
            foreach (var point in path)
            {
                receipt = f.Session.CompleteFormalDungeonLoading()!;
                if (point != opening.Entry.BossMap.Position)
                    That(f.Session.ObserveFormalPassiveDeath(69291,true,new string('A',64),rooms) is null,
                        "未到实际终点不能借物件模板提前完成");
                foreach (var monster in receipt.Monsters.Monsters.Where(row => row.IsBlocking))
                {
                    That(f.Session.ObserveFormalDungeonDeath(FormalDungeonSessionTests.Observation(receipt,monster),kills,growth).Outcome == DungeonRunOutcome.Accepted,
                        "前序每个真实生成key沿成长事务完成击杀");
                    killed[monster.MonsterId]=killed.GetValueOrDefault(monster.MonsterId)+1;
                }
                if (point == opening.Entry.BossMap.Position) break;
                var next = path[Array.IndexOf(path,point)+1];
                That(f.Session.MoveFormalDungeon(checked((byte)next.X),checked((byte)next.Y)) is not null,"真实门墙路径进入下一房间");
            }
            var before = f.Read(); var identity = f.Session.DungeonSnapshot!.Identity;
            That(killed.GetValueOrDefault(69723)>=20 && killed.GetValueOrDefault(69722)>=20 &&
                before.ActiveMainQuests.Single(row => row.QuestId == 3842).ConditionState == 0 &&
                f.Session.Phase == SessionPhase.Dungeon && f.Session.EnableFormalDungeonSettlement() is null,
                "两路完整20次猎杀已经归零，但场景物件未提交仍无结算许可");
            var passive = receipt.Monsters.Room.PassiveObjects.Single(row => row.ObjectId == 69291);
            That(!rooms.ReportsQuestDeath(passive) && receipt.Monsters.Monsters.All(row => !row.IsBlocking),
                "实际终点只有友方演员和无quest type的69291，不能制造Boss死亡");
            byte[] body = new byte[52]; BinaryPrimitives.WriteUInt32LittleEndian(body,69291);
            BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(4),ushort.MaxValue); body[37]=1;
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(42),1); FormalDungeonTcpTests.SealDeath(body);
            That(DungeonRequests.TryParsePassiveDeath(body,out var parsed) && parsed!.ChecksumMatches,"合成52B物件报告通过完整摘要解析");
            string fingerprint = Convert.ToHexString(SHA256.HashData(body));
            That(f.Session.ObserveFormalPassiveDeath(69291,false,fingerprint,rooms) is null &&
                f.Session.ObserveFormalPassiveDeath(69292,true,fingerprint,rooms) is null && QuestCompletionRepositoryTests.Same(before,f.Read()),
                "错误摘要或非本地图物件不能改变任务、资产或结算");
            if (mode == "rollback")
            {
                f.Sql("CREATE TRIGGER passive_clear_abort AFTER INSERT ON character_dungeon_scene_clear_receipts BEGIN SELECT RAISE(ABORT,'scene clear rollback'); END");
                Reject<SqliteException>(() => Observe(),"scene clear末段SQL故障完整回滚");
                That(QuestCompletionRepositoryTests.Same(before,f.Read()) && f.Session.DungeonSnapshot.Identity==identity,
                    "回滚保留原资产、猎杀计数和Run，不发布31");
                f.Sql("DROP TRIGGER passive_clear_abort");
            }
            if (mode is "lost" or "stale") Reject<IOException>(() => Observe(),"场景清图已提交但返回结果丢失");
            if (mode == "stale") f.Sql("UPDATE characters SET revision=revision+1");
            var progress = Observe()!;
            if (mode == "stale")
            {
                That(progress.Commit is { Duplicate:true,ProjectionCurrent:false } && !f.Session.CanPublishQuestLifecycle(progress) &&
                    f.Session.Phase==SessionPhase.Dungeon && f.Session.DungeonSnapshot.Identity==identity,
                    "过期revision不能用已存场景来源推进Run或发布旧结果");
                continue;
            }
            var sceneClear = progress.Commit?.Plan.SceneClear?.Result.ClearFact;
            That(progress.Commit is { ProjectionCurrent:true } && progress.Commit.Duplicate == (mode=="lost") &&
                sceneClear is { PassiveSource.TemplateId:69291,ConfirmedDeathEvents.Count:>0 } &&
                sceneClear.Identity.QuestId == (ordinary ? 0u : 3842u) &&
                sceneClear.ConfirmedDeathEvents.Count==killed.Values.Sum() &&
                f.Session.Phase==SessionPhase.Settlement && f.Session.DungeonSnapshot.Identity.Epoch==identity.Epoch+1,
                "明确提交后按真实物件来源一次转入结算，死亡集合仍只有前序实际击杀");
            var saved = f.Read();
            if (mode == "normal")
                foreach (var target in new[] { counted,single })
                    That(f.Repository.LoadQuestClear(f.Owner,saved,checked((ushort)target.Id)) is { SourceType:QuestClearSourceType.PassiveObject,MapId:70407 },
                        "场景事务同时归零条件目标，ReadClear使用独立物件来源而非growth外键");
            That(Observe() is null && f.Session.ObserveFormalPassiveTrigger(3842,new(0x10,0,0),fingerprint,rooms) is null &&
                QuestCompletionRepositoryTests.Same(saved,f.Read()),"39重发和不匹配猎杀33不重复场景、任务或资产");
            var enabled=f.Session.EnableFormalDungeonSettlement();
            That(enabled is { BossKey:null },"终点按空31许可结算，不伪造115 Boss key");
            f.Session.ConfirmFormalDungeonSettlementEnabled(enabled!.Value.Receipt);
            That(f.Session.EnableFormalDungeonSettlement() is null,"同实例成功发布后不重复31");
            byte[] report= DungeonSettlementProtocolTests.Report(saved.Id,99);
            That(DungeonSettlementRequests.TryParsePlayResult(report,out var result),"正式46保持本版完整报告形状");
            if (mode=="normal" && !ordinary)
            {
                var originalClear = f.Repository.LoadQuestClear(f.Owner,saved,checked((ushort)counted.Id));
                var claim = QuestCompletionPlan.Prepare(saved,counted,ushort.MaxValue,catalog,parameters,items.Items,
                    items.GoldLimits,items.EpicPieces,growth,DateTimeOffset.UtcNow,originalClear).Plan;
                That(originalClear is not null && claim is not null,"来源改写回归先保留实际scene的原ReadClear及合法34计划");
                string originalEvent = (string)f.Query("SELECT scene_event FROM character_quest_lifecycle_receipts WHERE scene_event IS NOT NULL")!;
                var frozenEvent = JsonSerializer.Deserialize<QuestSceneEvent>(originalEvent)!;
                foreach (string mutation in new[] { "fingerprint","lifecycle-identity" })
                {
                    if (mutation=="fingerprint")
                        f.Sql("UPDATE character_dungeon_scene_clear_receipts SET fingerprint=$p0",new string('C',64));
                    else
                        f.Sql("UPDATE character_quest_lifecycle_receipts SET scene_event=$p0 WHERE scene_event IS NOT NULL",
                            JsonSerializer.Serialize(frozenEvent with { Identity = frozenEvent.Identity with
                                { TaskAttempt = frozenEvent.Identity.TaskAttempt+1 } }));
                    Reject<InvalidDataException>(()=>f.Repository.LoadQuestClear(f.Owner,saved,checked((ushort)counted.Id)),
                        "ReadClear拒绝有合法长度摘要或原生命周期身份改写的scene："+mutation);
                    Reject<InvalidDataException>(()=>f.Repository.CommitQuestCompletion(f.Owner,claim!),
                        "原合法34计划在自身事务仍拒绝后来改写的scene，不借旧ReadClear绕过："+mutation);
                    Reject<InvalidDataException>(()=>f.Session.SettleFormalDungeon(result!.ActorId,result.RankPoint,result.MaximumRankPoint,
                        report,dungeons,clears,growth),"46在原事务拒绝改写的scene来源："+mutation);
                    That(QuestCompletionRepositoryTests.Same(saved,f.Read()) && f.Session.Phase==SessionPhase.Settlement &&
                        f.Session.DungeonSnapshot!.Identity==sceneClear!.Identity &&
                        f.Query("SELECT count(*) FROM character_dungeon_settlement_receipts") is 0L &&
                        f.Query("SELECT count(*) FROM character_quest_completion_receipts") is 0L,
                        "来源拒绝不改经验、资产、任务、epoch或完成/结算receipt："+mutation);
                    f.Sql("UPDATE character_dungeon_scene_clear_receipts SET fingerprint=$p0",fingerprint);
                    f.Sql("UPDATE character_quest_lifecycle_receipts SET scene_event=$p0 WHERE scene_event IS NOT NULL",originalEvent);
                }
            }
            var settled=f.Session.SettleFormalDungeon(result!.ActorId,result.RankPoint,result.MaximumRankPoint,report,dungeons,clears,growth);
            That(settled.Outcome==DungeonRunOutcome.Accepted && settled.Receipt is not null &&
                f.Session.SettleFormalDungeon(result.ActorId,result.RankPoint,result.MaximumRankPoint,report,dungeons,clears,growth).Outcome==DungeonRunOutcome.Duplicate,
                "物件来源持久核验后正常46保存通关经验且重复不发奖");
            var finish=f.Session.FinishQuest(3842,ushort.MaxValue,quests,parameters,items.Items,items.GoldLimits,items.EpicPieces,growth);
            That(finish.Receipt?.Commit is { ConditionType:6 } completed && completed.Character.CompletedQuests.Contains(3842),
                "实际猎杀任务以正式34交付，不要求把物件清图冒充猎杀或growth");
            if (mode == "normal")
                foreach (var target in new[] { counted,single })
                {
                    var claim=f.Session.FinishQuest(checked((ushort)target.Id),ushort.MaxValue,catalog,parameters,items.Items,items.GoldLimits,items.EpicPieces,growth);
                    That(claim.Receipt?.Commit.Clear is { SourceType:QuestClearSourceType.PassiveObject } &&
                        claim.Receipt.Commit.Character.CompletedQuests.Contains(target.Id),"条件通关及单地图34按原物件来源核验完整完成外键");
                }
            f.Session.ReturnToRoster(); f.Session.Select(0,resumeTown:true); f.Session.MarkReady();
            That(f.Read().CompletedQuests.Contains(3842) && f.Session.ObserveFormalPassiveDeath(69291,true,fingerprint,rooms) is null,
                "重新选角保存完成与资产，旧终点物件不能复活实例");
            QuestLifecycleReceipt? Observe()=>f.Session.ObserveFormalPassiveDeath(69291,true,fingerprint,rooms);
        }
        VerifyZeroKill(towns,characters,items,quests,skills,growth,dungeons,rooms,clears,parameters,cardRules,cardPrices);
        Console.WriteLine($"PASS {Count-checks} 实际3842及3447物件终末、场景事务及31/46/翻牌/34断言（非实机）");
    }

    static void VerifyZeroKill(TownCatalog towns, CharacterCatalog characters, ItemResources items, QuestCatalog quests,
        SkillLearningCatalog skills, CharacterGrowthCatalog growth, DungeonCatalog dungeons, DungeonRoomCatalog rooms,
        DungeonClearExperienceCatalog clears, QuestRewardParameterCatalog parameters,
        DungeonDropRuleCatalog cardRules, DungeonCardPriceCatalog cardPrices)
    {
        var dungeon = dungeons.Get(7156);
        var now = DateTimeOffset.UtcNow;
        var boundsDrawn = new List<int>();
        var policy = new DungeonCardRewardPolicy(cardRules,items.DungeonDropItems,items.Items,items.GoldLimits,items.EpicPieces,
            bound => { boundsDrawn.Add(bound); return 0; },cardPrices);
        var free = policy.Prepare(dungeon,0,0,utcNow:now);
        var zeroDraws = boundsDrawn.ToArray(); boundsDrawn.Clear();
        var positive = policy.Prepare(dungeon,0,1,utcNow:now);
        That(free.Gold==1 && free.Item==positive.Item && boundsDrawn.SequenceEqual(zeroDraws) &&
            zeroDraws.Length>=2 && zeroDraws[0]==cardRules.GoldReferences.Single(row=>row.Level==dungeon.BasisLevel).VariancePercent*2+1,
            "D7156真实kill0保留最低金币及与正死亡相同的variance、稀有度、物品抽样，不提前生成空牌");
        boundsDrawn.Clear();
        var membership = new CardMembership(now.AddDays(1).ToUnixTimeSeconds(),now.AddDays(1).ToUnixTimeSeconds());
        var memberOptions = policy.PrepareOptions(dungeon,0,0,membership,now)!;
        That(memberOptions is { Paid.Gold:0,Price:0,UseGoldCard:true,BlackDiamond.Gold:1 } &&
            memberOptions.Paid.Item==free.Item && memberOptions.BlackDiamond.Item==free.Item &&
            boundsDrawn.SequenceEqual(zeroDraws.Concat(zeroDraws)),
            "零击杀付费与有效黑钻分别完整独立抽样，58免金币资格及黑钻最低金币保持");
        var plainOptions = policy.PrepareOptions(dungeon,0,0,new(),now)!;
        That(plainOptions.Price==cardPrices.Price(dungeon.BasisLevel!.Value) && plainOptions.Paid.Gold==0 &&
            !plainOptions.UseGoldCard && plainOptions.BlackDiamond is null,
            "普通账号零击杀仍有原价付费牌，不能按死亡数拒绝合法牌型");
        int beforeNegative = boundsDrawn.Count;
        Reject<InvalidDataException>(()=>policy.Prepare(dungeon,0,-1),"负击杀数不能形成合法翻牌候选");
        That(boundsDrawn.Count==beforeNegative,"负击杀输入拒绝时不消费随机数");

        var area = towns.Areas.Values.First(row=>row.Level<=54 && row.DungeonWorldMap is { } world &&
            towns.DungeonMaps[world].Options.Any(option=>option.DungeonId==7156));
        var done = new HashSet<int>(area.Quests) { 3446 };
        if (area.AnyQuests.Length>0) done.Add(area.AnyQuests[0]);
        var bounds = area.Map(done).Bounds.First(row=>row.Conditions.Length==0 && row.Quests.All(done.Contains)).Bounds;
        var position = new Location(area.Town,area.Area,checked((ushort)(bounds.X+bounds.Width/2)),checked((ushort)(bounds.Y+bounds.Height/2)));
        foreach (string mode in new[] { "normal","rollback","lost" })
        {
            bool lost = mode=="lost";
            using var f = new QuestSceneTests.Fixture(towns,characters,skills,growth,quests,lost,54,done.ToArray(),position:position,
                contracts:items.Contracts,growthContractExpiresAt:now.AddDays(1).ToUnixTimeSeconds(),loseSettlement:lost);
            f.Session.SetCharacterGold(f.Read().Name,10000);
            That(f.Session.ChangeQuest(3447,QuestLifecycleAction.Accept,quests,items.Items,items.GoldLimits,items.EpicPieces)?.Commit is not null,
                "真实3447按Lv54及前置3446正常接取");
            var opening = f.Session.EnterFormalDungeon(7156,lost ? 0u : 3447u,0,dungeons,rooms)!;
            That(opening is not null && opening.Entry.StartMap.Id==70184 && opening.Entry.BossMap.Id==70185 &&
                opening.Entry.Maze.ClearConditions is [{ Type:"destroy object",TargetId:69292,Count:1 }],
                "真实D7156沿原70184到70185物件路线，普通0仍绑定3447接取");
            var loaded = f.Session.CompleteFormalDungeonLoading()!;
            That(loaded.Monsters.Monsters is [{ MonsterId:60409,IsBlocking:false }] &&
                f.Session.ObserveFormalPassiveDeath(69292,true,new string('A',64),rooms) is null &&
                f.Session.EnableFormalDungeonSettlement() is null,
                "首房保留友方APC60409，加载和未到终点的物件报告不提前清图");
            That(f.Session.MoveFormalDungeon(checked((byte)opening!.Entry.BossMap.Position.X),checked((byte)opening.Entry.BossMap.Position.Y)) is not null,
                "零敌怪首房按真实连接进入70185");
            loaded = f.Session.CompleteFormalDungeonLoading()!;
            var before = f.Read(); var identity = f.Session.DungeonSnapshot!.Identity;
            That(loaded.Monsters.Monsters.Count==0 && loaded.Monsters.Room.PassiveObjects is [{ ObjectId:69292 }] &&
                before.ActiveMainQuests.Single(row=>row.QuestId==3447).ConditionState==1 &&
                f.Session.ObserveFormalPassiveDeath(69292,false,new string('A',64),rooms) is null &&
                f.Session.ObserveFormalPassiveDeath(69291,true,new string('A',64),rooms) is null,
                "终点只有实际69292；无敌怪、错误摘要和错误模板都不推进sub6");
            if (mode=="rollback")
            {
                f.Sql("CREATE TRIGGER zero_scene_abort AFTER INSERT ON character_dungeon_scene_clear_receipts BEGIN SELECT RAISE(ABORT,'zero scene rollback'); END");
                Reject<SqliteException>(()=>Observe(),"零击杀scene末段SQL故障完整回滚");
                That(QuestCompletionRepositoryTests.Same(before,f.Read()) && f.Session.DungeonSnapshot.Identity==identity &&
                    f.Query("SELECT count(*) FROM character_dungeon_scene_clear_receipts") is 0L,
                    "回滚不留下空死亡清图、任务进度或新epoch");
                f.Sql("DROP TRIGGER zero_scene_abort");
            }
            if (lost) Reject<IOException>(()=>Observe(),"空死亡scene已提交后返回丢失");
            var scene = Observe()!;
            That(scene.Commit is { ProjectionCurrent:true } && scene.Commit.Duplicate==lost &&
                scene.Commit.Plan.SceneClear?.Result.ClearFact is { ConfirmedDeathEvents.Count:0,PassiveSource.TemplateId:69292 } &&
                f.Session.DungeonSnapshot.Identity.Epoch==identity.Epoch+1 && f.Query("SELECT count(*) FROM character_dungeon_growth_receipts") is 0L &&
                f.Query("SELECT length(death_events) FROM character_dungeon_scene_clear_receipts") is 0L &&
                f.Session.ReadClearedQuest(3447)?.Clear is { SourceType:QuestClearSourceType.PassiveObject,MapId:70185 },
                "原scene提交及丢返回恢复精确空growth，独立凭据推进3447并允许ReadClear");
            var enabled = f.Session.EnableFormalDungeonSettlement();
            That(enabled is { BossKey:null },"零击杀真实物件终点只许可空31，不造115 Boss key");
            f.Session.ConfirmFormalDungeonSettlementEnabled(enabled!.Value.Receipt);
            byte[] report = DungeonSettlementProtocolTests.Report(before.Id,99);
            That(DungeonSettlementRequests.TryParsePlayResult(report,out var parsed),"零击杀沿原53B正式46报告");
            if (mode=="rollback")
            {
                f.Sql("CREATE TRIGGER zero_settlement_abort AFTER INSERT ON character_dungeon_card_options BEGIN SELECT RAISE(ABORT,'zero settlement rollback'); END");
                Reject<SqliteException>(()=>Settle(),"零击杀冻结牌计划末段失败回滚成长和结算");
                That(f.Query("SELECT count(*) FROM character_dungeon_settlement_receipts") is 0L,"晚SQL失败不留下结算receipt");
                f.Sql("DROP TRIGGER zero_settlement_abort");
            }
            if (lost) Reject<IOException>(()=>Settle(),"零击杀正式46提交后返回丢失");
            var firstSettlement = mode=="normal" ? Settle() : null;
            int frozenDraws = boundsDrawn.Count;
            var settled = firstSettlement ?? Settle();
            var saved = f.Read();
            That(settled.Outcome==DungeonRunOutcome.Accepted && settled.Reward is { GrowthContractExperience:>0 } reward &&
                (ulong)reward.BaseExperience+reward.RankExperience+reward.GrowthContractExperience==reward.TotalExperience &&
                settled.Card is { Gold:1 } && settled.Options is { Paid.Gold:0 } && boundsDrawn.Count==frozenDraws &&
                f.Query("SELECT length(death_events) FROM character_dungeon_settlement_receipts") is 0L &&
                f.Query("SELECT count(*) FROM character_dungeon_settlement_receipts t JOIN character_dungeon_scene_clear_receipts s ON s.character_id=t.character_id AND s.operation_id=t.scene_operation_id") is 1L,
                "真实零击杀46保存基础、评分及契约经验和原scene外键，回滚/丢返回重试不重抽牌");
            That(Settle().Outcome==DungeonRunOutcome.Duplicate && boundsDrawn.Count==frozenDraws &&
                QuestCompletionRepositoryTests.Same(saved,f.Read()),"重复零击杀46只读原完整奖励，不重发经验或资产");
            f.Session.AdvanceFormalDungeonCards(false); f.Session.AdvanceFormalDungeonCards(true);
            var selected = f.Session.SelectFormalDungeonFreeCard(0,0);
            That(selected is not null && f.Session.SelectFormalDungeonFreeCard(0,0) is { Duplicate:true } &&
                f.Session.SelectFormalDungeonPaidCard(1,0,policy) is not null && boundsDrawn.Count==frozenDraws,
                "零击杀免费及原价付费牌均可实际领取，领取和重试不重抽");
            var finished = f.Session.FinishQuest(3447,ushort.MaxValue,quests,parameters,items.Items,items.GoldLimits,items.EpicPieces,growth);
            That(finished.Receipt?.Commit is { ConditionType:1,Clear.SourceType:QuestClearSourceType.PassiveObject } &&
                finished.Receipt.Commit.Character.CompletedQuests.Contains(3447),
                "真实3447以sub6分类及原物件来源34交付，不借空growth造击杀凭据");
            f.Session.ReturnToRoster(); f.Session.Select(0,resumeTown:true); f.Session.MarkReady();
            That(f.Read().CompletedQuests.Contains(3447) && f.Query("SELECT count(*) FROM character_dungeon_growth_receipts") is 0L &&
                f.Session.ObserveFormalPassiveDeath(69292,true,new string('A',64),rooms) is null,
                "重选恢复真实3447完成和零growth，迟到物件不能复活原实例");
            QuestLifecycleReceipt? Observe()=>f.Session.ObserveFormalPassiveDeath(69292,true,new string('A',64),rooms);
            FormalDungeonSettlementResult Settle()=>f.Session.SettleFormalDungeon(parsed!.ActorId,parsed.RankPoint,parsed.MaximumRankPoint,
                report,dungeons,clears,growth,policy);
        }
    }
}
