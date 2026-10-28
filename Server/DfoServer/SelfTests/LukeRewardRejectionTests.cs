using System.Collections.Immutable;
using System.Net;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Raids;
using DfoServer.GameWorld.Dungeons;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Handlers;
using DfoServer.Network.Protocol;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

// 生产Handler/发送泵的排队隔离测试；合成领域Run推进，不是完整Runtime、SQL领奖或原生验收。
internal static class LukeRewardRejectionTests
{
    internal static async Task RunAsync(RaidActor actor, string root)
    {
        int before=Count;
        foreach(string scenario in new[]{"same-phase-revision","state-change","phase-change","instance-change"})
        {
            var battle=new AntonRaidBattle(AntonRaidBattleTests.Script(10,20),kind:RaidKind.Luke);
            var policies=Enumerable.Range(0,2).Select(phase=>new RaidPhaseRewardPolicy("fixture/rejection-scope",
                [new(0,0,[new(100,1)],[])],new(0,(byte)phase,1,0,0,0))).ToImmutableArray();
            That(actor.Session.AttachRaidCombat(actor,battle,policies),"拒绝包用例绑定独立合成Battle");
            AntonRaidBattle? replacement=null;
            using var stop=new CancellationTokenSource();
            using var stream=new GameSenderBudgetTests.HeldStream();
            await using var sender=new GameConnectionSender(actor.Owner,stream,stop,0,(_,_,_,_,_)=>{});
            using var log=new ServerLog(Path.Combine(root,"reward-rejection-"+scenario));
            var context=new GameConnectionContext(new(1,IPAddress.Loopback,stream,stop),actor.Owner,actor.Session,sender);
            var commands=new GameCommandRegistry();
            // 非法kind在生产Handler读取奖励依赖之前即拒绝；不装资源、不伪造奖励仓储成功。
            new RaidCombatHandler(null!,null!,null!,null!,null!,log).Register(commands);
            commands.Seal();
            try
            {
                if(scenario=="phase-change") CompleteRun();
                var captured=battle.View;
                var held=sender.Send(0,2,[7],"held-before-rejection");
                await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                // 与共享队伍分派使用同一真实publication机制：入队后只保留原会话epoch。
                long epoch=actor.Session.SelectionEpoch;
                using(sender.QueuePublication(()=>actor.Owner.IsCurrent&&actor.Session.SelectionEpoch==epoch))
                    That(await commands.DispatchAsync(context,new Packet(1,660,[2,0]))==CommandResult.Handled,
                        "真实660非法kind走生产拒绝，不等待阻塞的输出流");
                if(scenario=="same-phase-revision")
                    battle.SetMechanism(battle.Instance,battle.View.Revision,"[hp]",2);
                else if(scenario=="state-change") CompleteRun();
                else if(scenario=="phase-change")
                {
                    That(battle.ConfirmReward(actor,new(battle.Instance,0,0,0,actor.Character,policies[0].Source,[new(100,1)],false,1)
                        {RaidType=RaidKind.Luke}),"隔离夹具明确确认唯一领域奖励义务，不伪称SQL领取");
                    That(battle.AdvanceAfterRewards(battle.Instance,battle.View.Revision)?.After.Phase==1,"沿领域领奖屏障推进下一阶段");
                }
                else
                {
                    That(actor.Session.DetachRaidCombatStart(actor,battle.Instance),"释放旧实例绑定");
                    replacement=new AntonRaidBattle(AntonRaidBattleTests.Script(10,20),kind:RaidKind.Luke);
                    That(actor.Session.AttachRaidCombat(actor,replacement,policies),"同角色epoch绑定新实例");
                }
                That(scenario!="same-phase-revision"||battle.View.Phase==captured.Phase&&battle.View.State==captured.State&&battle.View.Revision!=captured.Revision,
                    "同阶段测试确实只变revision，不能用严格View引用过度丢拒绝包");
                stream.Release.TrySetResult(); await held; await sender.DrainAsync().WaitAsync(TimeSpan.FromSeconds(3));
                That(stream.Frames.Count==(scenario=="same-phase-revision"?2:1),"旧state/phase/instance拒绝跳过，同phase新revision正常发送："+scenario);
                if(scenario=="same-phase-revision")
                    Bytes(stream.Frames[1],GameFrame.Build(1,660,1,[0,0x12,0]),"保留已证660失败正文及真实帧序");
                // 旧拒绝被跳过后，当前阶段的新请求仍需正常清pending；不把所有拒绝永久静默。
                int priorFrames=stream.Frames.Count;
                using(sender.QueuePublication(()=>actor.Owner.IsCurrent&&actor.Session.SelectionEpoch==epoch))
                    That(await commands.DispatchAsync(context,new Packet(1,660,[2,0]))==CommandResult.Handled,"当前阶段仍接受失败请求");
                await sender.DrainAsync().WaitAsync(TimeSpan.FromSeconds(3));
                That(stream.Frames.Count==priorFrames+1,"当前窗口新请求确实新增一帧");
                Bytes(stream.Frames[^1],GameFrame.Build(1,660,(ushort)(stream.Frames.Count-1),[0,0x12,0]),"当前身份的新拒绝照常写出");
                That(sender.PendingBytes==0,"跳过旧拒绝与新拒绝均归还发送预算");
            }
            finally
            {
                stream.Release.TrySetResult();
                actor.Session.DetachRaidCombatStart(actor,(replacement??battle).Instance);
            }
            void CompleteRun()
            {
                var identity=new DungeonRunIdentity(actor.Character,0,1,Guid.NewGuid(),1);
                using(var reservation=battle.ReserveDungeon(new(battle.Instance,0,1,RaidKind.Luke),10,[(actor,identity)])!) reservation.Install();
                var map=new DungeonMap("boss",new(0,0),77,"fixture/rejection-boss.map");
                That(battle.CompleteLukeDungeon([(actor,new DungeonClearFact(identity,10,[Guid.NewGuid()]){ClearMap=map})]) is not null,
                    "用已绑定Run清图事实触发阶段终结，不直接改Battle状态");
                battle.ReleaseDungeon(actor,identity.InstanceId);
                That(battle.View.State==AntonRaidBattleState.Reward,"末成员退出经过原阶段屏障");
            }
        }
        foreach(string scenario in new[]{"settlement-same-phase","settlement-next-phase","claim-window-next-phase"})
            await SettlementAsync(actor,root,scenario);
        Console.WriteLine($"PASS {Count-before} 卢克拒绝包排队身份隔离断言（非完整团本）");
    }
    static async Task SettlementAsync(RaidActor actor,string root,string scenario)
    {
        var policies=Enumerable.Range(0,2).Select(phase=>new RaidPhaseRewardPolicy("fixture/rejection-settlement",
            [new(0,0,[new(100,1)],[])],new(0,(byte)phase,1,0,0,0))).ToImmutableArray();
        var completion=new LukeRaidCompletionPolicy("fixture/rejection-settlement",2,
            new(DayOfWeek.Sunday,new TimeOnly(6,0),TimeSpan.FromHours(8)),
            policies.Select(policy=>new LukePhaseCompletionPolicy(1,2,policy)).ToImmutableArray(),LukeParticipationCutoff.TerminalCommittedFacts);
        var battle=new AntonRaidBattle(AntonRaidBattleTests.Script(10,20),kind:RaidKind.Luke,completion:completion);
        actor.Session.PrepareRaidCombatStart(actor,battle)!.Install();
        using var stop=new CancellationTokenSource();
        using var stream=new GameSenderBudgetTests.HeldStream();
        stream.Release.TrySetResult();
        await using var sender=new GameConnectionSender(actor.Owner,stream,stop,0,(_,_,_,_,_)=>{});
        using var log=new ServerLog(Path.Combine(root,"reward-rejection-"+scenario));
        var context=new GameConnectionContext(new(1,IPAddress.Loopback,stream,stop),actor.Owner,actor.Session,sender);
        var store=new SettlementStore(scenario!="settlement-next-phase");
        var clock=new CallbackClock(()=>
        {
            if(scenario!="claim-window-next-phase") return;
            // Handler冻结拒绝窗口后、正式Claim调用前的同步时钟边界，不冒充SQL事务内竞态。
            That(battle.View is {Phase:0,State:AntonRaidBattleState.Reward},"领取边界确实已提交同phase Reward");
            var candidate=store.Candidate!;
            That(battle.ConfirmReward(actor,new(battle.Instance,0,0,0,actor.Character,policies[0].Source,[new(100,1)],false,candidate.Week)
                {RaidType=RaidKind.Luke}),"显式领域确认既有奖励义务");
            That(battle.AdvanceAfterRewards(battle.Instance,battle.View.Revision)?.After.Phase==1,"领取开始边界沿正常奖励屏障进入下一phase");
        });
        var commands=new GameCommandRegistry();
        new RaidCombatHandler(null!,null!,null!,null!,null!,log,clock,store).Register(commands); commands.Seal();
        try
        {
            var identity=new DungeonRunIdentity(actor.Character,0,1,Guid.NewGuid(),1);
            using(var reservation=battle.ReserveDungeon(new(battle.Instance,0,1,RaidKind.Luke),10,[(actor,identity)])!) reservation.Install();
            That(battle.CompleteLukeDungeon([(actor,new DungeonClearFact(identity,10,[Guid.NewGuid()])
                {ClearMap=new DungeonMap("boss",new(0,0),77,"fixture/rejection-boss.map")})]) is not null,"真实绑定Run密封阶段候选");
            battle.ReleaseDungeon(actor,identity.InstanceId);
            That(battle.View is {Phase:0,State:AntonRaidBattleState.Fighting}&&battle.PendingLukeSettlement() is not null,
                "请求前等待资格提交，不手工设置Reward");
            long epoch=actor.Session.SelectionEpoch;
            using(sender.QueuePublication(()=>actor.Owner.IsCurrent&&actor.Session.SelectionEpoch==epoch))
                That(await commands.DispatchAsync(context,new Packet(1,660,[0,7]))==CommandResult.Handled,"合法kind未配置card走正式领取null路径");
            await sender.DrainAsync().WaitAsync(TimeSpan.FromSeconds(3));
            That(store.Calls==1&&battle.PendingLukeSettlement() is null,"该请求确实完成资格回执安装");
            if(scenario=="settlement-same-phase")
            {
                That(battle.View is {Phase:0,State:AntonRaidBattleState.Reward},"同阶段由Fighting提交到Reward");
                That(stream.Frames.Count==1,"本请求提交同阶段后仍写出合法拒绝");
                Bytes(stream.Frames[0],GameFrame.Build(1,660,0,[0,0x12,0]),"同phase提交后的660失败正文");
            }
            else That(battle.View.Phase==1&&stream.Frames.Count==0,"跨phase旧请求不能重新绑定新窗口失败");
            int priorFrames=stream.Frames.Count;
            using(sender.QueuePublication(()=>actor.Owner.IsCurrent&&actor.Session.SelectionEpoch==epoch))
                That(await commands.DispatchAsync(context,new Packet(1,660,[2,0]))==CommandResult.Handled,"当前窗口新请求可正常拒绝");
            await sender.DrainAsync().WaitAsync(TimeSpan.FromSeconds(3));
            That(stream.Frames.Count==priorFrames+1,"资格提交后的新请求确实新增一帧");
            Bytes(stream.Frames[^1],GameFrame.Build(1,660,(ushort)(stream.Frames.Count-1),[0,0x12,0]),"当前请求不被永久吞掉");
            That(sender.PendingBytes==0,"资格提交路径释放发送预算");
        }
        finally { actor.Session.DetachRaidCombatStart(actor,battle.Instance); }
    }
    // 仅构造领域资格回执，不代表真实SQL已提交；资产、持久化另有事务测试。
    sealed class SettlementStore(bool eligible):ILukeRaidLifecycleStore
    {
        public int Calls {get;private set;}
        public AntonRaidBattle.LukePhaseSettlement? Candidate {get;private set;}
        public LukePhaseCommit CommitLukePhase(AntonRaidBattle.LukePhaseSettlement candidate)
        {
            Calls++; Candidate=candidate;
            return new(candidate.Members.Select(member=>new RaidPhaseEligibility(candidate.Instance,RaidKind.Luke,candidate.Phase,
                candidate.Event,candidate.Anchor.ToUnixTimeSeconds(),member.Character,false,eligible,"fixture/domain",
                candidate.Policy.Rewards.Source,candidate.Week,candidate.Policy.Rewards.Offers)).ToImmutableArray());
        }
    }
    sealed class CallbackClock(Action callback):TimeProvider
    {
        public override DateTimeOffset GetUtcNow() { callback(); return DateTimeOffset.UtcNow; }
    }

}
