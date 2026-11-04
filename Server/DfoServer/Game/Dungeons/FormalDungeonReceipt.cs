using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Currency;
using System.Collections.Immutable;

namespace DfoServer.Game.Dungeons;

internal sealed record FormalDungeonReceipt(Character Character, long Generation, long SelectionEpoch,
    DungeonEntryPlan Entry, DungeonRunSnapshot Snapshot, DungeonMonsterPlan Monsters, uint MapSeed)
{
    // 房间加载前的身份在本次访问内保持不变；业务Epoch仍随加载、死亡等提交推进。
    public DungeonRunIdentity? RoomLoadingIdentity { get; init; }
    public TimeProvider? BattlePassMissionClock { get; init; }
    public bool LostLandStarted { get; init; }
    public ImmutableArray<ushort> PartyActors { get; init; } = [];
    public ImmutableArray<DfoServer.Game.Guild.GuildDungeonMembership> GuildMemberships { get; init; } = [];
    public byte PartySlot { get; init; }
    public bool HellPartyRoomCleared => Snapshot.Phase == SinglePlayerDungeonPhase.Dungeon &&
        Entry.HellParty is { } hell && Snapshot.CurrentMap.Id == hell.MapId && Monsters.HellWaves.Count > 0 &&
        Monsters.Monsters.Where(monster => monster.IsBlocking).All(monster => !Snapshot.LivingMonsters.Contains(monster.ObjectKey));

    public ImmutableArray<DungeonGroundObject> PassiveItems { get; init; } = [];
    // 本次房间访问冻结的AI参数；null不改原生数值，独立对象观测仍可发送仅携场景的私有包。
    public MonsterAiProfile? MonsterAi { get; init; }
    // #233 强袭入场提交时扣除材料变化的背包格，仅入场回执携带。
    public ImmutableArray<InventorySlot> AssaultConsumedSlots { get; init; } = [];
    // 实验性：深渊派对入场票在入场提交时扣掉的背包格与票据（日志 + UPDATE_ITEM_LIST 用）。
    public ImmutableArray<InventorySlot> HellConsumedSlots { get; init; } = [];
    public HellPartyTicket? HellTicket { get; init; }
    public HellPartyTicket? LegendTicket { get; init; }
    // #510/#511 普通入场票在入场提交时扣掉的背包格，与实际扣掉的入场一次性疲劳（无限疲劳时为 0）。
    public ImmutableArray<InventorySlot> EntryItemConsumedSlots { get; init; } = [];
    public ushort EntryFatigueCharged { get; init; }
    // 频道专属城镇入场扣除的 [required item] 背包格；扣次后该行全部副本的日/周剩余（NOTI 550），仅入场回执携带。
    public ImmutableArray<InventorySlot> ChannelConsumedSlots { get; init; } = [];
    public ImmutableArray<CurrencyKind> EntryChangedCurrencies { get; init; } = [];
    public ImmutableArray<DungeonEnterCount> EnterCounts { get; init; } = [];
    public GriefTowerAdmission? GriefAdmission { get; init; }
    public DungeonSpecialEntry? SpecialEntry { get; init; }
    public DazzlementTowerAdmission? Dazzlement { get; init; }
    // 演出返回只确认缓存呈现，不重新执行业务装载或授权基础图演员。
    public Guid ScriptedReturnId { get; init; }
    public bool NavigationReplay { get; init; }
    public uint ClearTimeMilliseconds { get; init; }
    public bool AutomaticRevival { get; init; }
}
internal sealed record FormalDungeonDeathResult(DungeonRunOutcome Outcome, FormalDungeonReceipt? Receipt = null,
    ushort MonsterKey = 0, DungeonExperienceReward? Reward = null, DungeonClearFact? ClearFact = null,
    DungeonGroundObject? Drop = null, byte? PreviousLevel = null)
{
    public Game.Session.TagEvildomClearPreparation? PendingTagEvildomClear { get; init; }
    public Game.Session.ImperialArenaDeathPreparation? PendingImperialArenaDeath { get; init; }
    public Game.ImperialArena.ImperialArenaDeathCommit? ImperialArenaDeath { get; init; }
    public Game.Session.TagEvildomClearReceipt? TagEvildomClear { get; init; }
    public ImmutableArray<DungeonGroundObject> Drops { get; init; } = [];
    public DungeonDirectPickupCommit DirectPickup { get; init; } = DungeonDirectPickupCommit.Empty;
    public uint GainedExperience { get; init; }
    // 同一笔击杀经验里记入熟练度的额度；与 GainedExperience 相加才是本次有效经验。
    public uint ProficiencyGained { get; init; }
    public ImmutableArray<Game.Inventory.Services.EquipmentGrowthChange> EquipmentChanges { get; init; } = [];
    public DungeonPlannedMonster? ReportedTarget { get; init; }
    public bool SpecialChildRetired { get; init; }
    public DungeonElevatorReceipt? Elevator { get; init; }
    public IReadOnlyList<ActiveMainQuest>? PreviousQuests { get; init; }
    public IReadOnlyList<InventorySlot> QuestDropSlots { get; init; } = [];
    public IReadOnlyList<ushort> CulledMonsterKeys { get; init; } = [];
    public DungeonRoomClearFact? RoomClearFact { get; init; }
    public DfoServer.GameWorld.Dungeons.DungeonPoint? BossPositionUpdate { get; init; }
    public BossRushHud? BossRushHud { get; init; }
    public TimeProvider? BattlePassMissionClock { get; init; }
}
internal sealed record EpicPiecePickup(int Index, int PieceId, int Amount, int Balance, long BookRevision);
internal sealed record AccountMaterialPickup(int ItemId, int Amount, int Balance, long WalletRevision);
internal sealed record DungeonPickupCommit(Character Character, bool Duplicate, InventorySlot Destination, long ItemUid,
    bool ProjectionCurrent, CurrencyKind? Currency = null, int Amount = 0)
{
    public EpicPiecePickup? EpicPiece { get; init; }
    public AccountMaterialPickup? AccountMaterial { get; init; }
}
internal sealed record FormalDungeonPickupResult(DungeonRunOutcome Outcome, FormalDungeonReceipt? Receipt = null,
    DungeonGroundObject? Ground = null, DungeonPickupCommit? Commit = null)
{
    public bool CapacityRejected { get; init; }
    public IReadOnlyList<ActiveMainQuest>? PreviousQuests { get; init; }
}
// 结果页杀怪经验明细的一条：客户端用对象键认回自己刷出的演员，再按怪物类型归并显示。
// Experience 是该怪本次的基础经验，不含任何百分比加成。
internal readonly record struct DungeonObjectExperience(uint ObjectKey, uint Experience);

