using DfoServer.Game.Characters;
using DfoServer.GameWorld.Dungeons;

namespace DfoServer.Game.Dungeons;

internal sealed record DungeonSettlementPreparation(DungeonRunOutcome Outcome, PreparedDungeonSettlement? Pending = null);
internal sealed record DungeonSettlementCommit(Character Character, bool Duplicate, DungeonClearExperienceReward Reward);

// 冻结首份报告与经验候选；只有原实例保存的同一对象能取得提交资格。
internal sealed class PreparedDungeonSettlement(DungeonClearFact fact, ReadOnlySpan<byte> reportBody,
    DungeonClearExperienceReward reward, int mapId)
{
    private readonly byte[] report = reportBody.ToArray();
    public Guid EventId { get; } = Guid.NewGuid();
    public DungeonClearFact Fact { get; } = fact;
    public ReadOnlySpan<byte> ReportBody => report;
    public DungeonClearExperienceReward Reward { get; } = reward;
    public int MapId { get; } = mapId;
}
