using DfoServer.Game.Characters;
using DfoServer.GameWorld.Dungeons;
using DfoServer.Game.Guild;
using System.Collections.Immutable;

namespace DfoServer.Game.Dungeons;

internal sealed record DungeonSettlementPreparation(DungeonRunOutcome Outcome, PreparedDungeonSettlement? Pending = null);
internal sealed record DungeonSettlementCommit(Character Character, bool Duplicate, DungeonClearExperienceReward Reward)
{
    public DungeonCardReward? Card { get; init; }
    public DfoServer.Game.Inventory.Models.InventoryPlan? Inventory { get; init; }
    public uint? MailId { get; init; }
    public uint DailyAttendanceMailId { get; init; }
    public DungeonCardOptions? Options { get; init; }
    public DungeonCardReward? BlackDiamond { get; init; }
    public uint GainedExperience { get; init; }
    // 同一笔通关经验记入熟练度的额度；与GainedExperience之和是本次有效经验，重复报告按回执同值回放。
    public uint ProficiencyGained { get; init; }
    public bool LuckyStarGained { get; init; }
    public int LuckyStarTotal { get; init; }
    public uint GuildMileageGuildId { get; init; }
    public GuildContributionCommit? GuildContribution { get; init; }
    public GuildDungeonProgress? GuildDungeon { get; init; }
    public DfoServer.Game.Adventure.AdventureProgressionCommit? Adventure { get; init; }
    public GriefTowerReward? GriefReward { get; init; }
    public BossRushClearReward? BossRushReward { get; init; }
    public int EndlessAdventureAmount { get; init; }
}

// 冻结首份报告与经验候选；只有原实例保存的同一对象能取得提交资格。
internal sealed class PreparedDungeonSettlement(DungeonClearFact fact, ReadOnlySpan<byte> reportBody,
    DungeonClearExperienceReward reward, int mapId, DungeonCardReward? card = null, DungeonCardOptions? options = null,
    DfoServer.Game.Inventory.Models.InventoryPlan? creatureInventory = null, GriefTowerClearReward? grief = null,
    BossRushClearReward? bossRush = null)
{
    private readonly byte[] report = reportBody.ToArray();
    public Guid EventId { get; } = Guid.NewGuid();
    public DfoServer.Game.Events.EndlessAdventureClear? EndlessAdventure { get; private set; }
    bool endlessAdventureFrozen;
    public void FreezeEndlessAdventure(DfoServer.Game.Events.EndlessAdventureClear? reward)
    {
        if (endlessAdventureFrozen) return;
        EndlessAdventure = reward;
        endlessAdventureFrozen = true;
    }
    public DungeonClearFact Fact { get; } = fact;
    public ReadOnlySpan<byte> ReportBody => report;
    public DungeonClearExperienceReward Reward { get; } = reward;
    public int MapId { get; } = mapId;
    public DungeonCardReward? Card { get; } = card;
    public DungeonCardOptions? Options { get; } = options;
    public DfoServer.Game.Inventory.Models.InventoryPlan? CreatureInventory { get; } = creatureInventory;
    public GriefTowerClearReward? Grief { get; } = grief;
    public BossRushClearReward? BossRush { get; } = bossRush;
    public DfoServer.Game.Events.DailyAttendancePlan? DailyAttendance { get; private set; }
    bool dailyAttendanceFrozen;
    public void FreezeDailyAttendance(DfoServer.Game.Events.DailyAttendancePlan? plan)
    {
        if (dailyAttendanceFrozen) return;
        DailyAttendance = plan;
        dailyAttendanceFrozen = true;
    }
    public DungeonTournamentReward? Tournament { get; private set; }
    public void FreezeTournament(DungeonTournamentReward? reward)
    {
        if (reward is null) return;
        if (Tournament is not null && !ReferenceEquals(Tournament, reward))
            throw new InvalidOperationException("同一大会不能替换原冻结牌面");
        Tournament = reward;
    }
    public DfoServer.Game.BattlePass.BattlePassTayberrsClear? BattlePassTayberrsClear { get; private set; }
    public void FreezeBattlePassTayberrsClear(DungeonDefinition definition, DungeonEntryPlan entry, TimeProvider clock)
    {
        BattlePassTayberrsClear ??= DfoServer.Game.BattlePass.BattlePassTayberrsClear.Prepare(definition, entry, Fact, clock);
    }
    public DfoServer.Game.BattlePass.BattlePassFormalClear? BattlePassFormalClear { get; private set; }
    public void FreezeBattlePassFormalClear(DungeonDefinition definition, DungeonEntryPlan entry, int reviveCoinsSpent, TimeProvider clock,
        byte? finalRankGrade = null, byte minimumSRankGrade = 0)
    {
        BattlePassFormalClear ??= DfoServer.Game.BattlePass.BattlePassFormalClear.Prepare(definition, entry, Fact, reviveCoinsSpent, clock, finalRankGrade, minimumSRankGrade);
    }
    public DfoServer.Game.BattlePass.BattlePassDazzlementClear? BattlePassDazzlementClear { get; private set; }
    public void FreezeBattlePassDazzlementClear(DungeonDefinition definition, DungeonEntryPlan entry,
        DazzlementTowerAdmission? admission, TimeProvider clock)
    {
        BattlePassDazzlementClear ??= DfoServer.Game.BattlePass.BattlePassDazzlementClear.Prepare(definition,entry,Fact,admission,clock);
    }
    public DfoServer.Game.BattlePass.BattlePassBossRushReward? BattlePassBossRushReward { get; private set; }
    public void FreezeBattlePassBossRushReward(DungeonDefinition definition, DungeonEntryPlan entry,
        BossRushRunState? rush, TimeProvider clock)
    {
        BattlePassBossRushReward ??= DfoServer.Game.BattlePass.BattlePassBossRushReward.Prepare(definition,entry,Fact,rush,BossRush,clock);
    }
    public int? EkernDay { get; private set; }
    public void FreezeEkernDay(int day) => EkernDay ??= day;
    public AncientGateClear? AncientGate { get; private set; }
    public void FreezeAncientGate(AncientGateClear? value)
    {
        if (AncientGate is not null) return;
        AncientGate = value;
    }
    public ImmutableArray<GuildDungeonMembership> GuildMemberships { get; private set; }
    public GuildContributionSource? GuildContributionSource { get; private set; }
    public DfoServer.GameWorld.Guilds.GuildDungeonCatalog? GuildDungeonCatalog { get; private set; }
    public GuildDungeonClearReward? GuildDungeonReward { get; private set; }
    public void FreezeGuildContribution(ImmutableArray<GuildDungeonMembership> memberships, GuildContributionSource? source,
        DfoServer.GameWorld.Guilds.GuildDungeonCatalog? dungeonCatalog = null, GuildDungeonClearReward? dungeonReward = null)
    {
        if (!GuildMemberships.IsDefault) return;
        GuildMemberships = memberships; GuildContributionSource = source;
        if (source == Game.Guild.GuildContributionSource.GuildDungeon)
        {
            GuildDungeonCatalog = dungeonCatalog;
            GuildDungeonReward = dungeonReward;
        }
    }
}
