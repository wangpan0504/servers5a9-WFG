using System.Collections.Immutable;

namespace DfoServer.Game.Pvp;

// 1287其余统计字段保留原值；不能拿未知统计值当奖励数量、时间或复活扣费依据。
internal sealed record PvpArcadeRoundReport(byte Event, int Opponent, byte TotalRounds, byte Round,
    uint Metric0, byte Flag0, bool Won, ushort Metric1, uint Metric2);

// 只接收资源层已解析的每轮允许对手；不把客户端自报APC扩充为允许集合。
// 输局不推进轮次；是否可以再次开始还须由正式实例的复活/退出规则验证。
internal sealed record PvpArcadeRoundState(int Round, int Opponent, bool Running, bool Complete)
{
    public ImmutableHashSet<int> UsedRandom { get; init; }=[];
    public static PvpArcadeRoundState Initial { get; }=new(1,0,false,false);
    public PvpArcadeRoundState? Apply(PvpArcadeRoundReport report, ImmutableArray<ImmutableHashSet<int>> allowed, ImmutableHashSet<int>? randomRounds=null)
    {
        if(Complete || allowed.IsDefaultOrEmpty || allowed.Length>255 || report.TotalRounds!=allowed.Length ||
            report.Round!=Round || Round<1 || Round>allowed.Length || report.Opponent<=0 ||
            !allowed[Round-1].Contains(report.Opponent)) return null;
        if(report.Event==0)
        {
            bool random=randomRounds?.Contains(Round)==true;
            if(Running || report.Won || Opponent!=0 && Opponent!=report.Opponent ||
                random && Opponent==0 && UsedRandom.Contains(report.Opponent)) return null;
            return this with {Opponent=report.Opponent,Running=true,
                UsedRandom=random ? UsedRandom.Add(report.Opponent) : UsedRandom};
        }
        if(report.Event!=1 || !Running || Opponent!=report.Opponent) return null;
        if(!report.Won) return this with {Running=false};
        return Round==allowed.Length ? this with {Running=false,Complete=true} : new(Round+1,0,false,false) {UsedRandom=UsedRandom};
    }
}
