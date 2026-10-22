using DfoServer.Game.Characters;
using DfoServer.GameWorld.Dungeons;

namespace DfoServer.Game.Dungeons;

internal sealed record DungeonSettlementPreparation(DungeonRunOutcome Outcome, PreparedDungeonSettlement? Pending = null);
internal sealed record DungeonSettlementCommit(Character Character, bool Duplicate, DungeonClearExperienceReward Reward)
{
    public DungeonCardReward? Card { get; init; }
    public DfoServer.Game.Inventory.Models.InventoryPlan? Inventory { get; init; }
    public uint? MailId { get; init; }
    public DungeonCardOptions? Options { get; init; }
    public DungeonCardReward? BlackDiamond { get; init; }
    public uint GainedExperience { get; init; }
    // 同一笔通关经验记入熟练度的额度；与GainedExperience之和是本次有效经验，重复报告按回执同值回放。
    public uint ProficiencyGained { get; init; }
    public bool LuckyStarGained { get; init; }
    public int LuckyStarTotal { get; init; }
    public DfoServer.Game.Adventure.AdventureProgressionCommit? Adventure { get; init; }
}

// 冻结首份报告与经验候选；只有原实例保存的同一对象能取得提交资格。
internal sealed class PreparedDungeonSettlement(DungeonClearFact fact, ReadOnlySpan<byte> reportBody,
    DungeonClearExperienceReward reward, int mapId, DungeonCardReward? card = null, DungeonCardOptions? options = null,
    DfoServer.Game.Inventory.Models.InventoryPlan? creatureInventory = null)
{
    private readonly byte[] report = reportBody.ToArray();
    public Guid EventId { get; } = Guid.NewGuid();
    public DungeonClearFact Fact { get; } = fact;
    public ReadOnlySpan<byte> ReportBody => report;
    public DungeonClearExperienceReward Reward { get; } = reward;
    public int MapId { get; } = mapId;
    public DungeonCardReward? Card { get; } = card;
    public DungeonCardOptions? Options { get; } = options;
    public DfoServer.Game.Inventory.Models.InventoryPlan? CreatureInventory { get; } = creatureInventory;
}
