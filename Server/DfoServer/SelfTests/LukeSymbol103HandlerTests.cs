using System.Collections.Immutable;
using System.Net;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Party;
using DfoServer.Game.Raids;
using DfoServer.Game.Session;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Raids;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Handlers;
using DfoServer.Network.Projection;
using DfoServer.Network.Protocol;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

// 领域提交子路径及真实Handler隔离：测试GiveUp→103输入，不经过Runtime666可用性守卫，不是原P2全流程。
internal static class LukeSymbol103HandlerTests
{
    internal static async Task RunAsync(ServerOptions source,string root,TcpTestResources resources)
    {
        int before=Count;string directory=Path.Combine(root,"luke-symbol103-handler");Directory.CreateDirectory(directory);
        string path=Path.Combine(directory,"game.db");
        var items=resources.ItemResources;var repository=new CharacterRepository(path);
        var owners=new AccountOwners();var leases=new List<AccountOwners.Lease>();var sessions=new List<GameSession>();
        var raids=new RaidRegistry();var parties=new PartyService(repository);
        var dungeons=SelfTestCatalogs.Dungeons(resources.Archive);var rooms=SelfTestCatalogs.Rooms(resources.Archive);
        var definition=RaidCatalog.Load(resources.Archive).PrepareResources(resources.Archive,dungeons,items.Items).Find(1)!;
        var raw=definition.Resources!.Script;var catalog=definition.Resources.EntryCatalog!;
        var rule=new RaidRule([new("[on giveup dungeon]",1900,5054,null,null,null,[])],
            [new("[set]",1901,null,"[kick out character]",null,null,[1])]);
        var script=raw with {Path="selftest/symbol103-not-exit-authority",Phases=raw.Phases.Select((phase,index)=>phase with
            {Rules=index==0?[rule]:[],Dungeons=phase.Dungeons.Select(d=>d with {InitialState=RaidDungeonState.Open}).ToImmutableArray()}).ToImmutableArray()};
        That(LukeRaidStart.RuleRejection(rule,script,raw,new HashSet<int>{5054,5055},new HashSet<int>(),false)==
            "luke-execution-symbol-consumer-unavailable","测试输入仍被生产启动guard拒绝，不借测试开放103");
        var policy=new LukeRaidRunPolicy("selftest/103-handler-no-economy",raw.Phases.SelectMany((phase,index)=>phase.Dungeons.Select(d=>
            new LukeRaidMazeChoice(index,d.Id,catalog.Find(d.Id)!.Mazes.First(m=>m.QuestConnectionTokens.Count==0).SourceOrdinal))).ToImmutableArray(),
            LukeRaidRunEconomy.ControlledNoOrdinaryRewards);
        try
        {
            for(int i=0;i<2;i++)
            {
                var owner=owners.Acquire("symbol103-"+i,()=>{});leases.Add(owner);var origin=new Location(38,0,629,196);
                var candidate=CharacterCreation.Prepare("LkScope"+i,resources.Characters.Get(0),origin,origin,resources.Learning.Get);
                var learning=SkillPointLedger.Synchronize(0,0,90,candidate.InitialLearning with {Experience=resources.Growth.GetLevelStartExperience(90)},resources.Learning);
                var role=repository.Create(owner,candidate with {Level=90,AcceptedStoryLevel=90,CompletedQuests=[2903,4872],InitialLearning=learning});
                var inventory=new InventoryService(items.Items,items.GoldLimits,items.EpicPieces);
                InventoryService.Commit(repository,owner,role,inventory.Grant(role,[new(raw.EntryCost!.ItemId,2)]));
                var session=new GameSession(repository,resources.Towns,owner,entry:new(new(2,20),32,"[raid]"));sessions.Add(session);
                session.Select(0);session.MarkReady();session.CurrentParty=()=>parties.Current(session);
            }
            var actors=sessions.Select(s=>s.CaptureRaidActor(raids)!).ToArray();
            That(raids.Create(actors[0],1,"Scope","Scope"u8.ToArray(),8,1) is not null,"正常领域建团");
            raids.Invite(actors[0],actors[1],0);That(raids.Respond(actors[1],actors[0].Character,true).Accepted,"正常领域入团");
            for(int i=0;i<2;i++)raids.Assign(actors[0],actors[i].Character,(byte)(i+1));
            var expected=raids.Find(actors[0])!;var roster=RaidPreparationPlan.Capture(expected,out _)!;
            var reservations=AntonRaidStart.PlanReservations(expected,raw.EntryCost!,raw.WaitingRoom!,resources.Towns,items.EpicPieces,DateTimeOffset.UtcNow,out var rejection);
            That(rejection is null&&roster is not null,"沿真实费用/位置/名册准备，不伪造角色入口");
            reservations=reservations.Select(r=>r with {Lifecycle=new("selftest/103-domain-only",DateTimeOffset.UtcNow)}).ToImmutableArray();
            // 生命周期来源/时间必须全团一致。
            var admission=reservations[0].Lifecycle;reservations=reservations.Select(r=>r with {Lifecycle=admission}).ToImmutableArray();
            var battle=new AntonRaidBattle(script,kind:RaidKind.Luke);
            var result=RaidOwnerGroup.Execute(RaidKind.Luke,actors.Select(a=>(a.Owner,a.Character)).ToArray(),()=>
            {
                var batch=parties.PlanRaidPreparation(roster!,[]).Batch!;
                var bindings=actors.Select(a=>a.Session.PrepareRaidCombatStart(a,battle)!).ToArray();
                That(batch is not null&&bindings.All(b=>b is not null),"原领域绑定及分队准备完整");
                var started=new RaidStarted(battle.Instance,DateTimeOffset.UtcNow,expected.Members.OrderBy(m=>m.Slot).Select(m=>(m.Actor.Character,m.Slot,m.Squad)).ToImmutableArray());
                return raids.CommitStart(actors[0],expected,reservations,raw.EntryCost!,()=>parties.CreateRaidSquads(expected,()=>
                {
                    var committed=repository.CommitRaidStart(RaidKind.Luke,definition,battle.Instance,reservations);
                    for(int i=0;i<2;i++)sessions[i].InstallRaidStart(reservations[i],committed[i]);
                    foreach(var binding in bindings)binding.Install();return(started,committed);
                },batch));
            });
            That(result.Error==0,"真实扣票/入场SQL及Registry/Session/Battle安装，不冒充Runtime666通过");
            var select=new LukeRaidDungeonService(raids,parties,catalog,policy);var runs=new PartyDungeonService(parties,repository);
            for(int i=0;i<2;i++)
            {
                That(select.Select(sessions[i],5054+i,parties.Current(sessions[i])).Length==1,"真实Luke选图权限与原分队一致");
                That(runs.Enter(sessions[i],(uint)(5054+i),2,dungeons,rooms,null,null,null,null).Length>0,"真实Luke正式Run入场");
                That(sessions[i].PartyDungeon!.CompleteLoading(sessions[i]).Length>0,"真实共享Run加载完成");
                var scene=sessions[i].CurrentFormalDungeonScene()!;
                That(actors[i].IsCurrent&&ReferenceEquals(sessions[i].CurrentRaidBattle(actors[i]),battle)&&sessions[i].StartedRaid?.InstanceId==battle.Instance&&
                    scene.Entry.RaidEntry is {Kind:RaidKind.Luke} id&&id.InstanceId==battle.Instance&&id.Phase==0&&id.Squad==i+1&&scene.Entry.DungeonId==5054+i,
                    "每个owner/session/raid/run/dungeon身份均来自同一正式入口");
            }
            That(runs.Depart(sessions[0],preserveSolo:true).Error==0&&sessions[0].DungeonSnapshot is null&&battle.View.Symbols["[kick out character]"]==1,
                "源Run正常事务退场产生GiveUp103；不手工SET或改Battle状态");
            var target=sessions[1];var actor=actors[1];var view=battle.View;var sceneBefore=target.CurrentFormalDungeonScene()!;
            string runSnapshotBefore=JsonSerializer.Serialize(sceneBefore.Snapshot);
            var partyBefore=parties.Current(target);var runBefore=target.PartyDungeon;string sqlBefore=Snapshot();
            That(target.Phase==SessionPhase.Dungeon&&target.FormalDungeonResultFocus() is null&&
                !battle.HasDungeonContribution(actor,sceneBefore.Snapshot.Identity.InstanceId)&&battle.View.Dungeons[5055]==RaidDungeonState.Open,
                $"另一真实Run仍未提交清图，没有结果focus；phase={target.Phase},run={sceneBefore.Snapshot.Phase},living={sceneBefore.Snapshot.LivingMonsters.Count}");
            using var log=new ServerLog(Path.Combine(directory,"logs"));using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var output=new MemoryStream();await using var sender=new GameConnectionSender(actor.Owner,output,stop,0,(_,_,_,_,_)=>{});
            var context=new GameConnectionContext(new(103,IPAddress.Loopback,output,stop),actor.Owner,target,sender);
            var stats=new CharacterStatComputer(resources.Characters);var town=new TownProjection(resources.Towns,stats,null,items.EpicPieces);
            var commands=new GameCommandRegistry();
            new DungeonSettlementHandler(log,dungeons,SelfTestCatalogs.ClearExperience(resources.Archive),resources.Growth,town,rooms,
                SelfTestCatalogs.MonsterDrops(resources.Archive),items.Items,new(stats),partyDungeons:runs).Register(commands);
            new DungeonHandler(log,dungeons,rooms,resources.Growth,SelfTestCatalogs.KillExperience(resources.Archive),town,new(stats),
                partyDungeons:runs,parties:parties).Register(commands);commands.Seal();
            for(int repeat=0;repeat<2;repeat++)
            {
                That(await commands.DispatchAsync(context,new Packet(1,72,[1,2]))==CommandResult.Unhandled,"103不是跨Run结算返回授权，重复请求同样拒绝");
                await sender.DrainAsync().WaitAsync(TimeSpan.FromSeconds(3));
                That(output.Length==0&&Snapshot()==sqlBefore&&ReferenceEquals(view,battle.View)&&ReferenceEquals(partyBefore,parties.Current(target))&&
                    ReferenceEquals(runBefore,target.PartyDungeon)&&JsonSerializer.Serialize(target.CurrentFormalDungeonScene()!.Snapshot)==runSnapshotBefore&&target.Phase==SessionPhase.Dungeon,
                    "拒绝无ACK，原Run/分队/角色资产/入场资格/奖励SQL均不变");
            }
            That(await commands.DispatchAsync(context,new Packet(1,42,[]))==CommandResult.Handled,"原42仍走正常事务退场");
            await sender.DrainAsync().WaitAsync(TimeSpan.FromSeconds(3));
            That(target.Phase==SessionPhase.Town&&target.DungeonSnapshot is null&&target.PartyDungeon is null&&output.Length>0,
                "不是永久困住角色：原GiveUp完成真实Run释放及Town投影");
            var accepted=GameFrame.Build(1,42,0,[1]);
            That(output.ToArray().AsSpan(0,accepted.Length).SequenceEqual(accepted),"原42精确成功ACK为输出首帧，不拿其它通知冒充应答");
            Console.WriteLine($"PASS {Count-before} 卢克103非退出授权领域/Handler断言（非Runtime666/原P2全流程）");
        }
        finally {foreach(var session in sessions)session.End();foreach(var owner in leases)owner.Dispose();}
        string Snapshot()
        {
            using var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path,Pooling=false}.ToString());db.Open();
            using var command=db.CreateCommand();command.CommandText="SELECT name FROM sqlite_schema WHERE type='table' ORDER BY name";
            var names=new List<string>();using(var reader=command.ExecuteReader())while(reader.Read())names.Add(reader.GetString(0));
            var tables=new List<object>();foreach(string name in names)
            {
                command.CommandText="SELECT * FROM \""+name.Replace("\"","\"\"")+"\"";var rows=new List<string>();
                using var reader=command.ExecuteReader();while(reader.Read()){var values=new object[reader.FieldCount];reader.GetValues(values);rows.Add(JsonSerializer.Serialize(values));}
                tables.Add(new{name,rows=rows.Order(StringComparer.Ordinal).ToArray()});
            }
            return JsonSerializer.Serialize(tables);
        }
    }
}
