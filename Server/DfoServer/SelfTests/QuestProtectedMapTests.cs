using DfoServer.Game.Dungeons;
using DfoServer.Game.Session;
using DfoServer.Game.Quests;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using PvfLib;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class QuestProtectedMapTests
{
    internal static void Run(TownCatalog towns,PvfArchive pvf,CharacterCatalog characters,ItemResources items,
        QuestCatalog quests,SkillLearningCatalog skills,CharacterGrowthCatalog growth)
    {
        int before=Count;
        var quest=quests.Get(2706);
        That(quest is { Condition:"[clear map]",SubType:0,IntData:[292101143,12008],DungeonIndex:5107 } &&
            QuestHiddenEntry.Direct(quest,quests),"真实朋友与敌人按保护APC地图条件进入隐藏候选");
        var entrance=MiddleQuestEntryTests.Entrance(towns,quest,5107);
        int[] done=entrance.Completed;
        var dungeons=DfoServer.Testing.SelfTestCatalogs.Dungeons(pvf);
        var rooms=DfoServer.Testing.SelfTestCatalogs.Rooms(pvf);
        var kills=DfoServer.Testing.SelfTestCatalogs.KillExperience(pvf);
        var parameters=DfoServer.Testing.SelfTestCatalogs.QuestRewardParameters(pvf);
        foreach (bool protect in new[] { false,true })
        {
            using var f=new QuestSceneTests.Fixture(towns,characters,skills,growth,quests,false,95,done,position:entrance.Position);
            That(QuestEligibility.Candidates(f.Read(),quests).Contains(2706) &&
                f.Session.ReadHiddenQuest(2706,quests) is { Open:true } &&
                f.Session.ChangeQuest(2706,QuestLifecycleAction.Accept,quests,items.Items,items.GoldLimits,items.EpicPieces)?.Commit is not null,
                "真实隐藏候选、884和31按正常资格接取，不补造完成");
            var opening=f.Session.EnterFormalDungeon(5107,2706,0,dungeons,rooms);
            That(opening is not null,"真实2706保护地图可生成正式路线并入场："+f.Session.LastFormalDungeonRejection);
            var target=opening!.Entry.QuestClearMap ?? opening.Entry.BossMap;
            That(target.Id==quest.IntData[0],"正式路线保留原保护地图");
            var path=QuestStoryRouteTests.Path(opening.Entry.Maze.StaticTopology,opening.Entry.StartMap.Position,target.Position);
            var loaded=f.Session.CompleteFormalDungeonLoading()!;
            var friend=loaded.Monsters.Monsters.Single(row=>row.MonsterId==12008);
            That(friend.AiCharacter is not null && friend.IsFriendly && !friend.IsBlocking,"起始图12008为跨房随行的被保护友方APC");
            for (int at=0;at<path.Length;at++)
            {
                if (at==path.Length-1) break;
                foreach (var enemy in loaded.Monsters.Monsters.Where(row=>row.IsBlocking))
                    That(f.Session.ObserveFormalDungeonDeath(FormalDungeonSessionTests.Observation(loaded,enemy),kills,growth).Outcome==DungeonRunOutcome.Accepted,
                        "沿原路线逐房清除真实敌人");
                That(f.Session.MoveFormalDungeon(checked((byte)path[at+1].X),checked((byte)path[at+1].Y)) is not null,"沿原拓扑抵达保护目标房");
                loaded=f.Session.CompleteFormalDungeonLoading()!;
            }
            if (loaded.Snapshot.CurrentMap!=target)
            {
                That(target.Kind=="layered" && f.Session.MoveFormalDungeon(checked((byte)target.Position.X),checked((byte)target.Position.Y),1) is not null,
                    "按原kind1进入保护目标层");
                loaded=f.Session.CompleteFormalDungeonLoading()!;
            }
            That(loaded.Snapshot.CurrentMap==target,"抵达原保护地图而非相同坐标基础层");
            That(loaded.Monsters.Monsters.All(row=>row.MonsterId!=12008),"保护对象沿起始房实际key随行，不在终点重复生成");
            if (!protect)
            {
                var beforeRetire=f.Read();
                var retired=f.Session.ObserveFormalDungeonDeath(FormalDungeonSessionTests.Observation(loaded,friend),kills,growth);
                That(retired.Outcome==DungeonRunOutcome.Accepted && retired.ReportedTarget==friend && retired.Receipt is not null && retired.Reward is null &&
                    QuestCompletionRepositoryTests.Same(beforeRetire,f.Read()),"跨房友方按本实例已下发key退场，不生成经验或资产");
            }
            var enemies=loaded.Monsters.Monsters.Where(row=>row.CanReportDeath && !row.IsFriendly).OrderBy(row=>row.IsBoss).ToArray();
            That(enemies.Length>0,"保护图存在真实阻挡敌人");
            foreach (var enemy in enemies[..^1])
                That(f.Session.ObserveFormalDungeonDeath(FormalDungeonSessionTests.Observation(loaded,enemy),kills,growth).Outcome==DungeonRunOutcome.Accepted,
                    "清除保护图前序敌人不提前完成任务");
            var observation=FormalDungeonSessionTests.Observation(loaded,enemies[^1]);
            var original=f.Read();
            if (protect)
            {
                f.Sql("CREATE TRIGGER protected_clear_abort AFTER INSERT ON character_quest_clear_receipts BEGIN SELECT RAISE(ABORT,'protected rollback'); END");
                Reject<SqliteException>(()=>f.Session.ObserveFormalDungeonDeath(observation,kills,growth),"保护条件与死亡经验同事务回滚");
                That(QuestCompletionRepositoryTests.Same(original,f.Read()) && f.Repository.LoadQuestClear(f.Owner,f.Read(),2706) is null,
                    "失败不保存任务scalar、资产或清图凭据");
                That(f.Session.ObserveFormalDungeonDeath(FormalDungeonSessionTests.Observation(loaded,friend),kills,growth).Outcome==DungeonRunOutcome.Rejected,
                    "末次死亡冻结后迟到的友方退场不能改变同一重试的保护条件");
                f.Sql("DROP TRIGGER protected_clear_abort");
            }
            That(f.Session.ObserveFormalDungeonDeath(observation,kills,growth).Outcome==DungeonRunOutcome.Accepted,"最后敌人按原死亡事务提交");
            var clear=f.Repository.LoadQuestClear(f.Owner,f.Read(),2706);
            That(protect ? clear is { MapId:292101143 } && f.Read().ActiveMainQuests.Single(row=>row.QuestId==2706).ConditionState==0 :
                clear is null && f.Read().ActiveMainQuests.Single(row=>row.QuestId==2706).ConditionState==1,
                "只有真实友方存活才保存本次接取的清图凭据");
            var committed=f.Read();
            That(f.Session.ObserveFormalDungeonDeath(observation,kills,growth).Outcome==DungeonRunOutcome.Duplicate &&
                QuestCompletionRepositoryTests.Same(committed,f.Read()),"最后死亡重报不重复推进或奖励");
            That(f.Session.ReturnFromFormalDungeon() is not null,"沿原返回流程退出保护副本");
            f.Session.ReturnToRoster(); f.Session.Select(0); f.Session.MarkReady();
            That(f.Repository.LoadQuestClear(f.Owner,f.Read(),2706)==clear,"重选角色恢复原持久清图凭据");
            var finished=f.Session.FinishQuest(2706,ushort.MaxValue,quests,parameters,items.Items,items.GoldLimits,items.EpicPieces,growth);
            That(protect ? finished.Outcome==QuestFinishOutcome.Committed : finished.Outcome==QuestFinishOutcome.Rejected,
                $"保护任务交付结果 protect={protect} outcome={finished.Outcome} reason={finished.Reason}");
            That(protect ? finished.Outcome==QuestFinishOutcome.Committed && f.Read().CompletedQuests.Contains(2706) &&
                QuestEligibility.Candidates(f.Read(),quests).Contains(2707) : finished.Outcome==QuestFinishOutcome.Rejected &&
                !f.Read().CompletedQuests.Contains(2706),"保护成功才交付并开启真实后继2707");
        }
        Console.WriteLine($"PASS {Count-before} 真实2706保护成功、失败、事务与后继链断言（非实机）");
    }
}
