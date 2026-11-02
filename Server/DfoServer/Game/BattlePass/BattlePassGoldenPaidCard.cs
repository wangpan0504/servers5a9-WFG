using System.Security.Cryptography;
using System.Text;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.BattlePass;

namespace DfoServer.Game.BattlePass;

// 黄金卡牌对应原Paid侧；免费牌和黑钻附赠牌不调用本来源。
internal sealed class BattlePassGoldenPaidCard
{
    public const string SourceKind="golden_paid_card";
    public DungeonRunIdentity Identity { get; }
    public DateTimeOffset At { get; }
    public string SourceId { get; }
    BattlePassGoldenPaidCard(PreparedDungeonSettlement prepared,DateTimeOffset at)
    {
        Identity=prepared.Fact.Identity;At=at;
        string identity=FormattableString.Invariant($"{prepared.EventId:N}:{Identity.InstanceId:N}:{Identity.CharacterId}:{Identity.QuestId}:{Identity.TaskAttempt}:{Identity.Epoch}");
        SourceId=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(SourceKind+":"+identity)).AsSpan(0,16)).ToLowerInvariant();
    }
    public static BattlePassGoldenPaidCard? Prepare(PreparedDungeonSettlement prepared,ushort actor,DateTimeOffset at)
    {
        if(prepared.Options is not { PaidDisabled:false })return null;
        if(prepared.EventId==Guid.Empty || prepared.Fact.Identity.InstanceId==Guid.Empty || actor==0 ||
            prepared.Fact.Identity.CharacterId!=actor || prepared.Fact.DungeonId==0 || at.ToUnixTimeSeconds()<0)
            throw new InvalidDataException("黄金付费牌来源与原通关角色实例不一致");
        return new(prepared,at);
    }
    public static bool MatchesMission(byte category,BattlePassMission mission)=>mission.RawTypeCode==22 && mission.RawParameter==1 &&
        (category==0 && mission is { Id:22,RequiredCount:2,Experience:100 } ||
         category==1 && mission is { Id:33,RequiredCount:6,Experience:1000 });
}
