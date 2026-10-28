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
            // 本服兼容Run账本：完成的贡献保留，未完成退出必须只撤回本次新增。
            var repeatedScript=script with {Symbols=script.Symbols.Add("[giveups]",251),
                Phases=script.Phases.SetItem(0,script.Phases[0] with
                {Dungeons=[script.Phases[0].Dungeons[0] with {HpRaw=3}],Rules=script.Phases[0].Rules.Add(new(
                    [new("[on giveup dungeon]",710,dungeon,null,null,null,[])],
                    [new("[reset sanctum object]",711,dungeon,null,null,null,[]),
                     new("[set]",712,null,"[giveups]",null,null,[1]) {Operator="[+]"}]))})};
            var repeated=Prepared(repeatedScript);
            That(Report(repeated).Outcome==LukeFacilityReportOutcome.Applied,"第一Run接受设施报告");
            That(repeated.CompleteLukeDungeon(rows.Select(row=>(row.Actor,new DungeonClearFact(row.Identity,(uint)dungeon,[Guid.NewGuid()]) {ClearMap=map})).ToArray()) is not null,
                "第一Run以已提交清图事实完成，不手改完成标志");
            foreach(var row in rows) repeated.ReleaseDungeon(row.Actor,run);
            That(repeated.View.Symbols[name]==1,"已完成Run离场保留设施贡献");
            Guid completedRun=run;
            void Reenter()
            {
                run=Guid.NewGuid();
                rows=actors.Take(4).Select(actor=>(Actor:actor,Identity:new DungeonRunIdentity(actor.Character,0,1,run,1))).ToArray();
                using(var reservation=repeated.ReserveDungeon(new(repeated.Instance,0,1,RaidKind.Luke),(uint)dungeon,rows,
                    new LukeRaidObjectRegistration(run,dungeon,maze,catalog))!) reservation.Install();
                repeated.ObserveLukeRun(run,rows,map.Id,map);
            }
            Reenter();
            That(Report(repeated).Outcome==LukeFacilityReportOutcome.Applied,"重进新Run可报告同一逻辑设施");
            repeated.ObserveLukeRun(run,rows.Select(row=>(row.Actor,row.Identity with {Epoch=2})).ToArray(),next.Id,next);
            That(Report(repeated,snapshot:Snapshot(epoch:2,current:next)).Outcome==LukeFacilityReportOutcome.Applied && repeated.View.Symbols[name]==3,
                "第二Run两次贡献与已完成Run合计三次");
            for(int member=0;member<3;member++) repeated.ReleaseDungeon(actors[member],run);
            That(repeated.View.Symbols[name]==3,"还有当前成员时不撤销共享Run贡献");
            Guid abortedRun=run;
            repeated.ReleaseDungeon(actors[3],run);
            That(repeated.View.Symbols[name]==1&&repeated.View.Symbols["[giveups]"]==1&&repeated.View.Deferred.ContainsKey(711),
                "末成员只撤本Run贡献并执行一次GiveUp，物理RESET仍明确Deferred");
            var afterAbort=repeated.View;
            repeated.ReleaseDungeon(actors[3],abortedRun);
            That(ReferenceEquals(afterAbort,repeated.View),"重复释放旧Run不二次扣除");
            Reenter();
            That(Report(repeated).Outcome==LukeFacilityReportOutcome.Applied && repeated.View.Symbols[name]==2,"再次重进不能叠加已放弃贡献");
            foreach(var row in rows) { repeated.ReleaseDungeon(row.Actor,completedRun); repeated.ReleaseDungeon(row.Actor,abortedRun); }
            That(repeated.View.Symbols[name]==2,"旧Run迟到释放不影响当前Run");
            foreach(var row in rows) repeated.ReleaseDungeon(row.Actor,run);
            That(repeated.View.Symbols[name]==1&&repeated.View.Symbols["[giveups]"]==2,"新Run放弃再次精确回滚，不清空历史完成贡献");
            foreach(int overwritten in new[]{0,7})
            {
                var invalid=Prepared(script);
                That(Report(invalid).Outcome==LukeFacilityReportOutcome.Applied,"失配用例先通过正式报告累计");
                invalid.SetMechanism(invalid.Instance,invalid.View.Revision,name,overwritten);
                invalid.ObserveLukeRun(run,rows.Select(row=>(row.Actor,row.Identity with {Epoch=2})).ToArray(),next.Id,next);
                Rejected(Report(invalid,snapshot:Snapshot(epoch:2,current:next)),"未知机制降低或抬高计数后拒绝新报告，不重建错误账本");
                for(int member=0;member<3;member++) invalid.ReleaseDungeon(actors[member],run);
                That(invalid.DetachStart(actors[3])&&!invalid.DetachStart(actors[3])&&invalid.View.Symbols[name]==overwritten,
                    "失配不困住末成员离场，不猜减量或改写其它机制权威值");
                That(invalid.LastFacilityLedgerMismatch is {Expected:1} mismatch&&mismatch.Actual==overwritten&&
                    mismatch.Run==run&&mismatch.Phase==0&&mismatch.Dungeon==dungeon&&mismatch.Symbol==name,
                    "失配保留一条精确类型化诊断，不以静默钳零掩盖");
            }
            var overwrittenBack=Prepared(script);
            That(Report(overwrittenBack).Outcome==LukeFacilityReportOutcome.Applied,"ABA用例先通过正式报告累计");
            overwrittenBack.SetMechanism(overwrittenBack.Instance,overwrittenBack.View.Revision,name,0);
            overwrittenBack.SetMechanism(overwrittenBack.Instance,overwrittenBack.View.Revision,name,1);
            for(int member=0;member<3;member++) overwrittenBack.ReleaseDungeon(actors[member],run);
            That(overwrittenBack.DetachStart(actors[3])&&overwrittenBack.View.Symbols[name]==1&&
                overwrittenBack.LastFacilityLedgerMismatch is {Expected:1,Actual:1} aba&&aba.ExpectedWriteEpoch!=aba.ActualWriteEpoch,
                "外部覆盖后回到相同数字且无中间报告，写入来源仍不同，安全退出不能误撤");
            var reopenScript=script with {Symbols=script.Symbols.Add("[reopen]",250),
                Phases=script.Phases.SetItem(0,script.Phases[0] with {Rules=script.Phases[0].Rules.Add(new(
                    [new("[on change symbol]",700,null,"[reopen]",null,null,[])],
                    [new("[set dungeon state]",701,dungeon,null,RaidDungeonState.Close,null,[]),
                     new("[set dungeon state]",702,dungeon,null,RaidDungeonState.Open,null,[])]))})};
            var reopened=Prepared(reopenScript);
            That(Report(reopened).Outcome==LukeFacilityReportOutcome.Applied,"目标重开前累计报告");
            reopened.SetMechanism(reopened.Instance,reopened.View.Revision,"[reopen]",1);
            var newGeneration=reopened.View;
            foreach(var row in rows) reopened.ReleaseDungeon(row.Actor,run);
            That(ReferenceEquals(newGeneration,reopened.View),"旧目标代末成员离场不回滚新代状态");
            if(dungeon==5054)
            {
                RaidScript Trigger(string behavior)=>script with {Phases=script.Phases.SetItem(0,script.Phases[0] with
                    {Rules=[new([new("[on change symbol]",0,null,name,null,null,[])],[new(behavior,0,null,null,null,null,[])])]})};
                var broken=Prepared(Trigger("[unsupported facility action]"));
                var unchanged=broken.View;
                for(int attempt=0;attempt<2;attempt++) Reject<InvalidDataException>(()=>Report(broken),"规则失败不消费，重复仍实际检查规则");
                That(ReferenceEquals(unchanged,broken.View),"规则异常不留下部分symbol");
                var scriptedOverwrite=script with {Phases=script.Phases.SetItem(0,script.Phases[0] with
                    {Rules=[new([new("[on change symbol]",720,null,name,null,null,[])],
                        [new("[set]",721,null,name,null,null,[1])])]})};
                var sameValueWrite=Prepared(scriptedOverwrite);
                That(Report(sameValueWrite).Outcome==LukeFacilityReportOutcome.Applied,"规则同值SET不伪造额外on-change循环");
                foreach(var row in rows) sameValueWrite.ReleaseDungeon(row.Actor,run);
                That(sameValueWrite.View.Symbols[name]==1&&sameValueWrite.LastFacilityLedgerMismatch is {Expected:1,Actual:1} scriptMismatch&&
                    scriptMismatch.ExpectedWriteEpoch!=scriptMismatch.ActualWriteEpoch,"报告同次Evaluate的同值外部SET也失去撤回归属");
                var failedScript=script with {Phases=script.Phases.SetItem(0,script.Phases[0] with
                    {Rules=[new([new("[on change symbol]",730,null,name,null,null,[]),new("[if]",733,null,name,null,null,[2])],
                        [new("[set]",731,null,name,null,null,[1]),new("[unsupported facility action]",732,null,null,null,null,[])])]})};
                var failedWrite=Prepared(failedScript);
                That(Report(failedWrite).Outcome==LukeFacilityReportOutcome.Applied,"候选失败用例先建立有效来源账本");
                var beforeFailedWrite=failedWrite.View;
                Reject<InvalidDataException>(()=>failedWrite.SetMechanism(failedWrite.Instance,failedWrite.View.Revision,name,2),"带写代的候选规则失败仍抛原规则错误");
                That(ReferenceEquals(beforeFailedWrite,failedWrite.View)&&failedWrite.View.LukeFacilityWriteEpochs.IsEmpty&&
                    failedWrite.LastFacilityLedgerMismatch is null,"失败Evaluate不发布写代、symbol、消费或误诊断");
                foreach(var row in rows) failedWrite.ReleaseDungeon(row.Actor,run);
                That(failedWrite.View.Symbols[name]==0&&failedWrite.LastFacilityLedgerMismatch is null,"失败候选不破坏原有效Run撤回资格");
                var noOp=Prepared(script);
                That(Report(noOp).Outcome==LukeFacilityReportOutcome.Applied,"同值无操作用例先接受报告");
                var beforeNoOp=noOp.View;
                That(noOp.SetMechanism(noOp.Instance,noOp.View.Revision,name,1) is null&&ReferenceEquals(beforeNoOp,noOp.View),
                    "公开SetMechanism同值仍为原no-op，不签发伪造写代");
                foreach(var row in rows) noOp.ReleaseDungeon(row.Actor,run);
                That(noOp.View.Symbols[name]==0&&noOp.LastFacilityLedgerMismatch is null,"同值no-op不妨碍有效Run贡献撤回");
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
