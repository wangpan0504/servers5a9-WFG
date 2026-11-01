using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Raids;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Raids;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class LukeRaidCompletionTests
{
    // 纯领域证明密封/截止/屏障，不冒充真实SQL或Runtime；资源事务另走原666和正式Run。
    internal static void RunCore(RaidActor[] actors)
    {
        int before = Count;
        var clock = new Clock(DateTimeOffset.Parse("2026-10-25T05:59:59+08:00"));
        var cycle = new RaidWeeklyCycle(DayOfWeek.Sunday, new TimeOnly(6, 0), TimeSpan.FromHours(8));
        That(cycle.Key(clock.Now)==20261018&&cycle.Key(clock.Now.AddSeconds(1))==20261025,
            "明确合成周日06策略跨界，非从票删除推定原服通关周期");
        var policy = new LukeRaidCompletionPolicy("fixture/terminal-cutoff",2,cycle,
            Enumerable.Range(0,2).Select(phase=>new LukePhaseCompletionPolicy(1,2,new("fixture/phase"+phase,
                [new(0,1,[new(100,1)],[]),new(1,1,[new(100,1)],[])],new(0,(byte)phase,1,0,0,0)))).ToImmutableArray(),
            LukeParticipationCutoff.TerminalCommittedFacts);
        var script = AntonRaidBattleTests.Script(10,20);
        script=script with {Phases=script.Phases.SetItem(0,script.Phases[0] with
            {Dungeons=script.Phases[0].Dungeons.Add(new(11,RaidDungeonState.Open,"[hp]",1,1))})};
        var battle=new AntonRaidBattle(script,kind:RaidKind.Luke,completion:policy,clock:clock);
        foreach(var actor in actors)That(battle.JoinStart(actor),"领域登记八名原始当前actor");
        Reject<InvalidOperationException>(()=>AntonRaidBattle.LukePhaseSettlement.Capture(battle,0),"无权威终结不能提前密封任意anchor");
        var first=Enter(0,10,1);var second=Enter(4,11,2);
        Clear(first,10);
        var candidate=battle.PendingLukeSettlement()!;
        That(!battle.RequiresLukeCompletedPhaseReturn(actors[0],battle.Instance,0),"阶段SQL未确认不允许自动退场");
        That(battle.View.State==AntonRaidBattleState.Fighting&&battle.View.Dungeons[10]==RaidDungeonState.Clear&&
            battle.View.Symbols["[hp]"]==0,"终图已提交地图与HP立即更新，Reward仍等待退场和资格事务屏障");
        That(candidate is {Phase:0,Week:20261018}&&candidate.Members.Take(4).All(member=>member.CommittedRuns==1)&&
            candidate.Members.Skip(4).All(member=>member.CommittedRuns==0),"终结线性化只冻结当时已提交参与");
        clock.Now=clock.Now.AddSeconds(2);
        Clear(second,11);
        That(ReferenceEquals(candidate,battle.PendingLukeSettlement())&&candidate.Anchor<clock.Now&&candidate.Members.Skip(4).All(member=>member.CommittedRuns==0),
            "终结后的旧Run事实可保留，但不追溯改变密封资格或周anchor");
        foreach(var row in first.Concat(second))battle.ReleaseDungeon(row.Actor,row.Identity.InstanceId);
        That(battle.View.State==AntonRaidBattleState.Fighting&&battle.HasPendingTransition,"全部旧Run退出也不能越过未提交阶段SQL屏障");
        var receipt=Receipt(candidate);
        Reject<InvalidDataException>(()=>battle.AcceptLukeSettlement(candidate,receipt with {Members=receipt.Members.SetItem(0,receipt.Members[0] with {Event=Guid.NewGuid()})}),
            "其它事件回执不能安装到密封候选");
        battle.AcceptLukeSettlement(candidate,receipt);
        That(battle.View.State==AntonRaidBattleState.Reward&&battle.PendingLukeSettlement() is null,"仅明确已提交回执才开放Reward");
        var replay=JsonSerializer.Deserialize<LukePhaseCommit>(JsonSerializer.Serialize(receipt))!;
        That(battle.AcceptLukeSettlement(candidate,replay) is null,"重新读取的相同资格/奖励值幂等，不依赖数组引用");
        foreach(var actor in actors.Take(4))foreach(byte kind in new byte[]{1,0})
        {
            battle.ConfirmReward(actor,new(battle.Instance,0,kind,1,actor.Character,policy.Phases[0].Rewards.Source,[new(100,1)],false,candidate.Week){RaidType=RaidKind.Luke});
            if(actor!=actors[3]||kind!=0)That(battle.AdvanceAfterRewards(battle.Instance,battle.View.Revision) is null,
                "部分有资格成员或0/1任一种未完成都不能跳过领奖");
        }
        That(!battle.ConfirmReward(actors[4],new(battle.Instance,0,0,1,actors[4].Character,policy.Phases[0].Rewards.Source,[new(100,1)],false,candidate.Week){RaidType=RaidKind.Luke}),
            "终结截止后才清图的成员不能冒认本阶段已密封领奖资格");
        That(battle.AdvanceAfterRewards(battle.Instance,battle.View.Revision)?.After.Phase==1,"仅有资格成员所需奖项完成即可推进，不被无资格成员永久卡住");
        foreach(bool none in new[]{true,false})
        {
            var emptyBattle=new AntonRaidBattle(script,kind:RaidKind.Luke,completion:policy,clock:clock);
            foreach(var actor in actors)That(emptyBattle.JoinStart(actor),"独立领域边界登记原成员");
            for(int phase=0;phase<(none?2:1);phase++)
            {
                var id=Guid.NewGuid();var rows=actors.Take(4).Select(actor=>(actor,new DungeonRunIdentity(actor.Character,0,1,id,1))).ToArray();
                using(var reservation=emptyBattle.ReserveDungeon(new(emptyBattle.Instance,phase,1,RaidKind.Luke),(uint)(phase==0?10:20),rows)!)reservation.Install();
                AntonRaidBattle.DungeonReservation? cancelled=null;
                if(none&&phase==0)
                {
                    var otherId=Guid.NewGuid();
                    cancelled=emptyBattle.ReserveDungeon(new(emptyBattle.Instance,0,2,RaidKind.Luke),11,
                        actors.Skip(4).Select(actor=>(actor,new DungeonRunIdentity(actor.Character,0,1,otherId,1))).ToArray())!;
                }
                emptyBattle.CompleteLukeDungeon(rows.Select(row=>(row.actor,new DungeonClearFact(row.Item2,(uint)(phase==0?10:20),[Guid.NewGuid()])
                    {ClearMap=new DungeonMap("boss",new(0,0),77,"fixture/boss.map")})).ToArray());
                var sealedPhase=emptyBattle.PendingLukeSettlement()!;
                var denied=Receipt(sealedPhase) with {Members=Receipt(sealedPhase).Members.Select((row,index)=>row with
                    {Eligible=!none&&index==0,CountsAsClear=phase==1}).ToImmutableArray()};
                long revision=emptyBattle.View.Revision;
                var ready=emptyBattle.AcceptLukeSettlement(sealedPhase,denied);
                That(ready is not null&&ready.After.Revision==revision+1&&
                    emptyBattle.RequiresLukeCompletedPhaseReturn(actors[0],emptyBattle.Instance,phase),
                    "阶段持久确认推进版本并通知自动退场，但不直接跳过正式Run释放");
                That(!emptyBattle.RequiresLukeCompletedPhaseReturn(actors[0],Guid.NewGuid(),phase)&&
                    !emptyBattle.RequiresLukeCompletedPhaseReturn(actors[0],emptyBattle.Instance,1-phase),"旧实例或旧阶段回调不能退新Run");
                That(emptyBattle.View.State==AntonRaidBattleState.Fighting,"即便全拒绝且SQL已提交，旧Run仍阻止转换");
                foreach(var row in rows)emptyBattle.ReleaseDungeon(row.actor,id);
                That(cancelled is not null||emptyBattle.View.State==AntonRaidBattleState.Reward||
                    !emptyBattle.RequiresLukeCompletedPhaseReturn(actors[0],emptyBattle.Instance,phase),
                    "阶段已推进后撤销旧退场资格；Reward仍允许尚在选图的成员返回");
                if(cancelled is not null)
                {
                    That(emptyBattle.HasPendingTransition&&emptyBattle.View.Phase==0,"尚未Install的另一分队预留也构成退出屏障");
                    cancelled.Dispose();var released=cancelled.ReleasedChange;
                    That(released is not null&&ReferenceEquals(released.After,emptyBattle.View)&&released.After.Phase==1,
                        "取消最后预留返回精确本次转换回执，供owner锁外同步");
                    cancelled.Dispose();That(ReferenceEquals(released,cancelled.ReleasedChange),"重复Dispose不二次转换或替换回执");
                }
                if(none)
                {
                    That(phase==0?emptyBattle.View is {Phase:1,State:AntonRaidBattleState.Fighting}:emptyBattle.View.State==AntonRaidBattleState.Completed,
                        "全拒绝阶段无伪领奖，退出屏障后沿同一推进函数自动收敛");
                    var view=emptyBattle.View;
                    That(emptyBattle.AcceptLukeSettlement(sealedPhase,denied) is null&&ReferenceEquals(view,emptyBattle.View),"重复已提交阶段不重复转换");
                }
                else
                {
                    That(emptyBattle.DetachStart(actors[0])&&emptyBattle.AdvanceAfterRewards(emptyBattle.Instance,emptyBattle.View.Revision) is null&&
                        emptyBattle.View.State==AntonRaidBattleState.Reward,"唯一已密封有资格成员断线不能被当前成员过滤成零待领奖而丢奖");
                }
            }
        }
        Console.WriteLine($"PASS {Count-before} 卢克阶段密封、参与截止与领奖屏障领域断言");

        (RaidActor Actor,DungeonRunIdentity Identity)[] Enter(int start,int dungeon,byte squad)
        {
            var run=Guid.NewGuid();var rows=actors.Skip(start).Take(4).Select(actor=>(actor,new DungeonRunIdentity(actor.Character,0,1,run,1))).ToArray();
            using var reservation=battle.ReserveDungeon(new(battle.Instance,battle.View.Phase,squad,RaidKind.Luke),(uint)dungeon,rows)!;
            reservation.Install();return rows;
        }
        void Clear((RaidActor Actor,DungeonRunIdentity Identity)[] rows,uint dungeon)=>battle.CompleteLukeDungeon(rows.Select(row=>(row.Actor,
            new DungeonClearFact(row.Identity,dungeon,[Guid.NewGuid()]){ClearMap=new DungeonMap("boss",new(0,0),77,"fixture/boss.map")})).ToArray());
        static LukePhaseCommit Receipt(AntonRaidBattle.LukePhaseSettlement candidate)=>new(candidate.Members.Select(member=>
            new RaidPhaseEligibility(candidate.Instance,RaidKind.Luke,candidate.Phase,candidate.Event,candidate.Anchor.ToUnixTimeSeconds(),member.Character,
                false,member.CommittedRuns>=1,"fixture/domain",candidate.Policy.Rewards.Source,candidate.Week,candidate.Policy.Rewards.Offers)).ToImmutableArray());
    }
    sealed class Clock(DateTimeOffset now):TimeProvider
    {
        public DateTimeOffset Now {get;set;}=now;
        public override DateTimeOffset GetUtcNow()=>Now;
    }
}
