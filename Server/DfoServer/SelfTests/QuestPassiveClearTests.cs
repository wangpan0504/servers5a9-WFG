using System.Buffers.Binary;
using System.Security.Cryptography;
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
    // 复用本轮已装配目录；实际3842资源与合成死亡/52B物件报告分开于Windows验收。
    internal static void Run(TownCatalog towns, CharacterCatalog characters, ItemResources items, QuestCatalog quests,
        SkillLearningCatalog skills, CharacterGrowthCatalog growth, DungeonCatalog dungeons, DungeonRoomCatalog rooms,
        DungeonKillExperienceCatalog kills, DungeonClearExperienceCatalog clears, QuestRewardParameterCatalog parameters)
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
            That(DungeonRequests.TryParsePassiveDeath(body,out var parsed) && parsed!.ChecksumMatches,"原生52B物件报告通过完整摘要解析");
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
        Console.WriteLine($"PASS {Count-checks} 实际3842物件终末、39摘要、场景事务及31/46/34断言（非实机）");
    }
}
