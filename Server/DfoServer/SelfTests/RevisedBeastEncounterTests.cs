using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Dungeons;
using static DfoServer.Testing.TestCheck;
namespace DfoServer;
internal static class RevisedBeastEncounterTests
{
    internal static void RunCore()
    {
        int before=Count;
        var (run,authority)=Create();
        LoadToBoss(run);
        That(run.Snapshot.LivingMonsters.SequenceEqual(new ushort[]{10,11}),"改版初态同时建立安妮和歇罗供原生脚本查找，铜像不占对象");
        That(run.CurrentLivingMonster(10) is not null && run.CurrentLivingMonster(11) is null,"已部署的歇罗在安妮死亡前不取得服务端击杀资格");
        var initialIdentity=run.Snapshot.Identity;
        var initialPayload=run.Snapshot.MapPayload;
        That(run.PrepareConfirmedDeath(authority.Death(run.Snapshot.Identity,11,1)).Outcome == DungeonRunOutcome.Rejected,"拒绝跳过安妮击杀歇罗");
        var anne=run.PrepareConfirmedDeath(authority.Death(run.Snapshot.Identity,10,1)).Pending!;
        That(run.Snapshot.LivingMonsters.SequenceEqual(new ushort[]{10,11}) && run.CurrentLivingMonster(11) is null,
            "准备死亡不提前开放歇罗");
        That(run.CancelUncommittedDeath(anne) && run.CurrentLivingMonster(11) is null,"回滚安妮死亡不会提前放行歇罗");
        anne=run.PrepareConfirmedDeath(authority.Death(run.Snapshot.Identity,10,1)).Pending!;
        That(run.ConfirmCommittedDeath(anne).Outcome == DungeonRunOutcome.Accepted && run.Phase == SinglePlayerDungeonPhase.Dungeon &&
            run.Snapshot.Identity == initialIdentity && run.Snapshot.MapPayload == initialPayload &&
            run.Snapshot.LivingMonsters.SequenceEqual(new ushort[]{11}) && run.CurrentLivingMonster(11)?.ObjectKey == 11,
            "安妮提交死亡后保留原房间epoch与歇罗对象，无重新加载握手");
        That(run.ConfirmCommittedDeath(anne).Outcome == DungeonRunOutcome.Duplicate && run.RevisedBeastTerminal is null,"重复死亡不触发终局");
        That(run.CompleteLoading(run.Snapshot.Identity,Guid.NewGuid()).Outcome != DungeonRunOutcome.Accepted,
            "交接不开放第二次加载事务");
        var hierro=run.PrepareConfirmedDeath(authority.Death(run.Snapshot.Identity,11,2)).Pending!;
        run.ConfirmCommittedDeath(hierro);
        That(run.RevisedBeastTerminal is {Success:true,Bosses.Count:2} && run.ClearFact is null && run.Phase == SinglePlayerDungeonPhase.Settlement,
            "最终死亡产生专用终局，不进入普通翻牌链");
        That(!run.FinishRevisedBeastOnDeathTimeout(),"胜利后超时不能改写终局");
        var (failed,failedAuthority)=Create(); LoadToBoss(failed);
        That(!failed.FinishRevisedBeastOnDeathTimeout() && failed.RevisedBeastTerminal is null,
            "安妮前死亡不产生魔兽终局");
        failed.AcceptConfirmedDeath(failedAuthority.Death(failed.Snapshot.Identity,10,1));
        That(failed.FinishRevisedBeastOnDeathTimeout() && failed.RevisedBeastTerminal is {Success:false} &&
            !failed.FinishRevisedBeastOnDeathTimeout(),"死亡倒计时终局唯一且不伪造击杀");
        var original=failed.RevisedBeastTerminal;
        That(failed.CurrentLivingMonster(10) is null && ReferenceEquals(original,failed.RevisedBeastTerminal),"终局后不可继续战斗");
        var (native,nativeAuthority)=Create(nativeOrder:true); LoadToBoss(native);
        That(native.CurrentLivingMonster(10) is null && native.CurrentLivingMonster(11)?.MonsterId==66159,
            "原生MAP歇罗排在首行时，击杀顺序仍按安妮身份判断，不按资源行顺序");
        var nativeAnne=native.PrepareConfirmedDeath(nativeAuthority.Death(native.Snapshot.Identity,11,1)).Pending!;
        native.ConfirmCommittedDeath(nativeAnne);
        That(native.CurrentLivingMonster(10)?.MonsterId==107005374 && native.RevisedBeastTerminal is null,
            "原生行序交接后放行原key的歇罗，未提前终局");
        VerifyResult(run,true);
        VerifyResult(failed,false);
        Console.WriteLine($"PASS {Count-before} 改版魔兽顺序与终局断言");
    }
    static void VerifyResult(SinglePlayerDungeonRun run,bool success)
    {
        var character=new DfoServer.Game.Characters.Character(99,0,"BeastWire",0,new(51,1,0,0),null);
        var scene=new FormalDungeonReceipt(character,1,1,run.Entry,run.Snapshot,
            new(new(run.Snapshot.CurrentMap,[],[],[]),[]),1);
        var commit=new RevisedBeastRewardCommit(character,false,true,success,
            success?[new(10305183,1)]:[],[],false);
        var receipt=new RevisedBeastSettlementReceipt(scene,run.RevisedBeastTerminal!,commit)
            { DisplayRewards=success?[new(10305186,1),new(10305183,1)]:[new(10305186,1)], ElapsedSeconds=125 };
        var bytes=DfoServer.Network.Builders.RevisedBeastPackets.Result(receipt);
        byte[] expected=success ?
            [0,0,0,0,1,1,0,2,2,63,157,0,1,0,0,0,255,62,157,0,1,0,0,0,0,0,0,0,0] :
            [0,0,0,0,0,1,0,1,2,63,157,0,1,0,0,0];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(expected,125);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(8),10305186);
        if(success) System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(16),10305183);
        That(bytes.SequenceEqual(expected),"成功展示安妮与歇罗两卡、失败只展示安妮卡；展示不等于本次新增资产");
    }

    internal static (SinglePlayerDungeonRun Run,Authority Authority) Create(ushort character=99,long attempt=1,byte difficulty=0,bool nativeOrder=false)
    {
        var start=new DungeonMap("map",new(0,0),1,"map/start.map");
        var boss=new DungeonMap("boss",new(1,0),2,"map/boss.map");
        var maze=new DungeonMaze(0,2,1,"BBEE",[start.Position],[boss.Position],[start,boss],[]);
        var rules=new RevisedBeastEncounter(66159,"monster/anne.mob",107005374,"monster/hierro.mob",10305186,10305183,3,10000,2,4,3,6);
        var entry=new DungeonEntryPlan(character,0,5335,0,255,difficulty,maze,start,boss)
            {IsRevisedBeast=true,RevisedBeastEncounter=rules,DisableClearReward=true,DisableMonsterAndClearExperience=true};
        DungeonRoomMonster Actor(int id,string path)=>new(id,new[]{id,0,120,100,100,0,1,1},"[fixed]","[boss]",path);
        var startPlan=DungeonMonsterPlan.FromRoom(new(start,[],[],[]),character,1);
        var bossPlan=DungeonMonsterPlan.FromRoom(new(boss,nativeOrder ? [Actor(rules.HierroId,rules.HierroPath),Actor(rules.AnneId,rules.AnnePath)] : [Actor(rules.AnneId,rules.AnnePath),Actor(rules.HierroId,rules.HierroPath)],[],[]),character,10);
        var route=new DungeonFixedRoute([startPlan,bossPlan],[new(start.Position,boss.Position)]);
        var authority=new Authority();
        That(SinglePlayerDungeonRun.TryCreate(entry,attempt,100,route,authority,out var run,out var reason),"建立改版测试路线："+reason);
        return (run!,authority);
    }
    internal static void LoadToBoss(SinglePlayerDungeonRun run)
    {
        run.CompleteLoading(run.Snapshot.Identity,Guid.NewGuid());
        That(run.Move(run.Snapshot.Identity,Guid.NewGuid(),new(1,0)).Outcome == DungeonRunOutcome.Accepted,"空首房可进入遭遇房");
        run.CompleteLoading(run.Snapshot.Identity,Guid.NewGuid());
    }
    internal sealed class Authority : DungeonCombatAuthority
    {
        internal ConfirmedDungeonMonsterDeath Death(DungeonRunIdentity identity,ushort key,long seq)=>ConfirmDeath(identity,Guid.NewGuid(),key,seq);
    }
}

