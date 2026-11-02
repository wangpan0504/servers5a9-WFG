using System.Security.Cryptography;
using System.Text;
using DfoServer.Game.TagEvildom;
using DfoServer.GameWorld.BattlePass;

namespace DfoServer.Game.BattlePass;

// 只消费原再战魔界真实终点的完整冻结计划，准入、切角和普通结算均不能代替通关。
internal sealed class BattlePassTagEvildomCompletion
{
    public const string SourceKind = "tag_evildom_completion";
    public DateTimeOffset At { get; }
    public string SourceId { get; }

    public BattlePassTagEvildomCompletion(TagEvildomClearPlan plan)
    {
        plan.Validate();
        var identity = plan.Fact.Identity;
        At = DateTimeOffset.FromUnixTimeSeconds(plan.Created);
        string key = FormattableString.Invariant($"{identity.InstanceId:N}:{plan.Fact.EventId:N}:{identity.CharacterId}:{identity.TaskAttempt}:{identity.Epoch}");
        SourceId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(SourceKind + ":" + key)).AsSpan(0, 16)).ToLowerInvariant();
    }

    // 原任务定义仍是四次，不能借玩法周三次上限改写调度资源。
    public static bool MatchesMission(byte category, BattlePassMission mission) => category == 1 && mission is
        { Id: 36, RawTypeCode: 35, RawParameter: -1, RequiredCount: 4, Experience: 1000 };
}
