using System.Collections.Immutable;
using DfoServer.Game.Pvp;
using DfoServer.Network.Parsers;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class PvpArcadeRoundTests
{
    public static void Run()
    {
        int before=Count;
        byte[] bytes=[0,0xB1,4,0,0,2,1,0x78,0x56,0x34,0x12,7,0,0x34,0x12,4,3,2,1];
        var report=PvpTrainingRequests.Round(bytes);
        That(report is {Event:0,Opponent:1201,TotalRounds:2,Round:1,Metric0:0x12345678,Flag0:7,Won:false,Metric1:0x1234,Metric2:0x01020304},
            "1287独立19字节金样保留全部字段且不重命名未知统计");
        Reject<InvalidDataException>(()=>PvpTrainingRequests.Round(bytes[..^1]),"1287截断拒绝");
        Reject<InvalidDataException>(()=>PvpTrainingRequests.Round([..bytes,0]),"1287尾部拒绝");
        var bad=(byte[])bytes.Clone();bad[0]=2;
        Reject<InvalidDataException>(()=>PvpTrainingRequests.Round(bad),"未知回合事件拒绝");
        bad=(byte[])bytes.Clone();bad[12]=1;
        Reject<InvalidDataException>(()=>PvpTrainingRequests.Round(bad),"开始包不能声明胜利");
        ImmutableArray<ImmutableHashSet<int>> allowed=[ImmutableHashSet.Create(1201),ImmutableHashSet.Create(1202,1203)];
        var initial=PvpArcadeRoundState.Initial;
        That(initial.Apply(report with {Event=1,Won=true},allowed) is null,"缺少已开始事实不能结算");
        That(initial.Apply(report with {Round=2},allowed) is null && initial.Apply(report with {TotalRounds=1},allowed) is null &&
            initial.Apply(report with {Opponent=9999},allowed) is null,"跳轮、篡改总轮数及目录外对手不能推进");
        var first=initial.Apply(report,allowed)!;
        That(first.Running && first.Apply(report,allowed) is null,"已开始回合不能重复开始");
        That(first.Apply(report with {Event=1,Won=true,Opponent=1202},allowed) is null,"结算不得换对手");
        var failed=first.Apply(report with {Event=1},allowed)!;
        That(failed.Round==1 && !failed.Complete && !failed.Running,"输局不跳下一轮，不制造完成事实");
        var second=first.Apply(report with {Event=1,Won=true},allowed)!;
        That(second.Round==2 && !second.Complete && second.Apply(report with {Event=1,Won=true},allowed) is null,
            "胜利只推进一轮，旧结算重放拒绝");
        var next=report with {Round=2,Opponent=1203};
        var finish=second.Apply(next,allowed)!.Apply(next with {Event=1,Won=true},allowed)!;
        That(finish.Complete && !finish.Running && finish.Apply(next,allowed) is null &&
            finish.Apply(next with {Event=1,Won=true},allowed) is null,"全轮完成只能产生一次边沿，拒绝后续重放");
        ImmutableArray<ImmutableHashSet<int>> randomPool=[ImmutableHashSet.Create(1201,1202),ImmutableHashSet.Create(1201,1202)];
        var randomRounds=ImmutableHashSet.Create(1,2);
        var randomStart=initial.Apply(report,randomPool,randomRounds)!;
        var randomLost=randomStart.Apply(report with {Event=1},randomPool,randomRounds)!;
        That(randomLost.Apply(report with {Opponent=1202},randomPool,randomRounds) is null &&
            randomLost.Apply(report,randomPool,randomRounds) is not null,"同轮失败重开不重抽客户端预选对手");
        var randomNext=randomStart.Apply(report with {Event=1,Won=true},randomPool,randomRounds)!;
        That(randomNext.Apply(report with {Round=2},randomPool,randomRounds) is null &&
            randomNext.Apply(report with {Round=2,Opponent=1202},randomPool,randomRounds) is not null,
            "随机池弹出不放回，跨轮重复APC不能构造胜利");
        var fixedNext=initial.Apply(report,randomPool,[])!.Apply(report with {Event=1,Won=true},randomPool,[])!;
        That(fixedNext.Apply(report with {Round=2},randomPool,[]) is not null,"原资源固定轮次可重复，不误套随机唯一约束");
        Console.WriteLine($"PASS {Count-before} 街机回合原始报告与顺序状态断言（未直接发放任务奖励）");
    }
}
