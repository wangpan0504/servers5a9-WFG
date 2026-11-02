using System.Security.Cryptography;
using System.Text;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.BattlePass;
using DfoServer.GameWorld.Dungeons;

namespace DfoServer.Game.BattlePass;

// 整次完成只从原希望终末通关与专用领奖规划取得，房间死亡或普通任务完成不替代此事实。
internal sealed class BattlePassOperationHopeCompletion
{
    readonly OperationHopeRewardPlan plan;
    readonly OperationHopeCatalog catalog;
    readonly SinglePlayerDungeonRun run;
    public TimeProvider Clock { get; }
    public DateTimeOffset PlannedAt => plan.FrozenAt;
    public string SourceId { get; }
    private BattlePassOperationHopeCompletion(OperationHopeRewardPlan plan, OperationHopeCatalog catalog,
        SinglePlayerDungeonRun run, TimeProvider clock)
    {
        this.plan = plan; this.catalog = catalog; this.run = run; Clock = clock;
        SourceId = Identity(plan.Identity, plan.Binding.MazeOrdinal, plan.AcceptanceId);
    }

    public static BattlePassOperationHopeCompletion? Prepare(OperationHopeRewardPlan plan,
        OperationHopeCatalog catalog, SinglePlayerDungeonRun run, TimeProvider clock)
    {
        var active = plan.Expected.ActiveMainQuests.SingleOrDefault(row => row.AcceptanceId == plan.AcceptanceId);
        return active is null || plan.Identity != run.Snapshot.Identity ||
            !OperationHopePlan.CanClaim(catalog, plan.Binding, run, active)
            ? null : new(plan, catalog, run, clock);
    }

    public bool Matches(OperationHopeRewardPlan candidate) => ReferenceEquals(plan, candidate) &&
        plan.Identity == run.Snapshot.Identity && plan.Expected.ActiveMainQuests.SingleOrDefault(row => row.AcceptanceId == plan.AcceptanceId) is { } active &&
        OperationHopePlan.CanClaim(catalog, plan.Binding, run, active) &&
        SourceId == Identity(plan.Identity, plan.Binding.MazeOrdinal, plan.AcceptanceId);

    public static bool MatchesMission(byte category, BattlePassMission definition) => category == 1 &&
        definition is { Id: 49, RawTypeCode: 8, RawParameter: 81, RequiredCount: 3, Experience: 1000 };

    public static long NextCount(byte category, BattlePassMission definition, long current)
    {
        if (!MatchesMission(category, definition) || current < 0 || current > definition.RequiredCount)
            throw new InvalidDataException("希望整次完成任务定义或原进度无效");
        return Math.Min(definition.RequiredCount, checked(current + 1));
    }

    internal static string Identity(DungeonRunIdentity identity, int mazeOrdinal, Guid acceptance)
    {
        if (identity.InstanceId == Guid.Empty || identity.CharacterId == 0 || identity.TaskAttempt <= 0 ||
            identity.Epoch <= 0 || identity.QuestId != 0 || acceptance == Guid.Empty || mazeOrdinal < 0)
            throw new InvalidDataException("希望整次完成缺少原实例与接取身份");
        string text = FormattableString.Invariant($"operation_hope_completion:{identity.InstanceId:N}:{identity.CharacterId}:{identity.TaskAttempt}:{identity.Epoch}:{mazeOrdinal}:{acceptance:N}");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)).AsSpan(0, 16)).ToLowerInvariant();
    }
}
