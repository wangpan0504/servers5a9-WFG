using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Party;
using DfoServer.Game.Raids;
using DfoServer.Game.Session;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Raids;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using DfoServer.Testing;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

// 真实资源/SQLite领域用例：异常不关闭测试连接，独立证明首、中、末成员的原PendingDeath恢复。
// 网络入口另由LukeRaidRunTcpTests经Runtime/666验证，此处不把直接领域调用称为TCP。
internal static class LukeRaidRunTransactionTests
{
    internal static void Run(TcpTestResources resources,RaidDefinition definition,LukeRaidStartPolicy policy)
    {
        int before=Count;
        string root=Path.Combine(Path.GetTempPath(),"S5A9-luke-raid-run-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);string path=Path.Combine(root,"game.db");
        bool completed=false;
        var owners=new AccountOwners();List<AccountOwners.Lease> leases=[];List<GameSession> all=[];
        try
        {
            var repository=new CharacterRepository(path,resources.Quests);
            var raids=new RaidRegistry();var parties=new PartyService(repository);
            var items=resources.ItemResources;
            var start=new LukeRaidStart(raids,parties,repository,resources.Towns,items.EpicPieces,policy);
            var invalidRun=new LukeRaidStart(raids,parties,repository,resources.Towns,items.EpicPieces,policy with
                {Run=policy.Run! with {Mazes=policy.Run.Mazes.SetItem(0,policy.Run.Mazes[0] with {MazeOrdinal=int.MaxValue})}});
            That(invalidRun.UnavailableReason(definition)=="luke-run-maze-unregistered","无效Run策略在启动能力预验证拒绝，不先扣票");
            var withReward=policy.ExecutionScript with {Phases=policy.ExecutionScript.Phases.SetItem(0,policy.ExecutionScript.Phases[0] with
                {Rules=[new([], [new("[clear phase]",0,null,null,null,null,[])])]})};
            var unsupported=new LukeRaidStart(raids,parties,repository,resources.Towns,items.EpicPieces,policy with {ExecutionScript=withReward});
            That(unsupported.UnavailableReason(definition)=="luke-run-reward-coordination-unavailable",
                "Run首竖切拒自动奖励转换，领域pending单测不等于可配置线上能力");
            var run=new LukeRaidDungeonService(raids,parties,definition.Resources!.EntryCatalog!,policy.Run!);
            var service=new PartyDungeonService(parties,repository);
            var dungeons=SelfTestCatalogs.Dungeons(resources.Archive);var rooms=SelfTestCatalogs.Rooms(resources.Archive);
            var kills=SelfTestCatalogs.KillExperience(resources.Archive);
            int dungeon=definition.Resources.Script.Phases[0].Dungeons[0].Id;
            foreach(int failureIndex in new[]{0,1,3})
            {
                var sessions=Enumerable.Range(0,4).Select(index=>
                {
                    var owner=owners.Acquire($"raidrun{failureIndex}-{index}",()=>{});leases.Add(owner);
                    var origin=new Location(38,0,629,196);
                    var candidate=CharacterCreation.Prepare($"RaidRun{failureIndex}{index}",resources.Characters.Get(0),origin,origin,resources.Learning.Get);
                    var learning=SkillPointLedger.Synchronize(0,0,90,candidate.InitialLearning with
                        {Experience=resources.Growth.GetLevelStartExperience(90)},resources.Learning);
                    var character=repository.Create(owner,candidate with {Level=90,AcceptedStoryLevel=90,CompletedQuests=[2903,4872],InitialLearning=learning});
                    var inventory=new InventoryService(items.Items,items.GoldLimits,items.EpicPieces);
                    InventoryService.Commit(repository,owner,character,inventory.Grant(character,[new(definition.Resources.Script.EntryCost!.ItemId,1)]));
                    var session=new GameSession(repository,resources.Towns,owner,questCatalog:resources.Quests,entry:new(new(2,20),32,"[raid]"));
                    all.Add(session);session.Select(0);session.MarkReady();session.CurrentParty=()=>parties.Current(session);return session;
                }).ToArray();
                var actors=sessions.Select(session=>session.CaptureRaidActor(raids)!).ToArray();
                That(raids.Create(actors[0],1,"RunTx","RunTx"u8.ToArray(),8,1) is not null,"事务夹具沿原Registry创建Luke团");
                foreach(var actor in actors.Skip(1)){raids.Invite(actors[0],actor,0);That(raids.Respond(actor,actors[0].Character,true).Accepted,"事务夹具原邀请接受");}
                foreach(var actor in actors)raids.Assign(actors[0],actor.Character,1);
                var raid=raids.Find(actors[0])!;
                That(start.Start(actors[0],raid,definition,[],()=>true).Error==0,"领域自然Start同事务安装Party/Started/Battle，不手工Attach");
                var battle=sessions[0].CurrentRaidBattle(actors[0])!;
                That(run.Select(sessions[0],dungeon,parties.Current(sessions[0])).Length==4,"原服务准备四人选择上下文");
                var selectionParty=parties.Current(sessions[0])!;
                var contexts=sessions.Select(session=>session.CurrentDungeonSelection).ToArray();
                That(!run.CancelSelection(sessions[1],selectionParty)&&!run.CancelSelection(sessions[0],selectionParty with {})&&
                    sessions.Select((session,index)=>ReferenceEquals(session.CurrentDungeonSelection,contexts[index])).All(value=>value),
                    "非队长及非当前队伍引用不能撤销合法选择，整队上下文原样");
                That(run.CancelSelection(sessions[0],selectionParty)&&run.CancelSelection(sessions[0],selectionParty)&&
                    sessions.All(session=>session.CurrentDungeonSelection is null&&session.Phase==SessionPhase.Town)&&
                    run.Select(sessions[0],dungeon,parties.Current(sessions[0])).Length==4,"当前队长取消幂等且可重新准备整队");
                string Persistent()=>JsonSerializer.Serialize(actors.Select(actor=>repository.LoadCharacter(actor.Owner,0)).ToArray());
                string State()=>JsonSerializer.Serialize(sessions.Select(session=>session.Selected).ToArray());
                var initial=State();
                PartyDungeonNotice[] Enter()=>service.Enter(sessions[0],(uint)dungeon,2,dungeons,rooms,null,null,null,null);
                That(service.Enter(sessions[0],(uint)definition.Resources.Script.Phases[0].Dungeons[1].Id,2,dungeons,rooms,null,null,null,null).Length==0&&
                    sessions[0].LastFormalDungeonRejection=="luke-entry-authorization-changed"&&State()==initial&&
                    sessions.All(session=>session.PartyDungeon is null&&session.HasLukeRaidSelection),
                    "尚未Enter开图的隐藏DGN先按授权拒绝，零Run/零资产且选择保持");
                Sql($"CREATE TRIGGER fail_luke_run BEFORE UPDATE ON character_dungeon_state WHEN NEW.character_id={actors[failureIndex].Character} BEGIN SELECT RAISE(ABORT,'Luke entry rollback'); END");
                Reject<SqliteException>(()=>Enter(),"首/中/末成员入图SQL失败");
                That(State()==initial&&sessions.All(session=>session.PartyDungeon is null&&session.CurrentFormalDungeonScene() is null)&&!battle.HasPendingTransition,
                    "入图失败全员零Run/零资产，预留撤销且不丢阶段状态");
                Sql("DROP TRIGGER fail_luke_run");
                var entered=Enter();That(entered.Count(n=>n.Kind==PartyDungeonNoticeKind.Entry)==4&&
                    entered.Count(n=>n.Kind==PartyDungeonNoticeKind.RaidBattleSync&&n.RaidEvent==RaidStageFactKind.EnterDungeon)==1&&entered.Length==5,
                    "失败后四人成功进入，附一次已提交Enter开图全团广播，不遗留占图");
                var instance=sessions[0].PartyDungeon!;var party=parties.Current(sessions[0])!;
                var plan=entered[0].Scene!.Entry;
                var route=QuestStoryRouteTests.Path(plan.Maze.StaticTopology,plan.StartMap.Position,plan.BossMap.Position);
                bool injected=false;
                foreach(var point in route)
                {
                    foreach(var session in sessions.SkipLast(1))instance.CompleteLoading(session);
                    var loaded=instance.CompleteLoading(sessions[^1]);
                    That(loaded.Length==5&&loaded.Count(n=>n.Kind==PartyDungeonNoticeKind.Loaded)==4&&
                        loaded.Count(n=>n.Kind==PartyDungeonNoticeKind.RaidBattleSync&&n.RaidEvent==RaidStageFactKind.EnterMap)==1,
                        "真实四人加载屏障及一次权威房间事件同步");
                    var loadedView=battle.View;
                    That(sessions.All(session=>instance.CompleteLoading(session).Length==0)&&ReferenceEquals(battle.View,loadedView),
                        "已释放屏障的重复加载回调不再发房间事件或重复推进");
                    var scene=sessions[0].CurrentFormalDungeonScene()!;
                    var monsters=scene.Monsters.Monsters.Where(m=>m.IsBlocking&&m.CanReportDeath&&!m.IsFriendly).OrderBy(m=>m.IsBoss).ToArray();
                    for(int index=0;index<monsters.Length;index++)
                    {
                        var monster=monsters[index];var observation=FormalDungeonSessionTests.Observation(scene,monster);
                        PartyDungeonNotice[] Kill()=>instance.ObserveDeath(sessions[0],monster.ObjectKey,
                            session=>session.ObserveFormalDungeonDeath(observation,kills,resources.Growth));
                        if(point==route[^1]&&index==monsters.Length-1)
                        {
                            var view=battle.View;
                            Sql($"CREATE TRIGGER fail_luke_clear AFTER INSERT ON character_dungeon_growth_receipts WHEN NEW.character_id={actors[failureIndex].Character} BEGIN SELECT RAISE(ABORT,'Luke clear rollback'); END");
                            Reject<SqliteException>(()=>Kill(),"首/中/末成员终图死亡SQL失败");
                            That(ReferenceEquals(battle.View,view)&&actors.All(actor=>!battle.HasDungeonContribution(actor,instance.Id)),
                                "逐员SQL未全部提交前Battle零提前推进和参与");
                            Sql("DROP TRIGGER fail_luke_clear");
                            var resumed=Kill();
                            That(resumed.Count(n=>n.Kind==PartyDungeonNoticeKind.Death)==4&&resumed.Count(n=>n.Kind==PartyDungeonNoticeKind.RaidBattleSync)==1&&
                                battle.View.Revision==view.Revision+1&&actors.All(actor=>battle.HasDungeonContribution(actor,instance.Id)),
                                "原PendingDeath恢复全四人后一次地图推进、四份真实事实、一次锁外广播通知");
                            var committed=State();var finalView=battle.View;
                            That(Kill().All(n=>n.Kind==PartyDungeonNoticeKind.RaidBattleSync)&&State()==committed&&ReferenceEquals(battle.View,finalView),
                                "重复报告只允许当前view重同步，不重写角色或再次推进");
                            injected=true;
                        }
                        else Kill();
                    }
                    if(point==route[^1])break;
                    var next=route[Array.IndexOf(route,point)+1];
                    That(instance.Move(sessions[0],(byte)next.X,(byte)next.Y,0).Length==4,"全队沿实际资源通路过房");
                }
                That(injected,"实际末图至少一名可报告怪物触发清图失败回归");
                // 明确人工瞬态状态，只用于迫使原回城维护路径写库，不代表本版道具效果资源。
                That(sessions[0].PlanAndCommitInventory(character=>new(true,0,new(character.Inventory,
                    character.Inventory with {ConsumableStates=[new(ConsumableStateKind.Effect,1000,
                        DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds(),0,0)]},"selftest_luke_return_state")),requireTown:false).Accepted,
                    "原库存用例安装人工回城维护前提，非直接改角色或Run");
                var beforeReturn=sessions[0].Selected;
                string persistent=Persistent();
                Sql($"CREATE TRIGGER fail_luke_return BEFORE UPDATE ON characters WHEN NEW.id={actors[0].Character} BEGIN SELECT RAISE(ABORT,'Luke return rollback'); END");
                Reject<SqliteException>(()=>service.Depart(sessions[0],preserveSolo:true),"回城SQL失败");
                That(ReferenceEquals(sessions[0].PartyDungeon,instance)&&parties.IsCurrent(party)&&instance.Members.Length==4&&ReferenceEquals(sessions[0].Selected,beforeReturn)&&
                    Persistent()==persistent&&sessions[0].Selected!.Inventory.ConsumableStates.Length==1,
                    "回城失败保持原Run与固定普通分队，不先释放占用");
                Sql("DROP TRIGGER fail_luke_return");
                foreach(var session in sessions)That(service.Depart(session,preserveSolo:true).Error==0,"原回城事务释放每名Run成员");
                That(parties.IsCurrent(party)&&sessions.All(session=>session.PartyDungeon is null),"全部回Town保留同party ID与revision，Run成员全部释放");
                var leaving=new AntonRaidStart(raids,parties,repository,resources.Towns,items.EpicPieces);
                That(leaving.Leave(actors[3],actors[3].Character) is not null&&!parties.IsCurrent(party)&&
                    sessions[3].CurrentRaidBattle(actors[3]) is null,"真实离团更新分队快照并精确释放本人Battle绑定");
                var remaining=parties.Current(sessions[0])!;
                int nextDungeon=definition.Resources.Script.Phases[0].Dungeons[1].Id;
                That(run.Select(sessions[0],nextDungeon,remaining).Length==3,"剩余三人沿真实新队伍快照选择另一已开放图");
                var nextContexts=sessions.Take(3).Select(session=>session.CurrentDungeonSelection).ToArray();
                var beforeMismatch=State();
                That(Enter().Length==0&&sessions[0].LastFormalDungeonRejection=="luke-entry-selection-changed"&&
                    State()==beforeMismatch&&sessions.Take(3).All(session=>session.PartyDungeon is null)&&
                    sessions.Take(3).Select((session,index)=>ReferenceEquals(session.CurrentDungeonSelection,nextContexts[index])).All(value=>value),
                    "已开放但非当前所选DGN仍按选择身份拒绝，零Run/零资产且选择保持");
                That(!run.CancelSelection(sessions[0],party)&&sessions.Take(3).Select((session,index)=>
                    ReferenceEquals(session.CurrentDungeonSelection,nextContexts[index])).All(value=>value),
                    "真实离团前的旧快照不能清除新分队选择上下文");
                That(run.CancelSelection(sessions[0],remaining),"当前新队伍队长仍可正常取消");
            }
            completed=true;
        }
        finally
        {
            foreach(var session in all)session.End();foreach(var lease in leases)lease.Dispose();
            SqliteConnection.ClearAllPools();
            if(completed&&Directory.Exists(root))Directory.Delete(root,true);
            else Console.WriteLine("Luke Run事务失败现场保留："+root);
        }
        Console.WriteLine($"PASS {Count-before} 卢克Run真实资源、入场/清图/回城SQL故障恢复断言");
        void Sql(string sql){using var db=new SqliteConnection("Data Source="+path);db.Open();using var command=db.CreateCommand();command.CommandText=sql;command.ExecuteNonQuery();}
    }
}
