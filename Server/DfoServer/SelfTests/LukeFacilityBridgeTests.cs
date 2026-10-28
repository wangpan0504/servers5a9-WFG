using System.Buffers.Binary;
using System.Collections.Immutable;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Raids;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Raids;
using DfoServer.Network.Parsers;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class LukeFacilityBridgeTests
{
    internal static void RunCore(RaidActor[] actors)
    {
        int before = Count;
        byte[] Wire(uint symbol,uint value=1,byte operation=1)
        { byte[] body=new byte[9]; BinaryPrimitives.WriteUInt32LittleEndian(body,symbol); BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4),value); body[8]=operation; return body; }
        foreach(uint symbol in new uint[]{117,118,119}) That(RaidRequests.LukeFacilitySymbol(Wire(symbol))==symbol,"原662精确9B三设施+1");
        foreach(var bad in new[]{Wire(116),Wire(120),Wire(117,0),Wire(117,2),Wire(117,1,0),Wire(117,1,2),Wire(117)[..8],Wire(117).Concat(new byte[]{0}).ToArray()})
            That(RaidRequests.LukeFacilitySymbol(bad) is null,"拒绝任意符号/数值/操作与非精确长度");
        foreach (var (dungeon,obj,symbol,action,name,folder,file) in new[]
        {
            (5054,64249,117,0,"[birth sanctum object]","birth","luke_mainmchine_destroy_raid.act"),
            (5055,64251,118,0,"[extinction sanctum object]","extinction","mainmchine_destroy_raid.act"),
            (5056,64252,119,1,"[spread sanctum object]","spread","censor_machine_broken_front_raid.act")
        })
        {
            var map=new DungeonMap("map",new(0,0),77,"fixture/facility.map");
            var next=map with {Position=new(1,0)};
            var maze=new DungeonMaze(0,2,1,"",[map.Position],[next.Position],[map,next],[]);
            LukeObjectRoom Key(DungeonMap value)=>new(dungeon,0,value.Kind,value.Position,value.Id,value.Path);
            LukeObjectBinding Binding(DungeonMap value)=>new(Key(value),0,obj,symbol,action,
                $"passiveobject/actionobject/map/luke_raid/phase1/sanctum_of_{folder}/action/{file}");
            var catalog=new LukeRaidObjectCatalog(new Dictionary<LukeObjectRoom,ImmutableArray<LukeObjectBinding>>
                {[Key(map)]=[Binding(map)],[Key(next)]=[Binding(next)]}.ToImmutableDictionary(),[]);
            var script=AntonRaidBattleTests.Script(dungeon,20);
            script=script with {Symbols=script.Symbols.Add(name,symbol)};
            var policy=new LukeFacilityReportPolicy("fixture/current-room-client-action-trust",true);
            var run=Guid.NewGuid();
            var rows=actors.Take(4).Select(actor=>(Actor:actor,Identity:new DungeonRunIdentity(actor.Character,0,1,run,1))).ToArray();
            DungeonRunSnapshot Snapshot(int member=0,long epoch=1,DungeonMap? current=null)=>
                new(rows[member].Identity with {Epoch=epoch},SinglePlayerDungeonPhase.Dungeon,current??map,[]);
            AntonRaidBattle Prepared(RaidScript resource,LukeRaidCompletionPolicy? rewards=null,bool enabled=true,bool observe=true,
                LukeRaidObjectCatalog? objects=null)
            {
                var value=new AntonRaidBattle(resource,kind:RaidKind.Luke,completion:rewards,facilities:enabled?policy:null);
                foreach(var actor in actors) That(value.JoinStart(actor),"原Battle登记成员");
                using(var reservation=value.ReserveDungeon(new(value.Instance,0,1,RaidKind.Luke),(uint)dungeon,rows,
                    new LukeRaidObjectRegistration(run,dungeon,maze,objects??catalog))!) reservation.Install();
                if(observe)value.ObserveLukeRun(run,rows,map.Id,map);
                return value;
            }
            LukeFacilityReportResult Report(AntonRaidBattle value,int member=0,DungeonRunSnapshot? snapshot=null,int? reported=null)=>
                value.ConfirmLukeFacility(actors[member],snapshot??Snapshot(member),reported??symbol,1,1);
            void Rejected(LukeFacilityReportResult value,string reason)=>That(value.Outcome==LukeFacilityReportOutcome.Rejected,reason);
            var disabled=Prepared(script,enabled:false);
            That(!disabled.AcceptsLukeFacilityReports,"默认null完全不开放原662");
            Rejected(Report(disabled),"默认策略不消费事件");
            var unloaded=Prepared(script,observe:false);
            Rejected(Report(unloaded),"原Run尚未全员加载登记不能报告");
            var ambiguous=new LukeRaidObjectCatalog(new Dictionary<LukeObjectRoom,ImmutableArray<LukeObjectBinding>>
                {[Key(map)]=[Binding(map),Binding(map) with {Ordinal=1}]}.ToImmutableDictionary(),[]);
            Rejected(Report(Prepared(script,objects:ambiguous)),"同房多个候选不猜物件身份");
            var wrongAction=new LukeRaidObjectCatalog(new Dictionary<LukeObjectRoom,ImmutableArray<LukeObjectBinding>>
                {[Key(map)]=[Binding(map) with {ActionPath="fixture/other.act"}]}.ToImmutableDictionary(),[]);
            Rejected(Report(Prepared(script,objects:wrongAction)),"路径不符不借模板授权");
            var battle=Prepared(script);
            Rejected(Report(battle,reported:symbol+1),"当前房唯一设施与请求符号必须相同");
            Rejected(battle.ConfirmLukeFacility(actors[4],Snapshot() with {Identity=Snapshot().Identity with {CharacterId=actors[4].Character}},symbol,1,1),"另一分队成员不能借原Run");
            Rejected(Report(battle,snapshot:Snapshot() with {Phase=SinglePlayerDungeonPhase.Loading}),"加载中拒绝");
            Rejected(Report(battle,snapshot:Snapshot() with {Identity=Snapshot().Identity with {InstanceId=Guid.NewGuid()}}),"错Run拒绝");
            Rejected(Report(battle,snapshot:Snapshot(epoch:2)),"未安装新epoch拒绝");
            Rejected(Report(battle,snapshot:Snapshot(current:next)),"同MAP另一格拒绝");
            Rejected(Report(battle,snapshot:Snapshot() with {Identity=Snapshot().Identity with {TaskAttempt=2}}),"错任务代次拒绝");
            var living=Snapshot() with {LivingMonsters=new ushort[]{1}};
            if(obj!=64252)Rejected(Report(battle,snapshot:living),"仅birth/extinction保留已证怪物门槛");
            That(Report(battle,snapshot:obj==64252?living:Snapshot()).Outcome==LukeFacilityReportOutcome.Applied && battle.View.Symbols[name]==1,
                "原动作报告一次增量，spread不额外要求清房");
            That(Report(battle).Outcome==LukeFacilityReportOutcome.Duplicate && Report(battle,1).Outcome==LukeFacilityReportOutcome.Duplicate && battle.View.Symbols[name]==1,
                "同房多人重放共享逻辑一次消费");
            battle.ObserveLukeRun(run,rows.Select(row=>(row.Actor,row.Identity with {Epoch=2})).ToArray(),next.Id,next);
            Rejected(Report(battle),"服务端旧epoch不能作为当前事实");
            var tasks=Enumerable.Range(0,4).Select(i=>Task.Run(()=>Report(battle,i,Snapshot(i,2,next)))).ToArray();
            Task.WaitAll(tasks);
            That(tasks.Count(task=>task.Result.Outcome==LukeFacilityReportOutcome.Applied)==1 && battle.View.Symbols[name]==2,"四人并发第二设施一次消费");
            // 原9B没有epoch：第二房同symbol的合法报告与跨房相同旧body无法区别，此处不假造防重放。
            battle.ObserveLukeRun(run,rows.Select(row=>(row.Actor,row.Identity with {Epoch=3})).ToArray(),map.Id,map);
            That(Report(battle,snapshot:Snapshot(epoch:3)).Outcome==LukeFacilityReportOutcome.Duplicate && battle.View.Symbols[name]==2,"回访已消费逻辑设施不重增");
            That(battle.View.State==AntonRaidBattleState.Fighting && !battle.HasPendingTransition,"不是清房自动胜利");
            battle.ReleaseDungeon(actors[0],run);
            Rejected(Report(battle,snapshot:Snapshot(epoch:3)),"退出原Run后拒绝");
            if(dungeon==5054)
            {
                RaidScript Trigger(string behavior)=>script with {Phases=script.Phases.SetItem(0,script.Phases[0] with
                    {Rules=[new([new("[on change symbol]",0,null,name,null,null,[])],[new(behavior,0,null,null,null,null,[])])]})};
                var broken=Prepared(Trigger("[unsupported facility action]"));
                var unchanged=broken.View;
                for(int attempt=0;attempt<2;attempt++) Reject<InvalidDataException>(()=>Report(broken),"规则失败不消费，重复仍实际检查规则");
                That(ReferenceEquals(unchanged,broken.View),"规则异常不留下部分symbol");
                var completion=new LukeRaidCompletionPolicy("fixture/facility-barrier",2,new(DayOfWeek.Sunday,new TimeOnly(6,0),TimeSpan.FromHours(8)),
                    Enumerable.Range(0,2).Select(phase=>new LukePhaseCompletionPolicy(0,2,new("fixture/phase",
                        [new(0,1,[new(100,1)],[])],new(0,(byte)phase,1,0,0,0)))).ToImmutableArray(),LukeParticipationCutoff.TerminalCommittedFacts);
                var terminal=Prepared(Trigger("[clear phase]"),completion);
                That(Report(terminal).Outcome==LukeFacilityReportOutcome.Applied,"合成终结规则通过真实消费链");
                var sealedPhase=terminal.PendingLukeSettlement();
                That(terminal.HasPendingTransition && sealedPhase is not null && terminal.View.State==AntonRaidBattleState.Fighting,"原资格SQL未提交不能开放Reward");
                That(Report(terminal).Outcome==LukeFacilityReportOutcome.Duplicate && ReferenceEquals(sealedPhase,terminal.PendingLukeSettlement()),"SQL重试重复报告保留同一封存候选");
                foreach(var row in rows)terminal.ReleaseDungeon(row.Actor,run);
                That(terminal.HasPendingTransition && terminal.View.State==AntonRaidBattleState.Fighting && ReferenceEquals(sealedPhase,terminal.PendingLukeSettlement()),"全部退出仍不越过未完成SQL屏障");
            }
        }
        Reject<InvalidDataException>(()=>new AntonRaidBattle(AntonRaidBattleTests.Script(10),kind:RaidKind.Luke,facilities:new("",true)),"策略缺来源拒绝");
        Reject<InvalidDataException>(()=>new AntonRaidBattle(AntonRaidBattleTests.Script(10),kind:RaidKind.Luke,facilities:new("fixture",false)),"未接受客户端报告信任模型拒绝");
        Console.WriteLine($"PASS {Count-before} 卢克原662当前场景兼容合同与一次性消费断言");
    }
}
