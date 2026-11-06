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
        var stone = new DungeonRoomPassiveObject(85176, [], "passiveobject/actionobject/map/cataclysm/metrocenter/tera/85176_tera_quest_2.obj");
        That(rooms.ReportsQuestItemReward(stone) && !rooms.ReportsQuestDeath(stone),
            "巨大的泰拉石按任务物品销毁，不是must be destroyed");
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
        VerifyLayeredPassiveClear(towns,characters,items,quests,skills,growth,dungeons,rooms,kills,clears,parameters);
        VerifyForgottenForest(towns,characters,items,quests,skills,growth,dungeons,rooms,kills,clears,parameters);
        Console.WriteLine($"PASS {Count-checks} 实际3842、3447、3858及3449物件终末、场景事务与结算边界断言（非实机）");
    }

    static void VerifyLayeredPassiveClear(TownCatalog towns, CharacterCatalog characters, ItemResources items, QuestCatalog quests,
        SkillLearningCatalog skills, CharacterGrowthCatalog growth, DungeonCatalog dungeons, DungeonRoomCatalog rooms,
        DungeonKillExperienceCatalog kills, DungeonClearExperienceCatalog clears, QuestRewardParameterCatalog parameters)
    {
        var quest = quests.Get(3858);
        That(quest.Condition == "[look cinematic]" && quest.SubType == -1 && quest.IntData.SequenceEqual(new[] { 2710 }) &&
            quest.DungeonIndex == 193, "真实3858保持type65演出目标，不改写为物件猎杀或普通清图任务");
        var area = towns.Areas.Values.First(row => row.Level <= 86 && row.DungeonWorldMap is { } world &&
            towns.DungeonMaps[world].Options.Any(option => option.DungeonId == 193));
        var done = new HashSet<int>(area.Quests) { 3857 };
        if (area.AnyQuests.Length > 0) done.Add(area.AnyQuests[0]);
        var bounds = area.Map(done).Bounds.First(row => row.Conditions.Length == 0 && row.Quests.All(done.Contains)).Bounds;
        var position = new Location(area.Town, area.Area, checked((ushort)(bounds.X + bounds.Width / 2)), checked((ushort)(bounds.Y + bounds.Height / 2)));
        foreach (string mode in new[] { "normal", "rollback", "lost", "cinematic-first", "cinematic-last", "cinematic-early", "cinematic-early-ordinary" })
        {
            bool lost = mode == "lost";
            bool cinematic = mode.StartsWith("cinematic-", StringComparison.Ordinal);
            bool ordinary = lost || mode == "cinematic-first" || mode == "cinematic-early-ordinary";
            bool early = mode.StartsWith("cinematic-early",StringComparison.Ordinal);
            using var f = new QuestSceneTests.Fixture(towns, characters, skills, growth, quests, lost, 86, done.ToArray(),
                position: position, loseSettlement: lost);
            var accepted = f.Session.ChangeQuest(3858, QuestLifecycleAction.Accept, quests, items.Items, items.GoldLimits, items.EpicPieces);
            That(accepted?.Commit is not null, "实际3858按Lv86及真实前置接取：" + accepted?.Reason);
            var active = f.Read().ActiveMainQuests.Single(row => row.QuestId == 3858);
            That(active.ConditionState == 1 && active.AcceptanceId != Guid.Empty, "3858沿原接取绑定AcceptanceId并保留未达成scalar1");
            var opening = f.Session.EnterFormalDungeon(193, ordinary ? 0u : 3858u, 0, dungeons, rooms)!;
            That(opening is { Entry.BossMap.Id: 70484, Entry.BaseBossMap.Id: 70484, Entry.PassiveClearTerminalMap.Id: 70487 } &&
                opening.Entry.RouteQuestId == 3858 && opening.Entry.QuestId == (ordinary ? 0u : 3858u) &&
                opening.Entry.Maze.ClearConditions is [{ Type: "destroy object", TargetId: 69292, Count: 1 }] &&
                opening.Entry.QuestAcceptances.Single(row => row.QuestId == 3858).AcceptanceId == active.AcceptanceId,
                "真实D193冻结70487物件末层及原接取，普通0仍保持原Boss70484和请求身份");
            var path = QuestStoryRouteTests.Path(opening!.Entry.Maze.StaticTopology, opening.Entry.StartMap.Position, opening.Entry.BossMap.Position);
            FormalDungeonReceipt receipt = opening;
            int deathCount = 0;
            foreach (var point in path)
            {
                receipt = f.Session.CompleteFormalDungeonLoading()!;
                That(f.Session.ObserveFormalPassiveDeath(69292, true, new string('A', 64), rooms) is null &&
                    f.Session.EnableFormalDungeonSettlement() is null, "基础路线加载及未到末层的69292报告不能提前结算");
                foreach (var monster in receipt.Monsters.Monsters.Where(row => row.IsBlocking).OrderBy(row => row.IsBoss).ToArray())
                {
                    var death = f.Session.ObserveFormalDungeonDeath(FormalDungeonSessionTests.Observation(receipt, monster), kills, growth);
                    That(death.Outcome == DungeonRunOutcome.Accepted && death.ClearFact is null,
                        "真实基础路线阻挡逐个提交成长，普通Boss死亡仍等待独立末层物件");
                    deathCount++;
                }
                if (point == opening.Entry.BossMap.Position) break;
                var next = path[Array.IndexOf(path, point) + 1];
                That(f.Session.MoveFormalDungeon(checked((byte)next.X), checked((byte)next.Y)) is not null,
                    "3858沿真实拓扑连接进入基础Boss房");
            }
            That(receipt.Monsters.Monsters.Any(row => row.MonsterId == 70201 && row.IsBoss && !row.IsScriptedBoss) &&
                f.Session.Phase == SessionPhase.Dungeon && f.Session.EnableFormalDungeonSettlement() is null,
                "实际非dummy基础70201已提交仍不能代替末层物件清图");
            foreach (var layer in opening.Entry.Maze.LayeredMaps.Where(map => map.Position == opening.Entry.BossMap.Position))
            {
                That(f.Session.MoveFormalDungeon(checked((byte)layer.Position.X), checked((byte)layer.Position.Y), 1) is not null,
                    "真实70484同坐标沿批准的叠层顺序进入物件层");
                if (early && layer.Id == quest.CinematicMapId)
                {
                    CompleteCinematic();
                    That(f.Session.Phase == SessionPhase.Loading && f.Session.EnableFormalDungeonSettlement() is null &&
                        f.Query("SELECT count(*) FROM character_dungeon_scene_clear_receipts") is 0L,
                        "3858进入演出层的33先于37只保存scalar，不制造物件清图或结算");
                }
                receipt = f.Session.CompleteFormalDungeonLoading()!;
                That(receipt.Snapshot.CurrentMap == layer && receipt.Entry.BossMap.Id == 70484,
                    "层加载保留实际地图身份和原Boss投影");
                foreach (var monster in receipt.Monsters.Monsters.Where(row => row.IsBlocking).ToArray())
                {
                    That(f.Session.ObserveFormalDungeonDeath(FormalDungeonSessionTests.Observation(receipt, monster), kills, growth).Outcome == DungeonRunOutcome.Accepted,
                        "真实中间或末层阻挡仍走原成长提交链");
                    deathCount++;
                }
            }
            var before = f.Read();
            var identity = f.Session.DungeonSnapshot!.Identity;
            That(receipt.Snapshot.CurrentMap.Id == 70487 && receipt.Monsters.Room.PassiveObjects.Count == 6 &&
                receipt.Monsters.Room.PassiveObjects[5].ObjectId == 69292 && !rooms.ReportsQuestDeath(receipt.Monsters.Room.PassiveObjects[5]) &&
                f.Session.Phase == SessionPhase.Dungeon && before.ActiveMainQuests.Single(row => row.QuestId == 3858).ConditionState == (early ? 0u : 1u),
                "真实末层唯一目标为普通物件row5，加载不伪造69292死亡或type65达成");
            byte[] body = QuestPassiveClearTcpTests.PassiveDeath(69292, 74);
            That(DungeonRequests.TryParsePassiveDeath(body, out var parsed) && parsed!.ChecksumMatches,
                "合成74B物件正文通过现有Parser，不代表原客户端producer已验证");
            string fingerprint = Convert.ToHexString(SHA256.HashData(body));
            That(f.Session.ObserveFormalPassiveDeath(69292, false, fingerprint, rooms) is null &&
                f.Session.ObserveFormalPassiveDeath(69311, true, fingerprint, rooms) is null &&
                QuestCompletionRepositoryTests.Same(before, f.Read()), "末层错误摘要和邻接row4物件不能借终点资格推进状态");
            // 两条独立合成事件覆盖原合同的先后兼容；已加载70487上先收到33（如ESC跳过CMT2710）会同时提交69292场景清图。
            if (mode == "cinematic-first")
            {
                var progress = CompleteCinematic(expectSceneClear: true);
                var clear = progress.Commit?.Plan.SceneClear?.Result.ClearFact;
                var eventIds = ((string)f.Query("SELECT group_concat(event_id) FROM character_dungeon_growth_receipts")!)
                    .Split(',').Select(Guid.Parse).ToHashSet();
                That(progress.Commit is { ProjectionCurrent: true, Duplicate: false } &&
                    clear is { ClearMap.Id: 70487, PassiveSource.TemplateId: 69292, PassiveSource.SourceOrdinal: 5 } &&
                    clear.ConfirmedDeathEvents.Count == deathCount && eventIds.SetEquals(clear.ConfirmedDeathEvents) &&
                    !eventIds.Contains(clear.PassiveSource!.OperationId) && f.Session.Phase == SessionPhase.Settlement &&
                    f.Session.DungeonSnapshot.Identity.Epoch == identity.Epoch + 1 &&
                    f.Query("SELECT count(*) FROM character_dungeon_scene_clear_receipts") is 1L,
                    "3858在已加载70487上先收到终末33同时达成原接取与69292场景清图，支持ESC跳过动画后结算");
            }
            else
            {
                if (mode == "rollback")
                {
                    f.Sql("CREATE TRIGGER layered_scene_abort AFTER INSERT ON character_dungeon_scene_clear_receipts BEGIN SELECT RAISE(ABORT,'layered scene rollback'); END");
                    Reject<SqliteException>(() => Observe(), "3858物件scene末段SQL失败完整回滚");
                    That(QuestCompletionRepositoryTests.Same(before, f.Read()) && f.Session.DungeonSnapshot.Identity == identity &&
                        f.Session.Phase == SessionPhase.Dungeon && f.Query("SELECT count(*) FROM character_dungeon_scene_clear_receipts") is 0L,
                        "失败不安装末层清图、来源receipt或新epoch");
                    f.Sql("DROP TRIGGER layered_scene_abort");
                }
                if (lost) Reject<IOException>(() => Observe(), "3858末层scene提交后丢返回，在线Run仍保留原准备");
                var progress = Observe()!;
                var clear = progress.Commit?.Plan.SceneClear?.Result.ClearFact;
                var eventIds = ((string)f.Query("SELECT group_concat(event_id) FROM character_dungeon_growth_receipts")!)
                    .Split(',').Select(Guid.Parse).ToHashSet();
                That(progress.Commit is { ProjectionCurrent: true } && progress.Commit.Duplicate == lost &&
                    clear is { ClearMap.Id: 70487, PassiveSource.TemplateId: 69292, PassiveSource.SourceOrdinal: 5 } &&
                    clear.ConfirmedDeathEvents.Count == deathCount && eventIds.SetEquals(clear.ConfirmedDeathEvents) &&
                    !eventIds.Contains(clear.PassiveSource!.OperationId) && f.Session.Phase == SessionPhase.Settlement &&
                    f.Session.DungeonSnapshot.Identity.Epoch == identity.Epoch + 1 &&
                    f.Query("SELECT count(*) FROM character_dungeon_scene_clear_receipts") is 1L,
                    "原scene提交或丢返回恢复后一次结算，完整死亡集合等于真实growth且不混物件ID");
            }
            var saved = f.Read();
            That(Observe() is null && QuestCompletionRepositoryTests.Same(saved, f.Read()),
                "重复末层物件不重复scene、epoch、任务或资产");
            var enabled = f.Session.EnableFormalDungeonSettlement();
            That(enabled is { BossKey: null }, "末层物件沿原空31许可结算，不伪造基础Boss的115确认");
            f.Session.ConfirmFormalDungeonSettlementEnabled(enabled!.Value.Receipt);
            byte[] report = DungeonSettlementProtocolTests.Report(saved.Id, 99);
            That(DungeonSettlementRequests.TryParsePlayResult(report, out var result), "真实3858沿既有正式46报告形状");
            if (lost) Reject<IOException>(() => Settle(), "3858正式46已提交后丢返回");
            var settled = Settle();
            That(settled.Outcome == DungeonRunOutcome.Accepted && settled.Receipt is not null &&
                Settle().Outcome == DungeonRunOutcome.Duplicate && f.Query("SELECT count(*) FROM character_dungeon_settlement_receipts") is 1L,
                "末层独立来源通过46保存并复用原receipt，不重复通关奖励");
            saved = f.Read();
            if (cinematic)
            {
                if (mode == "cinematic-last") CompleteCinematic();
                var finished = Finish();
                That(finished.Receipt?.Commit is { ConditionType: 65, Clear: null } complete &&
                    complete.AcceptanceId == active.AcceptanceId && f.Read().CompletedQuests.Contains(3858) &&
                    f.Read().ActiveMainQuests.All(row => row.QuestId != 3858),
                    "3858演出33及独立物件39后按原34交付type65，不借清图凭据替代演出");
                saved = f.Read();
                That(Finish().Outcome == QuestFinishOutcome.AlreadyCompleted && Observe() is null &&
                    Settle().Outcome == DungeonRunOutcome.Duplicate && QuestCompletionRepositoryTests.Same(saved, f.Read()),
                    "3858成功交付后34、39及46重放不重复任何奖励或凭据");
                That(f.Session.ReturnFromFormalDungeon() is not null, "3858真实末层结算和交付后正常回城");
                f.Session.ReturnToRoster(); f.Session.Select(0, resumeTown: true); f.Session.MarkReady();
                That(f.Read().CompletedQuests.Contains(3858) && f.Read().ActiveMainQuests.All(row => row.QuestId != 3858) &&
                    f.Query("SELECT count(*) FROM character_quest_completion_receipts WHERE quest_id=3858") is 1L &&
                    f.Query("SELECT count(*) FROM character_dungeon_scene_clear_receipts") is 1L &&
                    f.Query("SELECT count(*) FROM character_dungeon_settlement_receipts") is 1L,
                    "3858重选恢复实际完成位及三种独立唯一回执");
                continue;
            }
            var finish = Finish();
            That(finish.Outcome == QuestFinishOutcome.Rejected && finish.Receipt is null &&
                saved.ActiveMainQuests.Single(row => row.QuestId == 3858).ConditionState == 1 && !saved.CompletedQuests.Contains(3858) &&
                QuestCompletionRepositoryTests.Same(saved, f.Read()), "物件清图不归零type65，真实3858的34明确拒绝且不发奖");
            f.Session.ReturnToRoster(); f.Session.Select(0, resumeTown: true); f.Session.MarkReady();
            var restored = f.Read().ActiveMainQuests.Single(row => row.QuestId == 3858);
            That(restored.ConditionState == 1 && restored.AcceptanceId == active.AcceptanceId &&
                !f.Read().CompletedQuests.Contains(3858) && f.Session.ObserveFormalPassiveDeath(69292, true, fingerprint, rooms) is null,
                "重选恢复未达成3858及原接取，迟到末层物件不能复活实例或完成任务");
            QuestLifecycleReceipt? Observe() => f.Session.ObserveFormalPassiveDeath(69292, true, fingerprint, rooms);
            QuestFinishResult Finish() => f.Session.FinishQuest(3858, ushort.MaxValue, quests, parameters,
                items.Items, items.GoldLimits, items.EpicPieces, growth);
            QuestLifecycleReceipt CompleteCinematic(bool expectSceneClear = false)
            {
                var original = f.Read();
                var originalIdentity = f.Session.DungeonSnapshot!.Identity;
                var condition = f.Session.ChangeQuest(3858, QuestLifecycleAction.Condition, quests,
                    items.Items, items.GoldLimits, items.EpicPieces, new(null, 0, 0));
                var current = f.Read();
                var expectedIdentity = expectSceneClear
                    ? originalIdentity with { Epoch = originalIdentity.Epoch + 1 }
                    : originalIdentity;
                That(condition?.Outcome == QuestLifecycleOutcome.Committed && condition.DungeonIdentity == expectedIdentity &&
                    condition.Commit?.Plan.AcceptanceId == active.AcceptanceId &&
                    current.ActiveMainQuests.Single(row => row.QuestId == 3858).ConditionState == 0 &&
                    f.Session.DungeonSnapshot.Identity == expectedIdentity &&
                    QuestCompletionRepositoryTests.Same(original with { Revision = current.Revision, ActiveMainQuests = current.ActiveMainQuests }, current),
                    "3858终末33保存原接取scalar0，已加载末层同时推进物件清图epoch，其余阶段保留原epoch");
                That(f.Session.ChangeQuest(3858, QuestLifecycleAction.Condition, quests,
                        items.Items, items.GoldLimits, items.EpicPieces, new(null, 0, 0))?.Outcome == QuestLifecycleOutcome.Unchanged &&
                    QuestCompletionRepositoryTests.Same(current, f.Read()), "3858重复演出33不再次写入或领奖");
                return condition!;
            }
            FormalDungeonSettlementResult Settle() => f.Session.SettleFormalDungeon(result!.ActorId, result.RankPoint, result.MaximumRankPoint,
                report, dungeons, clears, growth);
        }
    }

    static void VerifyForgottenForest(TownCatalog towns, CharacterCatalog characters, ItemResources items, QuestCatalog quests,
        SkillLearningCatalog skills, CharacterGrowthCatalog growth, DungeonCatalog dungeons, DungeonRoomCatalog rooms,
        DungeonKillExperienceCatalog kills, DungeonClearExperienceCatalog clears, QuestRewardParameterCatalog parameters)
    {
        var quest = quests.Get(3449);
        That(quest.Condition == "[clear map]" && quest.SubType == -1 && quest.IntData.SequenceEqual(new[] { 70192 }) &&
            quest.DungeonIndex == 7157, "真实3449保持单地图清图目标70192，不改写为猎杀或演出");
        var area = towns.Areas.Values.First(row => row.Level <= 54 && row.DungeonWorldMap is { } world &&
            towns.DungeonMaps[world].Options.Any(option => option.DungeonId == 7157));
        var done = new HashSet<int>(area.Quests) { 3448 };
        if (area.AnyQuests.Length > 0) done.Add(area.AnyQuests[0]);
        var bounds = area.Map(done).Bounds.First(row => row.Conditions.Length == 0 && row.Quests.All(done.Contains)).Bounds;
        var position = new Location(area.Town, area.Area, checked((ushort)(bounds.X + bounds.Width / 2)), checked((ushort)(bounds.Y + bounds.Height / 2)));
        foreach (bool ordinary in new[] { false, true })
        {
            using var f = new QuestSceneTests.Fixture(towns, characters, skills, growth, quests, false, 54, done.ToArray(),
                position: position);
            var accepted = f.Session.ChangeQuest(3449, QuestLifecycleAction.Accept, quests, items.Items, items.GoldLimits, items.EpicPieces);
            That(accepted?.Commit is not null, "实际3449按Lv54及前置3448接取：" + accepted?.Reason);
            var active = f.Read().ActiveMainQuests.Single(row => row.QuestId == 3449);
            That(active.ConditionState == 1 && active.AcceptanceId != Guid.Empty, "3449沿原接取绑定AcceptanceId并保留未达成scalar1");
            var opening = f.Session.EnterFormalDungeon(7157, ordinary ? 0u : 3449u, 0, dungeons, rooms)!;
            That(opening is { Entry.BossMap.Id: 70192, Entry.BaseBossMap.Id: 70192, Entry.PassiveClearTerminalMap.Id: 70350 } &&
                opening.Entry.HasExplicitClearMap && opening.Entry.QuestClearMap is null &&
                opening.Entry.RouteQuestId == 3449 && opening.Entry.QuestId == (ordinary ? 0u : 3449u) &&
                opening.Entry.Maze.ClearConditions is [{ Type: "destroy object", TargetId: 69292, Count: 1 }] &&
                opening.Entry.QuestAcceptances.Single(row => row.QuestId == 3449).AcceptanceId == active.AcceptanceId,
                "真实D7157冻结70350物件末层，声明仍是基础70192；普通0保持请求身份");
            var path = QuestStoryRouteTests.Path(opening!.Entry.Maze.StaticTopology, opening.Entry.StartMap.Position, opening.Entry.BossMap.Position);
            FormalDungeonReceipt receipt = opening;
            int deathCount = 0;
            foreach (var point in path)
            {
                receipt = f.Session.CompleteFormalDungeonLoading()!;
                That(f.Session.ObserveFormalPassiveDeath(69292, true, new string('A', 64), rooms) is null &&
                    f.Session.EnableFormalDungeonSettlement() is null, "基础路线加载及未到末层的69292报告不能提前结算");
                foreach (var monster in receipt.Monsters.Monsters.Where(row => row.IsBlocking).ToArray())
                {
                    var death = f.Session.ObserveFormalDungeonDeath(FormalDungeonSessionTests.Observation(receipt, monster), kills, growth);
                    That(death.Outcome == DungeonRunOutcome.Accepted && death.ClearFact is null,
                        "真实基础路线阻挡逐个提交成长，无品级Boss死亡仍等待独立末层物件");
                    deathCount++;
                }
                if (point == opening.Entry.BossMap.Position) break;
                var next = path[Array.IndexOf(path, point) + 1];
                That(f.Session.MoveFormalDungeon(checked((byte)next.X), checked((byte)next.Y)) is not null,
                    "3449沿真实拓扑连接进入基础Boss房");
            }
            That(receipt.Snapshot.CurrentMap.Id == 70192 &&
                receipt.Monsters.Monsters.Count(row => row.MonsterId is 61251 or 61254 or 61253 or 61252 && row.IsBlocking && !row.IsBoss) == 4 &&
                f.Session.Phase == SessionPhase.Dungeon && f.Session.EnableFormalDungeonSettlement() is null &&
                f.Read().ActiveMainQuests.Single(row => row.QuestId == 3449).ConditionState == 1,
                "实际70192四只普通精灵已提交仍不能代替末层物件清图");
            foreach (var layer in opening.Entry.Maze.LayeredMaps.Where(map => map.Position == opening.Entry.BossMap.Position))
            {
                That(f.Session.MoveFormalDungeon(checked((byte)layer.Position.X), checked((byte)layer.Position.Y), 1) is not null,
                    "真实70192同坐标沿批准的叠层顺序进入物件层");
                receipt = f.Session.CompleteFormalDungeonLoading()!;
                That(receipt.Snapshot.CurrentMap == layer && receipt.Entry.BossMap.Id == 70192,
                    "层加载保留实际地图身份和原Boss投影");
            }
            var before = f.Read();
            var identity = f.Session.DungeonSnapshot!.Identity;
            That(receipt.Snapshot.CurrentMap.Id == 70350 && receipt.Monsters.Room.PassiveObjects.Count == 46 &&
                receipt.Monsters.Room.PassiveObjects[34].ObjectId == 69292 &&
                !rooms.ReportsQuestDeath(receipt.Monsters.Room.PassiveObjects[34]) &&
                f.Session.Phase == SessionPhase.Dungeon && before.ActiveMainQuests.Single(row => row.QuestId == 3449).ConditionState == 1,
                "真实末层唯一迷宫目标为普通物件row34，加载不伪造69292死亡或提前归零");
            byte[] body = QuestPassiveClearTcpTests.PassiveDeath(69292, ordinary ? 52 : 74);
            That(DungeonRequests.TryParsePassiveDeath(body, out var parsed) && parsed!.ChecksumMatches,
                "显式与普通入口分别以合成74B/52B物件正文通过原Parser，不代表实机实发");
            string fingerprint = Convert.ToHexString(SHA256.HashData(body));
            That(f.Session.ObserveFormalPassiveDeath(69292, false, fingerprint, rooms) is null &&
                f.Session.ObserveFormalPassiveDeath(69294, true, fingerprint, rooms) is null &&
                QuestCompletionRepositoryTests.Same(before, f.Read()), "末层错误摘要和邻接row35演员不能借终点资格推进状态");
            var progress = Observe()!;
            var clear = progress.Commit?.Plan.SceneClear?.Result.ClearFact;
            That(progress.Commit is { ProjectionCurrent: true, Duplicate: false } &&
                clear is { ClearMap.Id: 70350, PassiveSource.TemplateId: 69292, PassiveSource.SourceOrdinal: 34 } &&
                clear.ConfirmedDeathEvents.Count == deathCount &&
                f.Session.Phase == SessionPhase.Settlement &&
                f.Session.DungeonSnapshot.Identity.Epoch == identity.Epoch + 1 &&
                f.Read().ActiveMainQuests.Single(row => row.QuestId == 3449).ConditionState == 0 &&
                f.Repository.LoadQuestClear(f.Owner, f.Read(), 3449) is { SourceType: QuestClearSourceType.PassiveObject, MapId: 70350 },
                "3449物件末层一次结算并把声明70192归零，凭据记物理70350");
            var saved = f.Read();
            That(Observe() is null && QuestCompletionRepositoryTests.Same(saved, f.Read()),
                "重复末层物件不重复scene、epoch、任务或资产");
            var enabled = f.Session.EnableFormalDungeonSettlement();
            That(enabled is { BossKey: null }, "末层物件沿原空31许可结算，不伪造基础Boss的115确认");
            f.Session.ConfirmFormalDungeonSettlementEnabled(enabled!.Value.Receipt);
            byte[] report = DungeonSettlementProtocolTests.Report(saved.Id, 99);
            That(DungeonSettlementRequests.TryParsePlayResult(report, out var result), "真实3449沿既有正式46报告形状");
            var settled = f.Session.SettleFormalDungeon(result!.ActorId, result.RankPoint, result.MaximumRankPoint, report, dungeons, clears, growth);
            That(settled.Outcome == DungeonRunOutcome.Accepted && settled.Receipt is not null &&
                f.Session.SettleFormalDungeon(result.ActorId, result.RankPoint, result.MaximumRankPoint, report, dungeons, clears, growth).Outcome ==
                    DungeonRunOutcome.Duplicate,
                "末层独立来源通过46保存并复用原receipt");
            var finished = f.Session.FinishQuest(3449, ushort.MaxValue, quests, parameters, items.Items, items.GoldLimits, items.EpicPieces, growth);
            That(finished.Receipt?.Commit is { ConditionType: 3, Clear.SourceType: QuestClearSourceType.PassiveObject, Clear.MapId: 70350 } complete &&
                complete.AcceptanceId == active.AcceptanceId && f.Read().CompletedQuests.Contains(3449) &&
                f.Read().ActiveMainQuests.All(row => row.QuestId != 3449),
                "3449按清图type3及物理70350物件凭据交付，不要求凭据地图号等于声明70192");
            f.Session.ReturnFromFormalDungeon();
            f.Session.ReturnToRoster(); f.Session.Select(0, resumeTown: true); f.Session.MarkReady();
            That(f.Read().CompletedQuests.Contains(3449) && f.Read().ActiveMainQuests.All(row => row.QuestId != 3449) &&
                f.Query("SELECT count(*) FROM character_quest_completion_receipts WHERE quest_id=3449") is 1L &&
                f.Query("SELECT count(*) FROM character_dungeon_scene_clear_receipts") is 1L,
                "3449重选恢复实际完成位及物件清图回执");
            QuestLifecycleReceipt? Observe() => f.Session.ObserveFormalPassiveDeath(69292, true, fingerprint, rooms);
        }
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
