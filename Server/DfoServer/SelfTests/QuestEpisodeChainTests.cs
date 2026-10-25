using System.Buffers.Binary;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Quests;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Projection;
using DfoServer.Testing;
using Microsoft.Data.Sqlite;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class QuestEpisodeChainTests
{
    public static async Task RunAsync(TownCatalog towns,PvfArchive pvf,CharacterCatalog characters,ItemResources items)
    {
        int before=Count;
        var quests=SelfTestCatalogs.Quests(pvf); var skills=SelfTestCatalogs.SkillLearning(pvf);
        var growth=SelfTestCatalogs.Growth(pvf,skills); var parameters=SelfTestCatalogs.QuestRewardParameters(pvf);
        var dungeons=SelfTestCatalogs.Dungeons(pvf); var rooms=SelfTestCatalogs.Rooms(pvf); var kills=SelfTestCatalogs.KillExperience(pvf);
        int[] ids=Enumerable.Range(8660,12).ToArray();
        That(ids.All(id=>QuestEpisodeEntry.Supports(quests.Get(id)) && QuestCoverage.HasOrdinaryDeclarations(quests.Get(id))),
            "十二步原episode资源均具完整声明，不仅开放前四步");
        var changed=quests.Get(8666) with { IntData=[999999] };
        That(!QuestEpisodeEntry.Supports(changed) && !QuestEpisodeEntry.Supports(quests.Get(8672)),"未知目标与团队模式入口不套用剧情合同");
        var done=towns.AccessQuestIds.Append(6045).Where(id=>id is <8660 or >8672).Distinct().ToArray();
        Location AtNpc(int npcId,IReadOnlySet<int> completed)
        {
            foreach(var area in towns.Areas.Values)
            {
                var map=area.Map(completed); var npc=map.Npcs.FirstOrDefault(n=>n.Id==npcId);
                if(npc is null || area.Quests.Any(id=>!completed.Contains(id))) continue;
                foreach(var row in map.Bounds.Where(row=>row.Conditions.Length==0 && row.Quests.All(completed.Contains)))
                {
                    var r=row.Bounds;
                    var position=new Location(area.Town,area.Area,checked((ushort)Math.Clamp(npc.X,r.X,r.X+r.Width)),
                        checked((ushort)Math.Clamp(npc.Y,r.Y,r.Y+r.Height)));
                    if(towns.IsPosition(position,completed)) return position;
                }
            }
            throw new InvalidDataException("真实剧情NPC缺合法位置："+npcId);
        }
        using var f=new QuestSceneTests.Fixture(towns,characters,skills,growth,quests,false,level:94,completed:done,
            position:AtNpc(614,done.ToHashSet()));
        DfoServer.Game.Session.QuestLifecycleReceipt? Change(int id,QuestLifecycleAction action) => f.Session.ChangeQuest((ushort)id,action,
            quests,items.Items,items.GoldLimits,items.EpicPieces,action==QuestLifecycleAction.Condition ? new(null,0,0) : null);
        // 普通城镇交通不是本用例范围；仅定位测试存档，真实NPC场景、剧情镇36与地下城完整走正式入口。
        void Locate(Location position)
        {
            f.Session.ReturnToRoster(); var saved=f.Read();
            f.Repository.Save(f.Owner,saved,position,saved.Return); f.Session.Select(0,resumeTown:true); f.Session.MarkReady();
        }
        void Reselect() { f.Session.ReturnToRoster(); f.Session.Select(0,resumeTown:true); f.Session.MarkReady(); }
        for(int at=0;at<ids.Length;at++)
        {
            int id=ids[at]; var quest=quests.Get(id);
            if(at+1<ids.Length) That(Change(ids[at+1],QuestLifecycleAction.Accept)?.Commit is null,$"{id}前置未交付不能越步接取");
            Locate(AtNpc(quest.Npc,QuestEligibility.Completed(f.Read())));
            That(Change(id,QuestLifecycleAction.Accept)?.Commit is not null,$"{id}按原前置与等级接取");
            That(Change(id,QuestLifecycleAction.Giveup)?.Commit is null,$"{id}保留原cant giveup规则");
            if(id==8665)
            {
                var target=AtNpc(619,QuestEligibility.Completed(f.Read())); var source=f.Session.Selected!.Position;
                That(f.Session.Travel(new(target.Town,target.Area,target.X,target.Y,5,source.Town,source.Area,0,619)),
                    "由真实NPC621进入原剧情镇55，不使用普通跨图绕行");
                That(f.Session.ChatOrigin()!.Scene.RoomAccount==towns.Scene(target,QuestEligibility.Completed(f.Read())).OwnerKey("scene-quest",f.Read().Id),
                    "实际剧情会话聊天使用账号及角色隔离身份");
            }
            if(quest.Condition=="[clear map]")
            {
                Locate(AtNpc(620,QuestEligibility.Completed(f.Read())));
                That(Change(id,QuestLifecycleAction.Condition)?.Commit is null,$"{id}客户端33不能伪造清图");
                var entered=f.Session.EnterFormalDungeon(5346,(uint)id,0,dungeons,rooms);
                That(entered is not null,$"{id}进入原5346任务迷宫："+f.Session.LastFormalDungeonRejection);
                var route=QuestStoryRouteTests.Path(entered!.Entry.Maze.StaticTopology,entered.Entry.StartMap.Position,entered.Entry.BossMap.Position);
                for(int step=0;step<route.Length;step++)
                {
                    var loaded=f.Session.CompleteFormalDungeonLoading()!;
                    foreach(var monster in loaded.Monsters.Monsters.Where(row=>row.CanReportDeath).OrderBy(row=>row.IsBoss).ToArray())
                        That(f.Session.ObserveFormalDungeonDeath(FormalDungeonSessionTests.Observation(loaded,monster),kills,growth).Outcome==DungeonRunOutcome.Accepted,
                            $"{id}真实房间合成可信死亡经正式SQLite成长事务");
                    if(step+1<route.Length) That(f.Session.MoveFormalDungeon((byte)route[step+1].X,(byte)route[step+1].Y) is not null,"按原任务拓扑走下一房");
                }
                That(f.Session.ReadClearedQuest((ushort)id)?.Clear is { } clear && clear.MapId==quest.IntData[0],$"{id}仅原终点产生接取绑定清图凭据");
                That(f.Session.ReturnFromFormalDungeon() is not null,"剧情通关后正常回城");
                Reselect();
                That(f.Session.ReadClearedQuest((ushort)id)?.Clear is not null,"重登保持真实剧情清图凭据");
            }
            else if(quest.SubType!=1)
            {
                if(id!=8665) Locate(AtNpc(quest.IntData[0],QuestEligibility.Completed(f.Read())));
                That(Change(id,QuestLifecycleAction.Condition)?.Commit is not null,$"{id}在原NPC或范围位置推进真实目标");
            }
            QuestFinishResult Finish()=>f.Session.FinishQuest((ushort)id,ushort.MaxValue,quests,parameters,
                items.Items,items.GoldLimits,items.EpicPieces,growth);
            if(id==8668)
            {
                var snapshot=f.Read();
                f.Sql("CREATE TRIGGER episode_finish_abort AFTER INSERT ON character_quest_completion_receipts BEGIN SELECT RAISE(ABORT,'episode rollback'); END");
                Reject<SqliteException>(()=>Finish(),"剧情交付末段SQL失败");
                That(f.Read().Revision==snapshot.Revision && !f.Read().CompletedQuests.Contains(id),"剧情完成位、奖励与活动槽一并回滚");
                f.Sql("DROP TRIGGER episode_finish_abort");
            }
            That(Finish().Outcome==QuestFinishOutcome.Committed && f.Read().CompletedQuests.Contains(id),$"{id}交付原资源奖励并保存完成位");
            long revision=f.Read().Revision; Finish();
            That(f.Read().Revision==revision,$"{id}重复交付不重复奖励");
        }
        Reselect();
        That(ids.All(f.Read().CompletedQuests.Contains) && f.Read().ActiveMainQuests.All(row=>!ids.Contains(row.QuestId)),
            "十二步完整任务链重登后保持完成且无残留活动槽");
        var scene=towns.Scene(new(55,0,474,234),f.Read().CompletedQuests.ToHashSet());
        That(scene.IsEpisode && !scene.IsRoom && scene.OwnerKey("a",1)!=scene.OwnerKey("b",1) && scene.OwnerKey("a",1)!=scene.OwnerKey("a",2),
            "剧情城镇按账号和角色隔离，不改变赛丽亚房间物理语义");
        Locate(towns.InitialReturn);
        That(f.Session.ChatOrigin()!.Scene.RoomAccount is null && !towns.Scene(towns.InitialReturn,new HashSet<int>()).IsEpisode,
            "离开剧情镇恢复公共聊天域");
        await CheckPresence(towns,f.Read());
        Console.WriteLine($"PASS {Count-before} 十二步真实episode链、三段任务清图、回滚及剧情场景隔离断言（普通城镇交通由夹具定位）");
    }

    static async Task CheckPresence(TownCatalog towns,Character source)
    {
        var owners=new AccountOwners(); using var a=owners.Acquire("episode-a",()=>{}); using var b=owners.Acquire("episode-b",()=>{});
        using var stopA=new CancellationTokenSource(); using var stopB=new CancellationTokenSource();
        using var outA=new MemoryStream(); using var outB=new MemoryStream();
        await using var sendA=new GameConnectionSender(a,outA,stopA,0,(_,_,_,_,_)=>{});
        await using var sendB=new GameConnectionSender(b,outB,stopB,0,(_,_,_,_,_)=>{});
        var presence=new TownPresence(towns);
        var first=source with { Id=1,Position=new(55,0,474,234) }; var second=source with { Id=2,Position=first.Position };
        presence.Join(a,sendA,first); presence.Join(b,sendB,second);
        await sendB.Send(1,1347,[1],"episode_barrier"); int before=CountFrames(outB,119);
        presence.Publish(a,sendA,119,[1],"episode_private"); await sendB.Send(1,1347,[1],"episode_barrier");
        That(!presence.CanInteract(a,1,b,2) && CountFrames(outB,119)==before,"同坐标不同账号剧情玩家不能同屏交互或收到广播");
        presence.Remove(sendA);presence.Remove(sendB);
        first=first with {Position=towns.InitialReturn}; second=second with {Position=towns.InitialReturn};
        presence.Join(a,sendA,first);presence.Join(b,sendB,second);
        presence.Publish(a,sendA,119,[1],"episode_public"); await sendB.Send(1,1347,[1],"episode_barrier");
        That(presence.CanInteract(a,1,b,2) && CountFrames(outB,119)==before+1,"退出剧情回公共城镇后恢复同屏交互与广播");
        presence.Remove(sendA);presence.Remove(sendB);
    }
    static int CountFrames(MemoryStream stream,ushort opcode)
    {
        var raw=stream.ToArray(); int count=0;
        for(int at=0;at<raw.Length;at+=BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(at+3)))
            if(BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(at+1))==opcode) count++;
        return count;
    }
}