internal sealed record DungeonSettlementPresentation(uint ClearTimeMilliseconds, byte RankPoint, byte RankGrade,
    uint MonsterExperience)
{
    public uint MonsterGrowthContractExperience { get; init; }
    public uint MonsterEquipmentExperience { get; init; }
    public uint MonsterFatigueBurningExperience { get; init; }
    // 本实例实际到账的领主基础经验合计；客户端结果页“领主”行只读该口径，不取对象明细。
    public uint MonsterBossExperience { get; init; }
    // 按击杀顺序累计；为空时客户端只能显示特殊怪物行而显示不出各自经验。
    public IReadOnlyList<DungeonObjectExperience> MonsterExperienceEntries { get; init; } = [];
}
internal sealed record FormalDungeonSettlementResult(DungeonRunOutcome Outcome, FormalDungeonReceipt? Receipt = null,
    DfoServer.GameWorld.Dungeons.DungeonClearExperienceReward? Reward = null, DungeonSettlementPresentation? Presentation = null,
    Character? PreviousCharacter = null)
{
    public int EndlessAdventureAmount { get; init; }
    public uint GuildMileageGuildId { get; init; }
    public uint DailyAttendanceMailId { get; init; }
    public DfoServer.Game.Guild.GuildContributionCommit? GuildContribution { get; init; }
    public DfoServer.Game.Guild.GuildDungeonProgress? GuildDungeon { get; init; }
    public uint GainedExperience { get; init; }
    // 通关结算的熟练度到账额度；展示位统一使用两者之和，不把满级入口线的实际收益显示成 0。
    public uint ProficiencyGained { get; init; }
    public bool LuckyStarGained { get; init; }
    public int LuckyStarTotal { get; init; }
    public DungeonCardReward? Card { get; init; }
    public GriefTowerReward? GriefReward { get; init; }
    public InventoryResult? Inventory { get; init; }
    public uint? MailId { get; init; }
    public DfoServer.Game.Session.AssaultClearResult? AssaultClear { get; init; }
    public DfoServer.Game.Session.CrackClearResult? CrackClear { get; init; }
    // 魔界大战小图：发结果页（34）但不发普通白图的通关翻牌（35），发布端据此收窄。
    public bool EvildomWarClear { get; init; }
    public TimeProvider? BattlePassMissionClock { get; init; }
    public DungeonCardOptions? Options { get; init; }
    public DungeonCardReward? BlackDiamond { get; init; }
    public DfoServer.Game.Adventure.AdventureProgressionCommit? Adventure { get; init; }
    public BossRushClearReward? BossRushReward { get; init; }
}
