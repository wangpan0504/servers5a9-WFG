using System.Text.Json;
using System.Collections.Immutable;
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

// 与真实Runtime网络组互补：原Start/PartyDungeon/死亡SQL产生候选，直接领域调用只为有界故障重试而不关闭连接。
internal static class LukeRaidLifecycleTransactionTests
{
    internal static void Run(TcpTestResources resources,RaidDefinition definition,LukeRaidStartPolicy policy)
    {
        int before=Count;string root=Path.Combine(Path.GetTempPath(),"S5A9-luke-lifecycle-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);string path=Path.Combine(root,"game.db");bool done=false;
        var owners=new AccountOwners();List<AccountOwners.Lease> leases=[];List<GameSession> sessions=[];
        try
        {
            var items=resources.ItemResources;var repository=new CharacterRepository(path,resources.Quests);
            var raids=new RaidRegistry();var parties=new PartyService(repository);
            policy=policy with {Completion=policy.Completion! with {WeeklyClearLimit=1}};
            var clock=new Clock(DateTimeOffset.Parse("2026-10-25T05:59:59+08:00"));
            var start=new LukeRaidStart(raids,parties,repository,resources.Towns,items.EpicPieces,policy,clock,rewardItems:items.Items);
            var selection=new LukeRaidDungeonService(raids,parties,definition.Resources!.EntryCatalog!,policy.Run!);
            var dungeonService=new PartyDungeonService(parties,repository);
            var dungeons=SelfTestCatalogs.Dungeons(resources.Archive);var rooms=SelfTestCatalogs.Rooms(resources.Archive);
            var kills=SelfTestCatalogs.KillExperience(resources.Archive);
            for(int index=0;index<8;index++)
            {
                var owner=owners.Acquire("lifecycle"+index,()=>{});leases.Add(owner);var origin=new Location(38,0,629,196);
                var candidate=CharacterCreation.Prepare("LkLife"+index,resources.Characters.Get(0),origin,origin,resources.Learning.Get);
                var learning=SkillPointLedger.Synchronize(0,0,90,candidate.InitialLearning with {Experience=resources.Growth.GetLevelStartExperience(90)},resources.Learning);
                var role=repository.Create(owner,candidate with {Level=90,AcceptedStoryLevel=90,CompletedQuests=[2903,4872],InitialLearning=learning});
                var inventory=new InventoryService(items.Items,items.GoldLimits,items.EpicPieces);
                InventoryService.Commit(repository,owner,role,inventory.Grant(role,[new(definition.Resources.Script.EntryCost!.ItemId,2)]));
                var session=new GameSession(repository,resources.Towns,owner,questCatalog:resources.Quests,entry:new(new(2,20),32,"[raid]"));
                sessions.Add(session);session.Select(0);session.MarkReady();session.CurrentParty=()=>parties.Current(session);
            }
            var actors=sessions.Select(session=>session.CaptureRaidActor(raids)!).ToArray();
            void Create()
            {
                That(raids.Create(actors[0],1,"Life","Life"u8.ToArray(),8,1) is not null,"原Registry创建阶段事务团");
                foreach(var actor in actors.Skip(1)){raids.Invite(actors[0],actor,0);That(raids.Respond(actor,actors[0].Character,true).Accepted,"原邀请接受");}
                for(int index=0;index<actors.Length;index++)raids.Assign(actors[0],actors[index].Character,(byte)(index/4+1));
            }
            Create();
            That(start.Start(actors[0],raids.Find(actors[0])!,definition,[],()=>true).Error==0,"原Start签发SQL身份/绑定Battle，不手工Attach");
            var battle=sessions[0].CurrentRaidBattle(actors[0])!;
            var members=sessions.Take(4).ToArray();
            RunStages(false);
            That(battle.View.State==AntonRaidBattleState.Completed,"两阶段原领取路径后完成");
            var leave=new AntonRaidStart(raids,parties,repository,resources.Towns,items.EpicPieces);
            foreach(var actor in actors.Reverse())That(leave.Leave(actor,actor.Character) is not null,"完成后真实离团");
            Create();string full=Snapshot();var references=sessions.Select(session=>session.Selected).ToArray();
            That(start.Start(actors[0],raids.Find(actors[0])!,definition,[],()=>true) is {Error:19,Reason:"luke-weekly-clear-limit"},
                "当前周期已满在同费用SQL内拒绝新666，不让角色白付票");
            That(Snapshot()==full&&sessions.Select((session,index)=>ReferenceEquals(session.Selected,references[index])&&session.StartedRaid is null).All(value=>value),
                "满额拒绝零新生命周期/费用/角色/Battle/队伍提交");
            var replenish=new InventoryService(items.Items,items.GoldLimits,items.EpicPieces);
            foreach(var session in sessions)That(session.PlanAndCommitInventory(character=>replenish.Grant(character,[new(definition.Resources.Script.EntryCost!.ItemId,1)])).Accepted,
                "竞争回归沿原库存用例补下一次真实票，不改PVF/库存核心");
            policy=policy with {Completion=policy.Completion! with {Source="selftest/explicit-writer-race",WeeklyClearLimit=2,
                Phases=policy.Completion.Phases.Select(phase=>phase with {WeeklyEligibilityLimit=1}).ToImmutableArray()}};
            start=new LukeRaidStart(raids,parties,repository,resources.Towns,items.EpicPieces,policy,clock,rewardItems:items.Items);
            That(start.Start(actors[0],raids.Find(actors[0])!,definition,[],()=>true).Error==0,"显式两次额度下开始第二个真实实例");
            battle=sessions[0].CurrentRaidBattle(actors[0])!;
            RunStages(true);
            RunStages(false,stageDenied:true);
            LukeRaidResourceRulesTests.Run(definition.Resources.Script,actors,policy.Completion!);
            done=true;

            void RunStages(bool supersede,bool stageDenied=false)
            {
            for(int phase=0;phase<2;phase++)
            {
                int dungeon=phase==0?5054:5311;
                int hp=definition.Resources.Script.Phases[phase].Dungeons.Single(row=>row.Id==dungeon).HpRaw;
                for(int turn=0;turn<hp;turn++)
                {
                    That(selection.Select(sessions[0],dungeon,parties.Current(sessions[0])).Length==4,"原选择服务准备正式Run");
                    var entered=dungeonService.Enter(sessions[0],(uint)dungeon,2,dungeons,rooms,null,null,null,null);
                    var run=sessions[0].PartyDungeon!;var entry=entered.First(notice=>notice.Scene is not null).Scene!.Entry;
                    var route=QuestStoryRouteTests.Path(entry.Maze.StaticTopology,entry.StartMap.Position,entry.BossMap.Position);
                    using var blocked=new BlockedEntryStore(repository);
                    Thread? entering=null;Exception? entryError=null;int published=0;
                    if(!supersede&&!stageDenied&&phase==0&&turn+1==hp)
                    {
                        That(selection.Select(sessions[4],5055,parties.Current(sessions[4])).Length==4,"另一分队真实选择已由5054 Enter开放的5055");
                        Sql($"CREATE TRIGGER fail_reserved_entry BEFORE UPDATE ON character_dungeon_state WHEN NEW.character_id={actors[7].Character} BEGIN SELECT RAISE(ABORT,'reserved entry rollback'); END");
                        foreach(var actor in actors)actor.Session.RaidBattleNoticeArrived=(view,recipient)=>
                        {
                            That(view.State==AntonRaidBattleState.Reward&&view.Instance==battle.Instance,"最后预留撤销只发布当前已提交Reward");
                            Interlocked.Increment(ref published);
                            if(recipient==actors[7])throw new InvalidOperationException("injected publication failure");
                        };
                        var delayed=new PartyDungeonService(parties,blocked);
                        entering=new Thread(()=>{try{delayed.Enter(sessions[4],5055,2,dungeons,rooms,null,null,null,null);}catch(Exception error){entryError=error;}});
                        entering.Start();That(blocked.Entered.Wait(TimeSpan.FromSeconds(10)),"第二分队已持有原Run预留，SQL前有界阻塞");
                    }
                    try
                    {
                    foreach(var point in route)
                    {
                        foreach(var session in members)run.CompleteLoading(session);
                        var scene=sessions[0].CurrentFormalDungeonScene()!;
                        foreach(var monster in scene.Monsters.Monsters.Where(monster=>monster.IsBlocking&&monster.CanReportDeath&&!monster.IsFriendly).OrderBy(monster=>monster.IsBoss))
                        {
                            var observation=FormalDungeonSessionTests.Observation(scene,monster);
                            run.ObserveDeath(sessions[0],monster.ObjectKey,session=>session.ObserveFormalDungeonDeath(observation,kills,resources.Growth));
                        }
                        if(point==route[^1])break;
                        var next=route[Array.IndexOf(route,point)+1];That(run.Move(sessions[0],(byte)next.X,(byte)next.Y,0).Length==4,"按真实资源连通路线换房");
                    }
                    if(turn+1==hp)
                    {
                        var candidate=battle.PendingLukeSettlement()!;string prefix=phase==0?"phase0":"phase1";
                        if(supersede&&phase==1)
                        {
                            foreach(var session in members)That(dungeonService.Depart(session,preserveSolo:true).Error==0,"旧P2候选尚未提交时合法退出正式Run");
                            var retiring=battle;
                            var exiting=new AntonRaidStart(raids,parties,repository,resources.Towns,items.EpicPieces);
                            foreach(var actor in actors.Reverse())That(exiting.Leave(actor,actor.Character) is not null,"新666前必须真正离开旧Started团");
                            Create();
                            That(start.Start(actors[0],raids.Find(actors[0])!,definition,[],()=>true).Error==0,"新666先获得writer并关闭旧active");
                            var next=sessions[0].CurrentRaidBattle(actors[0])!;var nextView=next.View;
                            var denied=repository.CommitLukePhase(candidate);retiring.AcceptLukeSettlement(candidate,denied);
                            That(denied.Members.All(row=>!row.CountsAsClear&&!row.Eligible&&row.Reason=="admission-abandoned")&&
                                ReferenceEquals(next.View,nextView)&&sessions.All(session=>session.StartedRaid?.InstanceId==next.Instance),
                                "旧候选随后提交只拒绝旧行，不计次/发奖/关闭或改变新实例");
                            That(JsonSerializer.Serialize(repository.CommitLukePhase(candidate))==JsonSerializer.Serialize(denied),"被替代旧终结拒绝也幂等，不随新团状态改写");
                            battle=next;return;
                        }

                        That(candidate.Phase==phase&&candidate.Members.Take(4).All(member=>member.CommittedRuns==hp)&&candidate.Members.Skip(4).All(member=>member.CommittedRuns==0),"正式死亡SQL产生精确每成员参与，不由测试伪造ClearFact");
                        string snapshot=Snapshot();
                        foreach(int failure in new[]{0,3,7})
                        {
                            Sql($"CREATE TRIGGER fail_phase BEFORE UPDATE OF {prefix}_receipt ON character_raid_runs WHEN NEW.character_id={actors[failure].Character} BEGIN SELECT RAISE(ABORT,'phase rollback'); END");
                            Reject<SqliteException>(()=>repository.CommitLukePhase(candidate),"首/中/末成员阶段SQL失败");
                            That(Snapshot()==snapshot&&ReferenceEquals(battle.PendingLukeSettlement(),candidate)&&battle.View.State==AntonRaidBattleState.Fighting,
                                "阶段事务失败零部分资格/通关，密封原候选保留且不开放Reward");
                            Sql("DROP TRIGGER fail_phase");
                        }
                        var committed=repository.CommitLukePhase(candidate);battle.AcceptLukeSettlement(candidate,committed);
                        That(JsonSerializer.Serialize(repository.CommitLukePhase(candidate))==JsonSerializer.Serialize(committed),"原候选SQL重放返回同anchor/完整奖励/资格，不重计");
                        That(committed.Members.All(row=>row.CountsAsClear==(phase==1)&&row.Eligible==!stageDenied),"P1资格与P2通关分别持久提交");
                        That(battle.View.State==AntonRaidBattleState.Fighting,"阶段SQL成功也不能跳过旧Run退出屏障");
                    }
                    foreach(var session in members)That(dungeonService.Depart(session,preserveSolo:true).Error==0,"正常回城正式释放原Run");
                    if(entering is not null)
                    {
                        That(battle.HasPendingTransition&&battle.View.State==AntonRaidBattleState.Fighting,"原Run全部退出，另一分队最后预留仍阻止Reward");
                        blocked.Release.Set();That(entering.Join(TimeSpan.FromSeconds(10)),"SQL拒绝后有界释放分队owner并锁外发布");
                        That(entryError is SqliteException&&entryError.Data["RaidPublicationFailure"] as string==nameof(InvalidOperationException)&&published==8,
                            "精确最后预留SQL失败仍广播全团；通知失败不覆盖原SQL错误");
                        That(battle.View.State==AntonRaidBattleState.Reward&&!battle.HasPendingTransition&&sessions.Skip(4).All(session=>session.PartyDungeon is null),
                            "失败分队零Run，已提交阶段向另一队收敛且不回滚事实");
                        Sql("DROP TRIGGER fail_reserved_entry");
                        foreach(var actor in actors)actor.Session.RaidBattleNoticeArrived=null;
                        That(selection.CancelSelection(sessions[4],parties.Current(sessions[4])),"失败后沿原选择撤销回候战，不残留选图");
                    }
                    }
                    finally
                    {
                        blocked.Release.Set();if(entering is not null&&!entering.Join(TimeSpan.FromSeconds(10)))throw new TimeoutException("阶段事务阻塞worker未退出");
                        foreach(var actor in actors)actor.Session.RaidBattleNoticeArrived=null;
                    }
                }
                if(stageDenied)
                {
                    That(battle.View is {Phase:1,State:AntonRaidBattleState.Fighting}&&actors.All(actor=>
                        battle.LukeEligibility(actor.Character,0) is {Eligible:false,CountsAsClear:false,Reason:"phase-eligibility-limit"}),
                        "同周阶段额度按已密封资格限制，不能因没有最终通关而无限刷P1；全拒不堵下一阶段");
                    return;
                }
                That(battle.View.State==AntonRaidBattleState.Reward,"SQL及全部退出均完成才进入Reward");
                if(phase==0&&!supersede)clock.Now=clock.Now.AddSeconds(2);
                var character=sessions[0].Selected!;var eligibility=battle.LukeEligibility(character.Id,phase)!;
                That(eligibility.Week==(!supersede&&phase==0?20261018:20261025),"阶段anchor跨显式合成周界，迟领P1仍取旧周而P2按新anchor周");
                var changed=new PreparedRaidReward(battle.Instance,phase,0,1,character.Id,eligibility.Source,[new(10400,2)],phase==1,eligibility.Week){RaidType=RaidKind.Luke};
                var altered=InventoryRewardGrantService.TryGrantBatch(character,changed.Grants,items.Items,items.GoldLimits,items.EpicPieces,
                    utcNow:DateTimeOffset.UtcNow,allowOverflowMail:true).Plan!;
                string protectedState=Snapshot();
                That(repository.CommitRaidReward(leases[0],character,changed,new(altered.Before,altered.After,"raid_reward",new(character),altered.OverflowItems),DateTimeOffset.UtcNow) is null&&
                    Snapshot()==protectedState,"即便Source相同，终结后改Grants/数量也不能借旧资格发新奖");
                Sql("CREATE TRIGGER fail_award BEFORE INSERT ON character_raid_reward_receipts BEGIN SELECT RAISE(ABORT,'award rollback'); END");
                Reject<SqliteException>(()=>sessions[0].ClaimRaidReward(1,1,repository,items.Items,items.GoldLimits,items.EpicPieces,DateTimeOffset.UtcNow),"真实Luke领奖资产/回执SQL失败");
                That(Snapshot()==protectedState&&ReferenceEquals(sessions[0].Selected,character),"领奖失败保完整阶段资格和原资产，重试沿原冻结计划");
                Sql("DROP TRIGGER fail_award");
                foreach(var session in sessions)foreach(byte kind in new byte[]{1,0})
                {
                    var receipt=session.ClaimRaidReward(kind,1,repository,items.Items,items.GoldLimits,items.EpicPieces,DateTimeOffset.UtcNow);
                    That(receipt is not null,"原Session/库存writer按冻结资格发奖，不直接插入奖励表");session.ConfirmRaidRewardPublished(receipt!);
                }
            }
            }

        }
        finally
        {
            foreach(var session in sessions)session.End();foreach(var lease in leases)lease.Dispose();SqliteConnection.ClearAllPools();
            if(done)Directory.Delete(root,true);else Console.WriteLine("Luke阶段事务失败现场保留："+root);
        }
        Console.WriteLine($"PASS {Count-before} 卢克阶段真实SQL故障、两阶段领取及满额开团断言");
        void Sql(string sql){using var db=new SqliteConnection("Data Source="+path);db.Open();using var command=db.CreateCommand();command.CommandText=sql;command.ExecuteNonQuery();}
        string Snapshot()
        {
            using var db=new SqliteConnection("Data Source="+path);db.Open();using var command=db.CreateCommand();
            List<string> result=[];
            foreach(string table in new[]{"character_raid_runs","character_raid_reward_receipts","characters","character_inventory_items","inventory_audit_log"})
            {
                command.CommandText="SELECT * FROM "+table;using var reader=command.ExecuteReader();List<object[]> rows=[];
                while(reader.Read()){object[] row=new object[reader.FieldCount];reader.GetValues(row);rows.Add(row);}result.Add(JsonSerializer.Serialize(rows));
            }
            return JsonSerializer.Serialize(result);
        }
    }
    sealed class Clock(DateTimeOffset now):TimeProvider
    {
        public DateTimeOffset Now {get;set;}=now;
        public override DateTimeOffset GetUtcNow()=>Now;
    }
    sealed class BlockedEntryStore(IPartyDungeonStore inner):IPartyDungeonStore,IDisposable
    {
        public ManualResetEventSlim Entered {get;}=new();
        public ManualResetEventSlim Release {get;}=new();
        public PartyDungeonEntryCommit[] CommitPartyDungeonEntry(IReadOnlyList<PartyDungeonReservation> rows)
        {
            Entered.Set();if(!Release.Wait(TimeSpan.FromSeconds(30)))throw new TimeoutException("等待阶段终结有界测试信号");
            return inner.CommitPartyDungeonEntry(rows);
        }
        public Character[] CommitPartyDungeonExit(IReadOnlyList<PartyDungeonExitReservation> rows)=>inner.CommitPartyDungeonExit(rows);
        public void Dispose(){Release.Set();Entered.Dispose();Release.Dispose();}
    }

}
