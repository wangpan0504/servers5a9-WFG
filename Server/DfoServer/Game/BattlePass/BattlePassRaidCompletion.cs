using System.Security.Cryptography;
using System.Text;
using DfoServer.Game.Raids;
using DfoServer.GameWorld.BattlePass;

namespace DfoServer.Game.BattlePass;

// 只消费原正式攻坚最终事务已经确认的计次资格，不用领奖资格替代整场通关。
internal sealed class BattlePassRaidCompletion
{
    public const string SourceKind = "raid_completion";
    public string Account { get; }
    public ushort Character { get; }
    public RaidKind RaidType { get; }
    public DateTimeOffset At { get; }
    public string SourceId { get; }
    BattlePassRaidCompletion(AntonRaidBattle.LukePhaseSettlement candidate, LukePhaseMember member)
    {
        Account=member.Account;Character=member.Character;RaidType=candidate.RaidType;At=candidate.Anchor;
        string identity=FormattableString.Invariant($"{candidate.Instance:N}:{(byte)candidate.RaidType}:{candidate.Event:N}:{member.Character}");
        SourceId=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(SourceKind+":"+identity)).AsSpan(0,16)).ToLowerInvariant();
    }
    public static BattlePassRaidCompletion? Prepare(AntonRaidBattle.LukePhaseSettlement candidate, RaidPhaseEligibility receipt)
    {
        if(!candidate.FinalPhase || !receipt.CountsAsClear || candidate.RaidType is not (RaidKind.Luke or RaidKind.Fiendwar or RaidKind.Prey))return null;
        // 当前普雷正式来源只确认原引导聚合，不能借类型相同扩到独立普通模式。
        if(candidate.RaidType==RaidKind.Prey && candidate.Source!=PreyGuideRewards.Source)return null;
        int finalPhase=candidate.RaidType==RaidKind.Fiendwar?0:1;
        var member=candidate.Members.SingleOrDefault(row=>row.Character==receipt.Character);
        if(candidate.Instance==Guid.Empty || candidate.Event==Guid.Empty || candidate.Phase!=finalPhase ||
            member is not { Present:true,Character:>0 } || string.IsNullOrWhiteSpace(member.Account) ||
            receipt.Instance!=candidate.Instance || receipt.RaidType!=candidate.RaidType || receipt.Phase!=candidate.Phase ||
            receipt.Event!=candidate.Event || receipt.Anchor!=candidate.Anchor.ToUnixTimeSeconds() || receipt.Week!=candidate.Week ||
            receipt.Source!=candidate.RewardsFor(member.Character).Source)
            throw new InvalidDataException("攻坚通关来源与原最终计次回执不一致");
        return new(candidate,member);
    }
    public static bool MatchesMission(RaidKind kind, byte category, BattlePassMission mission)=>kind switch
    {
        RaidKind.Luke=>category==1 && mission is {Id:46,RawTypeCode:32,RawParameter:-1,RequiredCount:3,Experience:1000},
        RaidKind.Fiendwar=>category==2 && mission is {Id:58,RawTypeCode:33,RawParameter:-1,RequiredCount:20,Experience:1600},
        RaidKind.Prey=>category==2 && mission is {Id:59,RawTypeCode:34,RawParameter:-1,RequiredCount:20,Experience:1600},
        _=>false
    };
}
