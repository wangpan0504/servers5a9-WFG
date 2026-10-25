using System.Buffers.Binary;
using System.Security.Cryptography;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Quests;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Testing;
using Microsoft.Data.Sqlite;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class QuestLegacyAwakeningTests
{
    public static void Run(TownCatalog towns,PvfArchive pvf,CharacterCatalog characters,ItemResources items,
        QuestCatalog quests,SkillLearningCatalog skills,CharacterGrowthCatalog growth,QuestRewardParameterCatalog parameters)
    {
        int checks=Count;
        var dungeons=SelfTestCatalogs.Dungeons(pvf);var rooms=SelfTestCatalogs.Rooms(pvf);var kills=SelfTestCatalogs.KillExperience(pvf);
        That(quests.All.Where(QuestProfessionReward.LegacySelfAwakening).Select(q=>q.Id).Order().SequenceEqual([3423,3424]),
            "真实注册表只有两条外传Lv50剧情采用无job change的单步觉醒形状");
        foreach(var (id,job) in new (ushort,byte)[]{(3423,9),(3424,10)})
        {
            var quest=quests.Get(id);
            That(QuestProfessionReward.SupportedDeclaration(quest),"实际剧情职业奖励被共享合同识别");
            var done=quest.Definition!.PrerequisiteGroups.Single().ToHashSet();
            uint dungeon=checked((uint)quest.Definition.Dungeons.Single().DungeonId);
            var area=towns.Areas.Values.First(row=>row.Level<=50 && row.DungeonWorldMap is {} world &&
                towns.DungeonMaps[world].Options.Any(option=>option.DungeonId==dungeon));
            done.UnionWith(area.Quests);
            if(area.AnyQuests.Length!=0 && !area.AnyQuests.Any(done.Contains))done.Add(area.AnyQuests[0]);
            done.UnionWith(area.Map(done).DungeonGates.First(gate=>gate.Conditions.Length==0).Quests);
            var bounds=area.Map(done).Bounds.First(row=>row.Conditions.Length==0 && row.Quests.All(done.Contains)).Bounds;
            var position=new Location(area.Town,area.Area,checked((ushort)(bounds.X+bounds.Width/2)),checked((ushort)(bounds.Y+bounds.Height/2)));
            That(!done.Contains(id) && !done.Overlaps(quest.Definition.Collisions),"入口夹具不能预填目标或互斥任务完成");
            using var f=new QuestSceneTests.Fixture(towns,characters,skills,growth,quests,false,50,done.ToArray(),job:job,position:position);
            var before=f.Session.Selected!;
            That(QuestEligibility.Evaluate(before,quest,quests).Eligible &&
                !QuestEligibility.Evaluate(before with {Level=49},quest,quests).Eligible &&
                !QuestEligibility.Evaluate(before with {Job=(byte)(job==9?10:9)},quest,quests).Eligible &&
                !QuestEligibility.Evaluate(before with {GrowType=16},quest,quests).Eligible &&
                !QuestEligibility.Evaluate(before with {CompletedQuests=[]},quest,quests).Eligible,
                "保留等级、精确职业、未觉醒及真实前置，不因缺少标记放开所有角色");
            That(QuestProfessionReward.Target(before,quest,skills)==16,"外传零分支只升第一觉醒阶段");
            Reject<NotSupportedException>(()=>QuestProfessionReward.Target(before with {GrowType=16},quest,skills),"不得重复觉醒");
            foreach(var invalid in new[]{quest with {Grade="[side]"},quest with {MinimumLevel=1},
                quest with {Rewards=[new(0,2,null)]},quest with {Definition=quest.Definition with {TargetCharacters=[]}}})
                That(!QuestProfessionReward.LegacySelfAwakening(invalid),"相邻但未证明的职业奖励形状仍拒绝");
            That(f.Session.ChangeQuest(id,QuestLifecycleAction.Accept,quests,items.Items,items.GoldLimits,items.EpicPieces)?.Commit is not null,
                "实际任务经普通接取事务建立原接取身份");
            That(f.Session.FinishQuest(id,ushort.MaxValue,quests,parameters,items.Items,items.GoldLimits,items.EpicPieces,growth).Outcome==QuestFinishOutcome.Rejected,
                "缺少原地图清图凭据不能提前觉醒");
            var opening=f.Session.EnterFormalDungeon(dungeon,id,0,dungeons,rooms);
            That(opening is not null && opening.Entry.BossMap.Id==quest.IntData.Single(),"实际旧剧情只进入原目标地图");
            var entry=opening!.Entry;
            var path=QuestStoryRouteTests.Path(entry.Maze.StaticTopology,entry.StartMap.Position,entry.BossMap.Position);
            foreach(var point in path)
            {
                var loaded=f.Session.CompleteFormalDungeonLoading()!;
                foreach(var actor in loaded.Monsters.Monsters.Where(row=>row.CanReportDeath && !row.IsFriendly).OrderBy(row=>row.IsBoss))
                {
                    if (actor.IsScriptedBoss)
                    {
                        // 53546的真实dummy/boss不走有参与者击杀，按匹配客户端剧情正文解析。
                        byte[] body=new byte[74]; BinaryPrimitives.WriteUInt32LittleEndian(body,actor.ObjectKey);
                        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(4),ushort.MaxValue);
                        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(42),0x11);
                        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(70),ClientBodyChecksum.Compute(body.AsSpan(0,70)));
                        That(DungeonRequests.TryParseScriptedDeath(body,out var parsed) && parsed!.ChecksumMatches,
                            "原地图演出Boss死亡采用已确认的无参与者正文和校验");
                        var observation=new NativeDungeonScriptedDeathObservation(actor.ObjectKey,parsed!.RewriteFlag,true,Convert.ToHexString(SHA256.HashData(body)));
                        That(f.Session.ObserveFormalDungeonScriptedDeath(observation with {ChecksumMatches=false},kills,growth).Outcome==DungeonRunOutcome.Rejected &&
                            f.Session.ObserveFormalDungeonScriptedDeath(observation,kills,growth).Outcome==DungeonRunOutcome.Accepted &&
                            f.Session.ObserveFormalDungeonScriptedDeath(observation,kills,growth).Outcome==DungeonRunOutcome.Duplicate,
                            "剧情Boss必须匹配实例和校验，死亡事实恰好提交一次");
                    }
                    else That(f.Session.ObserveFormalDungeonDeath(FormalDungeonSessionTests.Observation(loaded,actor),kills,growth).Outcome==DungeonRunOutcome.Accepted,
                        "原路线阻塞敌方用合成战斗事实提交，不伪造清图凭据");
                }
                if(point==entry.BossMap.Position)break;
                var next=path[Array.IndexOf(path,point)+1];
                That(f.Session.MoveFormalDungeon(checked((byte)next.X),checked((byte)next.Y)) is not null,"按真实门墙逐房通行");
            }
            That(f.Session.ReadClearedQuest(id)?.Clear.MapId==quest.IntData.Single(),$"只使用当前接取与目标地图清图凭据；任务{id}，Boss {entry.BossMap}，阶段{f.Session.Phase}，条件{f.Read().ActiveMainQuests.Single(row=>row.QuestId==id).ConditionState}");
            var beforeAward=f.Read();
            f.Sql("CREATE TRIGGER legacy_awaken_abort AFTER INSERT ON character_quest_completion_receipts BEGIN SELECT RAISE(ABORT,'legacy awakening rollback'); END");
            Reject<SqliteException>(()=>Finish(),"觉醒回执末段失败回滚");
            That(QuestCompletionRepositoryTests.Same(beforeAward,f.Read()),"失败后职业、两树、资产与任务全快照保持");
            f.Sql("DROP TRIGGER legacy_awaken_abort");
            var completed=Finish();
            That(completed.Outcome==QuestFinishOutcome.Committed && completed.Receipt?.Commit.ProfessionGrowType==16 &&
                f.Read().GrowType==16 && f.Read().CompletedQuests.Contains(id),"原剧情清图交付真实觉醒并保存完成位");
            var ack=QuestPackets.Finish(id,3,0,[],0,[],growType:16);
            That(ack.AsSpan(8).SequenceEqual(new byte[]{2,1,0,0}),"外传觉醒沿已确认的阶段和双技能树回包");
            var saved=f.Read();
            _=Finish();That(QuestCompletionRepositoryTests.Same(saved,f.Read()),"重复交付不再觉醒或发奖");
            QuestFinishResult Finish()=>f.Session.FinishQuest(id,ushort.MaxValue,quests,parameters,items.Items,items.GoldLimits,items.EpicPieces,growth);
        }
        Console.WriteLine($"PASS {Count-checks} 旧外传Lv50主线觉醒资源与正式清图交付断言（合成战斗，非实机）");
    }
}
